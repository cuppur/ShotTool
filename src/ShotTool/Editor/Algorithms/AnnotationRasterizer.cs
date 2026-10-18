// 标注合成（栅格化出口）：入参 = 截图 Bitmap(32bpp) + 标注序列 + 物理像素参数，出参 = 新 Bitmap。
// 该签名在 WinForms(GDI+) 与 WPF 两种栈下都成立 —— 是"不返工"的保险：UI 栈怎么换，导出这条路不变。
//
// z-order 语义：按标注加入顺序绘制（后画的在上）。马赛克特殊 —— 它像素化的是"它之前已经合成的内容"，
// 因此实现上分段处理：先画连续的普通标注，再就地像素化紧随的马赛克块，然后继续。
#nullable enable
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Globalization;
using ShotTool.Core;
using ShotTool.Editor.Model;

namespace ShotTool.Editor.Algorithms
{
    /// <summary>合成参数。</summary>
    public sealed class RenderOptions
    {
        /// <summary>当前显示器 DPI 缩放（1.0 = 100%，本机 225% → 2.25）。</summary>
        public double DpiScale { get; set; } = 1.0;

        /// <summary>true = 跳过马赛克（拖动预览时省一次全图锁定）。</summary>
        public bool SkipMosaic { get; set; }

        /// <summary>是否抗锯齿（导出恒为 true；极端性能场景可关）。</summary>
        public bool Antialias { get; set; } = true;

        public static RenderOptions Create(double dpiScale)
        {
            return new RenderOptions { DpiScale = dpiScale > 0.05 ? dpiScale : 1.0 };
        }
    }

    /// <summary>把标注序列合成到底图上。</summary>
    public static class AnnotationRasterizer
    {
        /// <summary>
        /// 合成到**新**位图（不修改 snapshot）。返回的位图恒为 32bppPArgb，尺寸与 snapshot 相同。
        /// 调用方负责 Dispose 返回值。
        /// </summary>
        public static Bitmap Compose(Bitmap snapshot, IReadOnlyList<Annotation> annotations, RenderOptions? options = null)
        {
            if (snapshot == null)
            {
                throw new ArgumentNullException(nameof(snapshot));
            }

            Bitmap target = CloneToParGB(snapshot);
            Draw(target, annotations, options);
            return target;
        }

        /// <summary>
        /// 就地合成到 target（调用方需保证 target 已是可写 32bpp 位图，且是底图的副本）。
        /// annotations 为 null/空时直接返回。
        /// </summary>
        public static void Draw(Bitmap target, IReadOnlyList<Annotation> annotations, RenderOptions? options = null)
        {
            if (target == null)
            {
                throw new ArgumentNullException(nameof(target));
            }

            if (annotations == null || annotations.Count == 0)
            {
                return;
            }

            RenderOptions opt = options ?? RenderOptions.Create(1.0);
            int index = 0;
            while (index < annotations.Count)
            {
                int batchStart = index;
                while (index < annotations.Count && !(annotations[index] is MosaicAnnotation))
                {
                    index++;
                }

                if (index > batchStart)
                {
                    DrawBatch(target, annotations, batchStart, index - batchStart, opt);
                }

                while (index < annotations.Count && annotations[index] is MosaicAnnotation)
                {
                    MosaicAnnotation mosaic = (MosaicAnnotation)annotations[index];
                    if (!opt.SkipMosaic && mosaic.Blocks.Count > 0)
                    {
                        MosaicProcessor.Apply(target, mosaic.Blocks);
                    }

                    index++;
                }
            }
        }

        private static void DrawBatch(
            Bitmap target,
            IReadOnlyList<Annotation> annotations,
            int start,
            int count,
            RenderOptions opt)
        {
            using (Graphics graphics = Graphics.FromImage(target))
            {
                graphics.SmoothingMode = opt.Antialias ? SmoothingMode.AntiAlias : SmoothingMode.None;
                graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
                graphics.InterpolationMode = InterpolationMode.NearestNeighbor;
                graphics.CompositingMode = CompositingMode.SourceOver;
                graphics.CompositingQuality = CompositingQuality.HighSpeed;
                graphics.TextRenderingHint = opt.Antialias
                    ? TextRenderingHint.AntiAlias
                    : TextRenderingHint.SystemDefault;

                for (int i = start; i < start + count; i++)
                {
                    DrawOne(graphics, annotations[i], opt);
                }
            }
        }

        /// <summary>把单个标注绘制到任意 Graphics（overlay 预览与导出共用同一套绘制代码）。</summary>
        public static void DrawAnnotation(Graphics graphics, Annotation annotation, double dpiScale)
        {
            if (graphics == null || annotation == null || annotation.Kind == AnnotationKind.Mosaic)
            {
                return;
            }

            DrawOne(graphics, annotation, RenderOptions.Create(dpiScale));
        }

        /// <summary>绘制一组标注（跳过马赛克：马赛克需要位图取样，由调用方处理）。</summary>
        public static void DrawAnnotations(Graphics graphics, IReadOnlyList<Annotation> annotations, double dpiScale)
        {
            if (graphics == null || annotations == null)
            {
                return;
            }

            for (int i = 0; i < annotations.Count; i++)
            {
                DrawAnnotation(graphics, annotations[i], dpiScale);
            }
        }

        private static void DrawOne(Graphics graphics, Annotation annotation, RenderOptions opt)
        {
            switch (annotation.Kind)
            {
                case AnnotationKind.Rectangle:
                    DrawRect(graphics, (RectAnnotation)annotation, opt);
                    break;
                case AnnotationKind.Ellipse:
                    DrawEllipse(graphics, (EllipseAnnotation)annotation, opt);
                    break;
                case AnnotationKind.Arrow:
                    DrawArrow(graphics, (ArrowAnnotation)annotation, opt);
                    break;
                case AnnotationKind.Pen:
                    DrawPen(graphics, (PenAnnotation)annotation, opt);
                    break;
                case AnnotationKind.Text:
                    DrawText(graphics, (TextAnnotation)annotation);
                    break;
                case AnnotationKind.Number:
                    DrawNumber(graphics, (NumberAnnotation)annotation);
                    break;
                case AnnotationKind.Mosaic:
                    // 马赛克由 Draw() 分段处理（需要锁定像素，不能与 Graphics 同时进行）
                    break;
            }
        }

        private static void DrawRect(Graphics graphics, RectAnnotation annotation, RenderOptions opt)
        {
            Color color = ToColor(annotation.Style.Argb);
            RectangleF rect = ToRectangleF(annotation.VisualBounds);
            using (SolidBrush brush = new SolidBrush(color))
            {
                if (annotation.Filled)
                {
                    graphics.FillRectangle(brush, rect);
                    return;
                }

                double width = annotation.Style.Brush.ToPhysLineWidth(opt.DpiScale);
                using (Pen pen = CreatePen(color, width))
                {
                    graphics.DrawRectangle(pen, rect.X, rect.Y, rect.Width, rect.Height);
                }
            }
        }

        private static void DrawEllipse(Graphics graphics, EllipseAnnotation annotation, RenderOptions opt)
        {
            Color color = ToColor(annotation.Style.Argb);
            RectangleF rect = ToRectangleF(annotation.VisualBounds);
            using (SolidBrush brush = new SolidBrush(color))
            {
                if (annotation.Filled)
                {
                    graphics.FillEllipse(brush, rect);
                    return;
                }

                double width = annotation.Style.Brush.ToPhysLineWidth(opt.DpiScale);
                using (Pen pen = CreatePen(color, width))
                {
                    graphics.DrawEllipse(pen, rect);
                }
            }
        }

        private static void DrawArrow(Graphics graphics, ArrowAnnotation annotation, RenderOptions opt)
        {
            Color color = ToColor(annotation.Style.Argb);
            double width = annotation.Style.Brush.ToPhysLineWidth(opt.DpiScale);
            double head = annotation.HeadLengthPhys > 0
                ? annotation.HeadLengthPhys
                : EditorDefaults.ResolveArrowHeadLength(width, opt.DpiScale);

            PointF from = new PointF((float)annotation.From.X, (float)annotation.From.Y);
            PointF to = new PointF((float)annotation.To.X, (float)annotation.To.Y);

            double angle = Math.Atan2(annotation.To.Y - annotation.From.Y, annotation.To.X - annotation.From.X);
            const double spread = Math.PI / 7.0;

            PointF h1 = new PointF(
                (float)(annotation.To.X - head * Math.Cos(angle - spread)),
                (float)(annotation.To.Y - head * Math.Sin(angle - spread)));
            PointF h2 = new PointF(
                (float)(annotation.To.X - head * Math.Cos(angle + spread)),
                (float)(annotation.To.Y - head * Math.Sin(angle + spread)));

            using (Pen pen = CreatePen(color, width))
            {
                graphics.DrawLine(pen, from, to);
                graphics.DrawLine(pen, to, h1);
                graphics.DrawLine(pen, to, h2);
            }
        }

        private static void DrawPen(Graphics graphics, PenAnnotation annotation, RenderOptions opt)
        {
            Color color = ToColor(annotation.Style.Argb);
            double width = annotation.Style.Brush.ToPhysLineWidth(opt.DpiScale);
            List<PhysPointF> flat = PenSmoother.Flatten(annotation.Points, 0);

            if (flat.Count == 0)
            {
                return;
            }

            using (SolidBrush brush = new SolidBrush(color))
            using (Pen pen = CreatePen(color, width))
            {
                if (flat.Count == 1)
                {
                    float r = (float)(width * 0.5);
                    graphics.FillEllipse(brush, (float)flat[0].X - r, (float)flat[0].Y - r, r * 2, r * 2);
                    return;
                }

                PointF[] points = new PointF[flat.Count];
                for (int i = 0; i < flat.Count; i++)
                {
                    points[i] = new PointF((float)flat[i].X, (float)flat[i].Y);
                }

                graphics.DrawLines(pen, points);
            }
        }

        private static void DrawText(Graphics graphics, TextAnnotation annotation)
        {
            if (annotation.IsEmptyText)
            {
                return;
            }

            if (!annotation.HasMeasuredBounds)
            {
                TextMeasurer.Fill(annotation);
            }

            Color color = ToColor(annotation.Style.Argb);
            PhysRectF bounds = annotation.VisualBounds;
            RectangleF layout = new RectangleF(
                (float)bounds.X,
                (float)bounds.Y,
                (float)Math.Max(bounds.Width, 1) + 8f,
                (float)Math.Max(bounds.Height, 1) + 8f);

            using (Font font = TextMeasurer.CreateFont(annotation.FontFamily, annotation.FontSizePhys))
            using (SolidBrush brush = new SolidBrush(color))
            using (StringFormat format = TextMeasurer.CreateFormat())
            {
                graphics.DrawString(annotation.Text, font, brush, layout, format);
            }
        }

        private static void DrawNumber(Graphics graphics, NumberAnnotation annotation)
        {
            Color fill = ToColor(annotation.Style.Argb);
            Color text = ToColor(annotation.Style.ContrastingArgb());
            double radius = annotation.RadiusPhys;
            RectangleF ellipse = new RectangleF(
                (float)(annotation.Center.X - radius),
                (float)(annotation.Center.Y - radius),
                (float)(radius * 2),
                (float)(radius * 2));

            using (SolidBrush brush = new SolidBrush(fill))
            {
                graphics.FillEllipse(brush, ellipse);
            }

            using (Font font = TextMeasurer.CreateFont(TextAnnotation.DefaultFontFamily, radius * 1.15))
            using (SolidBrush brush = new SolidBrush(text))
            using (StringFormat format = new StringFormat())
            {
                format.Alignment = StringAlignment.Center;
                format.LineAlignment = StringAlignment.Center;
                string label = annotation.Index.ToString(CultureInfo.InvariantCulture);
                graphics.DrawString(label, font, brush, ellipse, format);
            }
        }

        private static Pen CreatePen(Color color, double widthPhys)
        {
            float width = (float)(widthPhys < 1.0 ? 1.0 : widthPhys);
            Pen pen = new Pen(color, width);
            pen.StartCap = LineCap.Round;
            pen.EndCap = LineCap.Round;
            pen.LineJoin = LineJoin.Round;
            pen.Alignment = PenAlignment.Center;
            return pen;
        }

        private static RectangleF ToRectangleF(PhysRectF rect)
        {
            return new RectangleF((float)rect.X, (float)rect.Y, (float)rect.Width, (float)rect.Height);
        }

        private static Color ToColor(uint argb)
        {
            return Color.FromArgb(unchecked((int)argb));
        }

        /// <summary>复制为可写 32bppPArgb 位图（不修改源位图）。</summary>
        private static Bitmap CloneToParGB(Bitmap source)
        {
            Rectangle rect = new Rectangle(0, 0, source.Width, source.Height);
            if (source.PixelFormat == PixelFormat.Format32bppPArgb)
            {
                return (Bitmap)source.Clone();
            }

            object clone = source.Clone(rect, PixelFormat.Format32bppPArgb);
            return (Bitmap)clone;
        }
    }
}
