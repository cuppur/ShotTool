// 标注叠加绘制器：实现 Core.IAnnotationOverlayPainter。
// overlay 的 OnPaint 在画完「帧 → 遮罩 → 选区边框 → 扩展叠加层」后按 ZOrder 调用本 painter。
//
// 坐标系：AnnotationPaintContext.Selection 是虚拟桌面物理像素；标注模型用**选区局部**物理像素，
// 因此绘制前 translate(Selection.Left, Selection.Top) 并 clip 到选区。
// 马赛克：overlay 上无法就地像素化整屏位图，改为按块从抓屏帧取样中心像素后 FillRectangle —— 视觉一致且极快。
#nullable enable
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using ShotTool.Core;
using ShotTool.Editor.Algorithms;
using ShotTool.Editor.Model;

namespace ShotTool.Editor.Painters
{
    /// <summary>把标注模型画到 overlay 上（含拖拽预览标注）。</summary>
    public sealed class AnnotationPainter : IAnnotationOverlayPainter, IDisposable
    {
        private readonly Func<IReadOnlyList<Annotation>> _itemsProvider;
        private readonly Func<Annotation?> _previewProvider;
        private readonly Func<Bitmap?> _frameProvider;

        private FramePixelCache? _cache;
        private Bitmap? _cacheFrame;

        /// <param name="itemsProvider">已提交标注（按绘制顺序）。</param>
        /// <param name="previewProvider">正在拖拽的预览标注；无则返回 null。</param>
        /// <param name="frameProvider">当前抓屏帧（用于马赛克取样）；可为 null。</param>
        public AnnotationPainter(
            Func<IReadOnlyList<Annotation>> itemsProvider,
            Func<Annotation?>? previewProvider = null,
            Func<Bitmap?>? frameProvider = null)
        {
            _itemsProvider = itemsProvider ?? throw new ArgumentNullException(nameof(itemsProvider));
            _previewProvider = previewProvider ?? (() => null);
            _frameProvider = frameProvider ?? (() => null);
        }

        /// <summary>标注应在选区边框之上、提示层之下（见 architect 的固定绘制顺序 ④⑤）。</summary>
        public int ZOrder
        {
            get { return 50; }
        }

        public bool ShouldPaint(AnnotationPaintContext context)
        {
            return context != null && !context.Selection.IsEmpty;
        }

        public void Paint(Graphics graphics, AnnotationPaintContext context)
        {
            if (graphics == null || context == null)
            {
                return;
            }

            PhysRect selection = context.Selection;
            if (selection.IsEmpty)
            {
                return;
            }

            double dpiScale = context.Dpi > 0 ? context.Dpi / DpiHelper.ReferenceDpi : 1.0;
            GraphicsState state = graphics.Save();
            try
            {
                graphics.SetClip(selection.ToRectangle());
                graphics.TranslateTransform(selection.Left, selection.Top);

                IReadOnlyList<Annotation> items = _itemsProvider();
                if (items != null && items.Count > 0)
                {
                    // 先画非马赛克与马赛克按原顺序（z-order 正确性依赖顺序；overlay 上两者可混绘，
                    // 因为马赛克是"取样块色填充"，不会破坏之前绘制的标注像素）
                    for (int i = 0; i < items.Count; i++)
                    {
                        DrawOne(graphics, items[i], context, selection, dpiScale, context.IsPreview);
                    }
                }

                Annotation? preview = _previewProvider();
                if (preview != null)
                {
                    DrawOne(graphics, preview, context, selection, dpiScale, true);
                }
            }
            finally
            {
                graphics.Restore(state);
            }
        }

        public void Dispose()
        {
            _cache?.Dispose();
            _cache = null;
            _cacheFrame = null;
        }

        /// <summary>帧对象变化时（新会话）调用，丢弃旧的取样缓存。</summary>
        public void ResetFrame()
        {
            _cache?.Dispose();
            _cache = null;
            _cacheFrame = null;
        }

        private void DrawOne(
            Graphics graphics,
            Annotation annotation,
            AnnotationPaintContext context,
            PhysRect selection,
            double dpiScale,
            bool isPreview)
        {
            if (annotation == null || annotation.VisualBounds.IsEmpty)
            {
                return;
            }

            if (annotation.Kind != AnnotationKind.Mosaic)
            {
                if (isPreview)
                {
                    // 预览态可略降低不透明度（Core 注释建议），这里保持一致绘制以贴合原生手感
                }

                AnnotationRasterizer.DrawAnnotation(graphics, annotation, dpiScale);
                return;
            }

            DrawMosaic(graphics, (MosaicAnnotation)annotation, context, selection, dpiScale);
        }

        private void DrawMosaic(
            Graphics graphics,
            MosaicAnnotation mosaic,
            AnnotationPaintContext context,
            PhysRect selection,
            double dpiScale)
        {
            FramePixelCache? cache = GetCache();
            IReadOnlyList<PhysRectF> blocks = mosaic.Blocks;
            if (blocks.Count == 0)
            {
                return;
            }

            using (SolidBrush fallback = new SolidBrush(Color.FromArgb(140, 120, 120, 120)))
            {
                for (int i = 0; i < blocks.Count; i++)
                {
                    PhysRectF block = blocks[i];
                    // 选区局部 → 虚拟桌面坐标 → 帧位图坐标
                    int frameX = (int)Math.Round(selection.Left + block.Left - context.BitmapOrigin.X);
                    int frameY = (int)Math.Round(selection.Top + block.Top - context.BitmapOrigin.Y);

                    // 与 MosaicProcessor 保持一致：块内 3×3 平均取色（overlay 预览必须与导出结果同色）
                    Color color = SampleBlockAverage(cache, frameX, frameY, (int)Math.Round(block.Width), (int)Math.Round(block.Height));
                    bool hasColor = color != Color.Empty;

                    RectangleF rect = new RectangleF(
                        (float)block.Left,
                        (float)block.Top,
                        (float)block.Width,
                        (float)block.Height);

                    if (hasColor)
                    {
                        using (SolidBrush brush = new SolidBrush(color))
                        {
                            graphics.FillRectangle(brush, rect);
                        }
                    }
                    else
                    {
                        graphics.FillRectangle(fallback, rect);
                    }
                }
            }
        }

        /// <summary>块内 3×3 平均取色（与 MosaicProcessor 的取色规则一致，保证预览与导出同色）。</summary>
        private static Color SampleBlockAverage(FramePixelCache? cache, int left, int top, int width, int height)
        {
            if (cache == null || width <= 0 || height <= 0)
            {
                return Color.Empty;
            }

            int pad = Math.Max(1, Math.Min(width, height) / 4);
            int centerX = left + width / 2;
            int centerY = top + height / 2;
            int accR = 0;
            int accG = 0;
            int accB = 0;
            int accA = 0;
            int count = 0;

            for (int oy = -1; oy <= 1; oy++)
            {
                for (int ox = -1; ox <= 1; ox++)
                {
                    Color sample;
                    if (!cache.TryGet(centerX + ox * pad, centerY + oy * pad, out sample))
                    {
                        continue;
                    }

                    accR += sample.R;
                    accG += sample.G;
                    accB += sample.B;
                    accA += sample.A;
                    count++;
                }
            }

            if (count == 0)
            {
                return Color.Empty;
            }

            return Color.FromArgb(accA / count, accR / count, accG / count, accB / count);
        }

        private FramePixelCache? GetCache()
        {
            Bitmap? frame = _frameProvider();
            if (frame == null)
            {
                return null;
            }

            if (_cache != null && ReferenceEquals(_cacheFrame, frame))
            {
                return _cache;
            }

            _cache?.Dispose();
            _cacheFrame = frame;
            _cache = new FramePixelCache(frame);
            return _cache;
        }
    }
}
