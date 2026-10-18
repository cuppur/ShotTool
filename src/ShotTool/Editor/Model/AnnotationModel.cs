// Editor 标注模型：实现 ShotTool.Core.IAnnotation，但几何/枚举本体归 Editor（Core 只定义"别人怎么看标注"）。
//
// 坐标约定（与 Core/Geometry.cs 一致）：
//   * 标注内部用亚像素 double（PhysPointF / Core.PhysRectF）保留精度；
//   * 对外契约 IAnnotation.VisualBounds 也用它；IAnnotation.Bounds（int）由 Core 默认实现派生；
//   * 导出、裁剪、命中边界一律走 ToCoreRect() → Core.PhysRect(int)。
#nullable enable
using System;
using System.Collections.Generic;
using ShotTool.Core;

namespace ShotTool.Editor.Model
{
    /// <summary>笔触档位（对应微信截图资源 size_small / size_middle / size_big / size_fill 四档）。</summary>
    public enum BrushLevel
    {
        Thin = 0,
        Middle = 1,
        Thick = 2,
        Fill = 3,
    }

    /// <summary>马赛克模式：块状矩形 / 扫描式（对应原生资源 mosaic_saomiao 的"扫描"语义）。</summary>
    public enum MosaicMode
    {
        Block = 0,
        Sweep = 1,
    }

    /// <summary>
    /// 标注种类（Editor 内部枚举；Core 侧不需要它 —— Core 用 <see cref="IAnnotation.KindName"/> 字符串，
    /// 因此这里不构成"第二套枚举"）。
    /// </summary>
    public enum AnnotationKind
    {
        Rectangle = 0,
        Ellipse = 1,
        Arrow = 2,
        Pen = 3,
        Mosaic = 4,
        Text = 5,
        Number = 6,
    }

    /// <summary>笔触档位的像素换算（逻辑值 × dpiScale，最小 1 物理像素）。</summary>
    public static class BrushLevelExtensions
    {
        /// <summary>档位对应的逻辑线宽（96 DPI 基准）。Fill 档为填充语义，线宽仅用于描边。</summary>
        public static double ToLogicalLineWidth(this BrushLevel level)
        {
            switch (level)
            {
                case BrushLevel.Thin:
                    return 1.5;
                case BrushLevel.Middle:
                    return 3.0;
                case BrushLevel.Thick:
                    return 6.0;
                case BrushLevel.Fill:
                    return 3.0;
                default:
                    return 3.0;
            }
        }

        /// <summary>物理像素线宽。</summary>
        public static double ToPhysLineWidth(this BrushLevel level, double dpiScale)
        {
            double scale = dpiScale > 0 ? dpiScale : 1.0;
            double width = level.ToLogicalLineWidth() * scale;
            return width < 1.0 ? 1.0 : width;
        }
    }

    /// <summary>标注样式（颜色 + 笔触档位）。不可变。</summary>
    public sealed class AnnotationStyle
    {
        private static readonly AnnotationStyle s_default =
            new AnnotationStyle(unchecked((uint)0xFFF5222D), BrushLevel.Middle);

        public AnnotationStyle(uint argb, BrushLevel brush)
        {
            Argb = argb;
            Brush = brush;
        }

        /// <summary>ARGB 颜色（A 在高位）。</summary>
        public uint Argb { get; private set; }

        public BrushLevel Brush { get; private set; }

        /// <summary>默认：红色 / 中笔触（对应微信 6 色板默认观感）。</summary>
        public static AnnotationStyle Default
        {
            get { return s_default; }
        }

        public byte Alpha
        {
            get { return (byte)((Argb >> 24) & 0xFF); }
        }

        public byte Red
        {
            get { return (byte)((Argb >> 16) & 0xFF); }
        }

        public byte Green
        {
            get { return (byte)((Argb >> 8) & 0xFF); }
        }

        public byte Blue
        {
            get { return (byte)(Argb & 0xFF); }
        }

        public AnnotationStyle WithColor(uint argb)
        {
            return new AnnotationStyle(argb, Brush);
        }

        public AnnotationStyle WithBrush(BrushLevel brush)
        {
            return new AnnotationStyle(Argb, brush);
        }

        /// <summary>该颜色上的对比色（序号数字）：亮度 &gt; 0.6 用黑，否则用白。</summary>
        public uint ContrastingArgb()
        {
            double luma = (0.299 * Red + 0.587 * Green + 0.114 * Blue) / 255.0;
            return luma > 0.6 ? 0xFF000000u : 0xFFFFFFFFu;
        }
    }

    /// <summary>
    /// 标注基类：实现 <see cref="IAnnotation"/>（Core 契约），几何用亚像素 double。
    /// <para>
    /// PixelScale 由 Editor 在创建标注时设置（= 当前显示器 dpiScale / 96 × 96 = dpiScale），
    /// 只用于把"笔触档位"换算成物理线宽以满足 <see cref="StrokeWidthPx"/> 契约。
    /// </para>
    /// </summary>
    public abstract class Annotation : IAnnotation
    {
        protected Annotation(AnnotationStyle? style)
        {
            Style = style ?? AnnotationStyle.Default;
        }

        public AnnotationStyle Style { get; private set; }

        /// <summary>当前 DPI 缩放（1.0 = 100%，本机 225% → 2.25）。</summary>
        public double PixelScale { get; private set; } = 1.0;

        public abstract AnnotationKind Kind { get; }

        // ---- IAnnotation 契约实现 ----

        /// <summary>
        /// 诊断用类型名（Core 契约的**不透明标签**，必须稳定）：
        /// rect / ellipse / arrow / pen / mosaic / text / number。
        /// </summary>
        public string KindName
        {
            get
            {
                switch (Kind)
                {
                    case AnnotationKind.Rectangle:
                        return "rect";
                    case AnnotationKind.Ellipse:
                        return "ellipse";
                    case AnnotationKind.Arrow:
                        return "arrow";
                    case AnnotationKind.Pen:
                        return "pen";
                    case AnnotationKind.Mosaic:
                        return "mosaic";
                    case AnnotationKind.Text:
                        return "text";
                    case AnnotationKind.Number:
                        return "number";
                    default:
                        return "annotation";
                }
            }
        }

        /// <summary>实际绘制颜色（Core 契约）。</summary>
        public System.Drawing.Color Color
        {
            get { return System.Drawing.Color.FromArgb(unchecked((int)Style.Argb)); }
        }

        /// <summary>实际线宽（物理像素；填充型返回 0）。</summary>
        public double StrokeWidthPx
        {
            get
            {
                return Style.Brush == BrushLevel.Fill ? 0.0 : Style.Brush.ToPhysLineWidth(PixelScale);
            }
        }

        /// <summary>亚像素包围盒（Core 契约；不含笔宽外扩）。</summary>
        public abstract PhysRectF VisualBounds { get; }

        /// <summary>整数包围盒（Core 默认实现即可，此处显式提供便于日志/裁剪直接调用）。</summary>
        public PhysRect BoundsRect
        {
            get { return VisualBounds.ToCoreRect(); }
        }

        // ---- Editor 内部 ----

        /// <summary>设置 DPI 缩放（创建标注时由 Editor 调用）。</summary>
        public void SetPixelScale(double dpiScale)
        {
            PixelScale = dpiScale > 0.05 ? dpiScale : 1.0;
        }

        /// <summary>命中测试（物理像素；tolerance 为点击容差）。</summary>
        public abstract bool HitTest(PhysPointF point, double tolerance);

        /// <summary>替换样式（切换颜色/笔触时对"选中标注"生效；P0 默认只影响后续绘制）。</summary>
        public void Restyle(AnnotationStyle style)
        {
            if (style != null)
            {
                Style = style;
            }
        }
    }

    /// <summary>矩形标注（Fill 档或 Filled=true 时为填充矩形）。</summary>
    public sealed class RectAnnotation : Annotation
    {
        private readonly PhysRectF _bounds;

        public RectAnnotation(PhysRectF bounds, AnnotationStyle? style = null, bool filled = false)
            : base(style)
        {
            _bounds = bounds.Normalized();
            Filled = filled || Style.Brush == BrushLevel.Fill;
        }

        public bool Filled { get; private set; }

        public override AnnotationKind Kind
        {
            get { return AnnotationKind.Rectangle; }
        }

        public override PhysRectF VisualBounds
        {
            get { return _bounds; }
        }

        public override bool HitTest(PhysPointF point, double tolerance)
        {
            if (Filled)
            {
                return _bounds.Inflated(tolerance).ContainsPoint(point);
            }

            if (!_bounds.Inflated(tolerance).ContainsPoint(point))
            {
                return false;
            }

            PhysRectF inner = _bounds.Inflated(-tolerance);
            return inner.IsEmpty || !inner.ContainsPoint(point);
        }
    }

    /// <summary>椭圆标注。</summary>
    public sealed class EllipseAnnotation : Annotation
    {
        private readonly PhysRectF _bounds;

        public EllipseAnnotation(PhysRectF bounds, AnnotationStyle? style = null, bool filled = false)
            : base(style)
        {
            _bounds = bounds.Normalized();
            Filled = filled || Style.Brush == BrushLevel.Fill;
        }

        public bool Filled { get; private set; }

        public override AnnotationKind Kind
        {
            get { return AnnotationKind.Ellipse; }
        }

        public override PhysRectF VisualBounds
        {
            get { return _bounds; }
        }

        public override bool HitTest(PhysPointF point, double tolerance)
        {
            double rx = _bounds.Width * 0.5;
            double ry = _bounds.Height * 0.5;
            if (rx <= 0.5 || ry <= 0.5)
            {
                return _bounds.Inflated(tolerance).ContainsPoint(point);
            }

            double nx = (point.X - _bounds.CenterX) / rx;
            double ny = (point.Y - _bounds.CenterY) / ry;
            double d = nx * nx + ny * ny;
            double ring = tolerance / Math.Min(rx, ry);
            if (Filled)
            {
                return d <= 1.0 + ring;
            }

            return d <= 1.0 + ring && d >= 1.0 - ring - 0.05;
        }
    }

    /// <summary>箭头标注（直线 + 箭头头部）。</summary>
    public sealed class ArrowAnnotation : Annotation
    {
        public ArrowAnnotation(PhysPointF from, PhysPointF to, AnnotationStyle? style = null, double headLengthPhys = 0)
            : base(style)
        {
            From = from;
            To = to;
            HeadLengthPhys = headLengthPhys > 0 ? headLengthPhys : 0;
        }

        public PhysPointF From { get; private set; }

        public PhysPointF To { get; private set; }

        /// <summary>箭头头长（物理像素）；0 表示由渲染层按线宽推导。</summary>
        public double HeadLengthPhys { get; private set; }

        public override AnnotationKind Kind
        {
            get { return AnnotationKind.Arrow; }
        }

        public override PhysRectF VisualBounds
        {
            get
            {
                PhysRectF line = EditorGeometry.FromEdges(From.X, From.Y, To.X, To.Y);
                double pad = Math.Max(HeadLengthPhys, Style.Brush.ToPhysLineWidth(PixelScale)) + 2;
                return line.Inflated(pad);
            }
        }

        public override bool HitTest(PhysPointF point, double tolerance)
        {
            return EditorGeometry.DistanceToSegment(point, From, To)
                <= tolerance + Style.Brush.ToPhysLineWidth(PixelScale) * 0.5;
        }
    }

    /// <summary>画笔轨迹标注（自由笔）。</summary>
    public sealed class PenAnnotation : Annotation
    {
        private readonly List<PhysPointF> _points;
        private PhysRectF _bounds;

        public PenAnnotation(IReadOnlyList<PhysPointF> points, AnnotationStyle? style = null)
            : base(style)
        {
            _points = new List<PhysPointF>(points != null ? points.Count : 0);
            if (points != null)
            {
                for (int i = 0; i < points.Count; i++)
                {
                    if (points[i].IsFinite)
                    {
                        _points.Add(points[i]);
                    }
                }
            }

            _bounds = ComputeBounds();
        }

        public IReadOnlyList<PhysPointF> Points
        {
            get { return _points; }
        }

        public override AnnotationKind Kind
        {
            get { return AnnotationKind.Pen; }
        }

        public override PhysRectF VisualBounds
        {
            get { return _bounds; }
        }

        public override bool HitTest(PhysPointF point, double tolerance)
        {
            double max = tolerance + Style.Brush.ToPhysLineWidth(PixelScale) * 0.5;
            if (_points.Count == 1)
            {
                return point.DistanceTo(_points[0]) <= max;
            }

            for (int i = 1; i < _points.Count; i++)
            {
                if (EditorGeometry.DistanceToSegment(point, _points[i - 1], _points[i]) <= max)
                {
                    return true;
                }
            }

            return false;
        }

        private PhysRectF ComputeBounds()
        {
            if (_points.Count == 0)
            {
                return PhysRectF.Empty;
            }

            double minX = _points[0].X;
            double minY = _points[0].Y;
            double maxX = minX;
            double maxY = minY;
            for (int i = 1; i < _points.Count; i++)
            {
                PhysPointF p = _points[i];
                if (p.X < minX)
                {
                    minX = p.X;
                }

                if (p.Y < minY)
                {
                    minY = p.Y;
                }

                if (p.X > maxX)
                {
                    maxX = p.X;
                }

                if (p.Y > maxY)
                {
                    maxY = p.Y;
                }
            }

            double pad = Style.Brush.ToPhysLineWidth(PixelScale) * 0.5 + 1;
            return new PhysRectF(minX, minY, maxX - minX, maxY - minY).Inflated(pad);
        }
    }

    /// <summary>
    /// 马赛克标注：Blocks 为已量化的块（对齐 BlockSize 网格），渲染层只需逐块像素化。
    /// Block 模式额外记录原始拖拽矩形（DragRegion），Sweep 模式它为 Empty。
    /// </summary>
    public sealed class MosaicAnnotation : Annotation
    {
        private readonly List<PhysRectF> _blocks;
        private PhysRectF _bounds;

        public MosaicAnnotation(
            IReadOnlyList<PhysRectF> blocks,
            int blockSize,
            MosaicMode mode,
            AnnotationStyle? style = null,
            PhysRectF dragRegion = default(PhysRectF))
            : base(style)
        {
            _blocks = new List<PhysRectF>(blocks != null ? blocks.Count : 0);
            if (blocks != null)
            {
                for (int i = 0; i < blocks.Count; i++)
                {
                    if (!blocks[i].IsEmpty)
                    {
                        _blocks.Add(blocks[i]);
                    }
                }
            }

            BlockSize = blockSize > 1 ? blockSize : 2;
            Mode = mode;
            DragRegion = dragRegion;
            _bounds = ComputeBounds();
        }

        public IReadOnlyList<PhysRectF> Blocks
        {
            get { return _blocks; }
        }

        public int BlockSize { get; private set; }

        public MosaicMode Mode { get; private set; }

        public PhysRectF DragRegion { get; private set; }

        public override AnnotationKind Kind
        {
            get { return AnnotationKind.Mosaic; }
        }

        public override PhysRectF VisualBounds
        {
            get { return _bounds; }
        }

        public override bool HitTest(PhysPointF point, double tolerance)
        {
            for (int i = 0; i < _blocks.Count; i++)
            {
                if (_blocks[i].Inflated(tolerance * 0.5).ContainsPoint(point))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>追加扫描式的新块（拖动过程中增量提交，避免整段重算）。</summary>
        public void AppendBlocks(IReadOnlyList<PhysRectF> blocks)
        {
            if (blocks == null || blocks.Count == 0)
            {
                return;
            }

            for (int i = 0; i < blocks.Count; i++)
            {
                if (!blocks[i].IsEmpty)
                {
                    _blocks.Add(blocks[i]);
                }
            }

            _bounds = ComputeBounds();
        }

        private PhysRectF ComputeBounds()
        {
            PhysRectF acc = PhysRectF.Empty;
            for (int i = 0; i < _blocks.Count; i++)
            {
                acc = EditorGeometry.Union(acc, _blocks[i]);
            }

            return acc;
        }
    }

    /// <summary>
    /// 文字标注。绘制/测量依赖渲染栈，因此实测包围盒由 <c>TextMeasurer</c> 测量后回填
    /// （<see cref="SetMeasuredBounds"/>）；未测量前用估算值，仅用于命中与粗略定位。
    /// </summary>
    public sealed class TextAnnotation : Annotation
    {
        private PhysRectF _measuredBounds;

        public TextAnnotation(
            PhysPointF topLeft,
            string text,
            double fontSizePhys,
            AnnotationStyle? style = null,
            string? fontFamily = null)
            : base(style)
        {
            TopLeft = topLeft;
            Text = text ?? string.Empty;
            FontSizePhys = fontSizePhys > 1 ? fontSizePhys : 12;
            FontFamily = string.IsNullOrEmpty(fontFamily) ? DefaultFontFamily : fontFamily;
        }

        /// <summary>首选字体串（中英混排均可）：微软雅黑 UI → 微软雅黑 → Segoe UI。</summary>
        public const string DefaultFontFamily = "Microsoft YaHei UI, Microsoft YaHei, Segoe UI";

        public PhysPointF TopLeft { get; private set; }

        public string Text { get; private set; }

        public double FontSizePhys { get; private set; }

        public string FontFamily { get; private set; }

        public override AnnotationKind Kind
        {
            get { return AnnotationKind.Text; }
        }

        public override PhysRectF VisualBounds
        {
            get
            {
                if (!_measuredBounds.IsEmpty)
                {
                    return _measuredBounds;
                }

                return EstimateBounds();
            }
        }

        public bool HasMeasuredBounds
        {
            get { return !_measuredBounds.IsEmpty; }
        }

        /// <summary>由渲染层/测量器回填实测包围盒（物理像素）。</summary>
        public void SetMeasuredBounds(PhysRectF bounds)
        {
            _measuredBounds = bounds.Normalized();
        }

        public void UpdateText(string text)
        {
            Text = text ?? string.Empty;
            _measuredBounds = PhysRectF.Empty;
        }

        public bool IsEmptyText
        {
            get { return string.IsNullOrEmpty(Text); }
        }

        public override bool HitTest(PhysPointF point, double tolerance)
        {
            return VisualBounds.Inflated(tolerance).ContainsPoint(point);
        }

        private PhysRectF EstimateBounds()
        {
            int lines = 1;
            int maxLineChars = 0;
            int current = 0;
            for (int i = 0; i < Text.Length; i++)
            {
                char c = Text[i];
                if (c == '\n')
                {
                    lines++;
                    if (current > maxLineChars)
                    {
                        maxLineChars = current;
                    }

                    current = 0;
                }
                else if (c == '\r')
                {
                    continue;
                }
                else
                {
                    current++;
                }
            }

            if (current > maxLineChars)
            {
                maxLineChars = current;
            }

            double width = Math.Max(FontSizePhys, maxLineChars * FontSizePhys * 0.62);
            double height = lines * FontSizePhys * 1.32;
            return new PhysRectF(TopLeft.X, TopLeft.Y, width, height);
        }
    }

    /// <summary>序号标注（圆底 + 数字，连续递增）。</summary>
    public sealed class NumberAnnotation : Annotation
    {
        public NumberAnnotation(PhysPointF center, int index, double radiusPhys, AnnotationStyle? style = null)
            : base(style)
        {
            Center = center;
            Index = index < 1 ? 1 : index;
            RadiusPhys = radiusPhys > 2 ? radiusPhys : 10;
        }

        public PhysPointF Center { get; private set; }

        public int Index { get; private set; }

        public double RadiusPhys { get; private set; }

        public override AnnotationKind Kind
        {
            get { return AnnotationKind.Number; }
        }

        public override PhysRectF VisualBounds
        {
            get
            {
                return new PhysRectF(
                    Center.X - RadiusPhys,
                    Center.Y - RadiusPhys,
                    RadiusPhys * 2,
                    RadiusPhys * 2);
            }
        }

        public override bool HitTest(PhysPointF point, double tolerance)
        {
            return point.DistanceTo(Center) <= RadiusPhys + tolerance;
        }
    }
}
