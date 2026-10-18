// ============================================================================
//  App/Overlay/OverlayPainters.cs —— 选区描边/控制柄/十字准星/尺寸 HUD/放大镜
//
//  分工边界（captain 裁决）：
//    本文件 = dev-capture 的 overlay 视觉（遮罩、边框、控制柄、放大镜、HUD）；
//    标注绘制与工具条绘制**不在这里**（dev-editor 的 AnnotationRasterizer / ToolbarPainter）。
//
//  绘制顺序由 SelectionOverlayForm 固定（ADR-003 §9.2），本文件只负责单层，
//  这样每层都能被单独自测（--selftest-overlay 会逐层渲染取证）。
//
//  文字一律走 TextRenderer（GDI），不用 Graphics.DrawString（GDI+）：
//  HUD 是小字号 + 高 DPI，GDI 的整数栅格化比 GDI+ 的抗锯齿更清晰、更快。
// ============================================================================

using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Globalization;
using ShotTool.Core;

namespace ShotTool.App.Overlay;

/// <summary>overlay 字体解析（避免每次绘制都新建 Font 族）。</summary>
internal static class OverlayFonts
{
    private static readonly string FamilyName = ResolveFamilyName();

    private static string ResolveFamilyName()
    {
        // 中文 Windows 首选（WeChat 用系统 UI 字体），缺失时由 Font 构造器自动回落
        foreach (string candidate in new[] { "Microsoft YaHei UI", "Microsoft YaHei", "Segoe UI" })
        {
            try
            {
                using var font = new Font(candidate, 12f, FontStyle.Regular, GraphicsUnit.Pixel);
                if (string.Equals(font.FontFamily.Name, candidate, StringComparison.OrdinalIgnoreCase))
                {
                    return candidate;
                }
            }
            catch (ArgumentException)
            {
                // 该字体不存在，继续试下一个
            }
        }

        return SystemFonts.MessageBoxFont?.FontFamily.Name ?? "Segoe UI";
    }

    /// <summary>按**物理像素**字号建字体（不随 DPI 二次缩放）。</summary>
    internal static Font Create(float pixelSize, FontStyle style = FontStyle.Regular)
        => new(FamilyName, Math.Max(6f, pixelSize), style, GraphicsUnit.Pixel);
}

/// <summary>选区视觉：描边 / 8 个控制柄 / 全屏十字准星 / 尺寸 HUD。</summary>
internal static class SelectionPainter
{
    private const TextFormatFlags HudFlags =
        TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.Left;

    /// <summary>画选区边框（1–2 物理 px，亮绿）。</summary>
    internal static void PaintBorder(Graphics graphics, PhysRect selection, OverlayMetrics metrics)
    {
        if (selection.IsEmpty)
        {
            return;
        }

        int width = metrics.BorderWidthPx;

        // 底部半像素偏移：让 1px 线落在像素中心，避免发虚
        Rectangle rect = new(
            selection.X,
            selection.Y,
            Math.Max(1, selection.Width - width),
            Math.Max(1, selection.Height - width));

        using var pen = new Pen(OverlayColors.SelectionBorder, width);
        graphics.SmoothingMode = SmoothingMode.None;
        graphics.DrawRectangle(pen, rect);
    }

    /// <summary>画 8 个控制柄（NW/N/NE/E/SE/S/SW/W，顺序与 SelectionGeometry.GetHandleCenters 一致）。</summary>
    internal static void PaintHandles(Graphics graphics, PhysRect selection, OverlayMetrics metrics)
    {
        if (selection.IsEmpty)
        {
            return;
        }

        Span<PhysPoint> centers = stackalloc PhysPoint[8];
        SelectionGeometry.GetHandleCenters(selection, centers);

        int size = metrics.HandleSizePx;
        int half = size / 2;

        graphics.SmoothingMode = SmoothingMode.None;
        using var fill = new SolidBrush(OverlayColors.HandleFill);
        using var border = new Pen(OverlayColors.HandleBorder, Math.Max(1, metrics.BorderWidthPx));

        for (int i = 0; i < centers.Length; i++)
        {
            PhysPoint center = centers[i];
            var rect = new Rectangle(center.X - half, center.Y - half, size, size);
            graphics.FillRectangle(fill, rect);
            graphics.DrawRectangle(border, rect);
        }
    }

    /// <summary>未框选态：全屏十字准星（跟随光标）。</summary>
    internal static void PaintCrosshair(Graphics graphics, PhysPoint cursor, PhysRect virtualBounds, OverlayMetrics metrics)
    {
        if (!virtualBounds.Contains(cursor))
        {
            return;
        }

        int width = Math.Max(1, metrics.BorderWidthPx);
        using var pen = new Pen(OverlayColors.Crosshair, width);
        graphics.SmoothingMode = SmoothingMode.None;

        int clientX = cursor.X - virtualBounds.X;
        int clientY = cursor.Y - virtualBounds.Y;

        graphics.DrawLine(pen, 0, clientY, virtualBounds.Width, clientY);
        graphics.DrawLine(pen, clientX, 0, clientX, virtualBounds.Height);
    }

    /// <summary>
    /// 尺寸 HUD：`宽 x 高`（**物理像素**，与微信一致），位置在选区左上角上方；
    /// 上方放不下时翻到选区内部顶端。
    /// </summary>
    internal static void PaintSizeHud(
        Graphics graphics,
        PhysRect selection,
        PhysRect virtualBounds,
        OverlayMetrics metrics,
        Font font,
        bool live)
    {
        if (selection.IsEmpty)
        {
            return;
        }

        string text = string.Create(
            CultureInfo.InvariantCulture,
            $"{selection.Width} x {selection.Height}");

        Size textSize = TextRenderer.MeasureText(graphics, text, font, new Size(int.MaxValue, int.MaxValue), HudFlags);
        int padX = metrics.HudPaddingPx;
        int padY = Math.Max(1, metrics.HudPaddingPx / 2);

        int boxWidth = textSize.Width + (padX * 2);
        int boxHeight = textSize.Height + (padY * 2);

        // 默认：选区左上角**上方**（与微信一致）
        int localLeft = selection.X - virtualBounds.X;
        int localTop = (selection.Top - virtualBounds.Y) - boxHeight - metrics.HudGapPx;

        if (localTop < 0)
        {
            // 上方空间不足 → 翻到选区内部顶端
            localTop = (selection.Top - virtualBounds.Y) + metrics.HudGapPx;
        }

        // 水平方向夹进虚拟桌面
        localLeft = Math.Clamp(
            localLeft,
            0,
            Math.Max(0, virtualBounds.Width - boxWidth));

        var box = new Rectangle(localLeft, localTop, boxWidth, boxHeight);

        using (var background = new SolidBrush(OverlayColors.HudBackground))
        {
            graphics.FillRectangle(background, box);
        }

        if (live)
        {
            using var pen = new Pen(OverlayColors.SelectionBorder, Math.Max(1, metrics.BorderWidthPx));
            graphics.DrawRectangle(pen, box);
        }

        TextRenderer.DrawText(
            graphics,
            text,
            font,
            new Rectangle(box.X + padX, box.Y + padY, box.Width, box.Height),
            OverlayColors.HudText,
            HudFlags);
    }
}

/// <summary>
/// 放大镜 + 像素信息面板（HEX / RGB / 坐标 / 倍率）。
/// 采样与绘制分离：<see cref="Measure"/> 给出占用矩形（供脏矩形失效），
/// <see cref="Paint"/> 只画不测，保证「失效区域 == 绘制区域」不产生残影。
/// </summary>
internal sealed class MagnifierRenderer : IDisposable
{
    private const TextFormatFlags Flags =
        TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.Left;

    private readonly Bitmap _source;
    private readonly PhysRect _virtualBounds;

    private Font? _font;
    private Font? _fontBold;
    private float _fontPx = -1f;

    /// <summary>最近一次 <see cref="Measure"/> 的结果（overlay 用它做脏矩形）。</summary>
    internal PhysRect LastBounds { get; private set; }

    internal MagnifierRenderer(Bitmap source, PhysRect virtualBounds)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _virtualBounds = virtualBounds;
    }

    /// <summary>
    /// 计算放大镜 + 信息面板在**局部坐标**（overlay 客户区 = 虚拟桌面 - 原点）里的联合矩形。
    /// 位置策略：光标右下偏移；右侧/下方放不下则翻到左/上；最后夹进虚拟桌面。
    /// </summary>
    internal PhysRect Measure(PhysPoint cursorVirtual, OverlayMetrics metrics, string hexText, string rgbText, string coordText)
    {
        EnsureFonts(metrics);

        int localCursorX = cursorVirtual.X - _virtualBounds.X;
        int localCursorY = cursorVirtual.Y - _virtualBounds.Y;

        int boxSize = metrics.MagnifierSizePx;
        int panelWidth = MeasurePanelWidth(metrics, hexText, rgbText, coordText);
        int panelHeight = MeasurePanelHeight(metrics);

        int totalWidth = Math.Max(boxSize, panelWidth);
        int totalHeight = boxSize + metrics.HudGapPx + panelHeight;

        int left = localCursorX + metrics.MagnifierGapPx;
        int top = localCursorY + metrics.MagnifierGapPx;

        if (left + totalWidth > _virtualBounds.Width)
        {
            left = localCursorX - metrics.MagnifierGapPx - totalWidth;
        }

        if (top + totalHeight > _virtualBounds.Height)
        {
            top = localCursorY - metrics.MagnifierGapPx - totalHeight;
        }

        left = Math.Clamp(left, 0, Math.Max(0, _virtualBounds.Width - totalWidth));
        top = Math.Clamp(top, 0, Math.Max(0, _virtualBounds.Height - totalHeight));

        LastBounds = new PhysRect(left, top, totalWidth, totalHeight).Inflate(4);
        return LastBounds;
    }

    /// <summary>绘制放大镜 + 信息面板。<paramref name="cursorVirtual"/> 为虚拟桌面坐标。</summary>
    internal void Paint(
        Graphics graphics,
        PhysPoint cursorVirtual,
        OverlayMetrics metrics,
        Color pixelColor,
        string hexText,
        string rgbText,
        string coordText)
    {
        EnsureFonts(metrics);

        PhysRect bounds = Measure(cursorVirtual, metrics, hexText, rgbText, coordText);

        int boxSize = metrics.MagnifierSizePx;
        int panelHeight = MeasurePanelHeight(metrics);
        var box = new Rectangle(bounds.X, bounds.Y, boxSize, boxSize);
        var panel = new Rectangle(
            bounds.X,
            bounds.Y + boxSize + metrics.HudGapPx,
            Math.Max(boxSize, bounds.Width),
            panelHeight);

        graphics.SmoothingMode = SmoothingMode.None;
        graphics.InterpolationMode = InterpolationMode.NearestNeighbor;
        graphics.PixelOffsetMode = PixelOffsetMode.Half;

        // ---- 1) 采样框（位图坐标）----
        PhysPoint bitmapPoint = new(
            cursorVirtual.X - _virtualBounds.X,
            cursorVirtual.Y - _virtualBounds.Y);

        int sampleSide = Math.Max(3, metrics.MagnifierSamplePerSide);
        int srcLeft = bitmapPoint.X - (sampleSide / 2);
        int srcTop = bitmapPoint.Y - (sampleSide / 2);

        // 屏幕边缘：整体平移采样窗口而不是裁剪，保证放大镜恒为整块
        srcLeft = Math.Clamp(srcLeft, 0, Math.Max(0, _source.Width - sampleSide));
        srcTop = Math.Clamp(srcTop, 0, Math.Max(0, _source.Height - sampleSide));

        int actualSide = Math.Min(sampleSide, Math.Min(_source.Width, _source.Height));
        var srcRect = new Rectangle(srcLeft, srcTop, actualSide, actualSide);

        graphics.DrawImage(_source, box, srcRect, GraphicsUnit.Pixel);

        // ---- 2) 放大镜边框 + 中心准星 ----
        using (var borderPen = new Pen(OverlayColors.MagnifierBorder, metrics.MagnifierBorderPx))
        {
            graphics.DrawRectangle(borderPen, box);
        }

        int cell = Math.Max(2, boxSize / Math.Max(1, actualSide));
        int centerX = box.X + (box.Width / 2);
        int centerY = box.Y + (box.Height / 2);
        var centerCell = new Rectangle(
            centerX - (cell / 2),
            centerY - (cell / 2),
            cell,
            cell);

        using (var crossPen = new Pen(OverlayColors.MagnifierCrosshair, Math.Max(1, metrics.MagnifierBorderPx)))
        {
            graphics.DrawRectangle(crossPen, centerCell);
        }

        // ---- 3) 信息面板：色块 + HEX + RGB + 坐标 + 倍率 ----
        using (var background = new SolidBrush(OverlayColors.HudBackground))
        {
            graphics.FillRectangle(background, panel);
        }

        int pad = metrics.HudPaddingPx;
        int swatch = Math.Max(10, metrics.HudPaddingPx * 2);
        var swatchRect = new Rectangle(panel.X + pad, panel.Y + pad, swatch, swatch);

        using (var swatchBrush = new SolidBrush(pixelColor))
        {
            graphics.FillRectangle(swatchBrush, swatchRect);
        }

        using (var swatchPen = new Pen(OverlayColors.HudTextSecondary, 1f))
        {
            graphics.DrawRectangle(swatchPen, swatchRect);
        }

        int textLeft = swatchRect.Right + pad;
        int lineHeight = TextRenderer.MeasureText(graphics, "Ag", _font!, new Size(int.MaxValue, int.MaxValue), Flags).Height;

        TextRenderer.DrawText(
            graphics,
            hexText,
            _fontBold!,
            new Rectangle(textLeft, panel.Y + pad, panel.Width, lineHeight),
            OverlayColors.HudText,
            Flags);

        int line2Top = panel.Y + pad + lineHeight;
        TextRenderer.DrawText(
            graphics,
            rgbText,
            _font!,
            new Rectangle(textLeft, line2Top, panel.Width, lineHeight),
            OverlayColors.HudTextSecondary,
            Flags);

        int line3Top = line2Top + lineHeight;
        TextRenderer.DrawText(
            graphics,
            coordText,
            _font!,
            new Rectangle(panel.X + pad, line3Top, panel.Width, lineHeight),
            OverlayColors.HudTextSecondary,
            Flags);

        string zoomText = $"{metrics.Zoom}x";
        Size zoomSize = TextRenderer.MeasureText(graphics, zoomText, _font!, new Size(int.MaxValue, int.MaxValue), Flags);
        TextRenderer.DrawText(
            graphics,
            zoomText,
            _font!,
            new Rectangle(panel.Right - zoomSize.Width - pad, panel.Y + pad, zoomSize.Width, lineHeight),
            OverlayColors.HudText,
            Flags);
    }

    private int MeasurePanelWidth(OverlayMetrics metrics, string hexText, string rgbText, string coordText)
    {
        using Graphics? measure = null;
        int pad = metrics.HudPaddingPx;
        int swatch = Math.Max(10, metrics.HudPaddingPx * 2);

        int w1 = TextWidth(measure, hexText, _fontBold!, metrics);
        int w2 = TextWidth(measure, rgbText, _font!, metrics);
        int w3 = TextWidth(measure, coordText, _font!, metrics);
        int zw = TextWidth(measure, $"{metrics.Zoom}x", _font!, metrics);

        int widest = Math.Max(Math.Max(w1 + zw, w2), w3);
        return Math.Max(metrics.MagnifierSizePx, pad + swatch + pad + widest + pad);
    }

    private int MeasurePanelHeight(OverlayMetrics metrics)
    {
        using Graphics? measure = null;
        int lineHeight = TextHeight(measure, _font!, metrics);
        return (metrics.HudPaddingPx * 2) + (lineHeight * 3);
    }

    private static int TextWidth(Graphics? graphics, string text, Font font, OverlayMetrics metrics)
    {
        Size size = MeasureText(graphics, text, font);
        return size.Width;
    }

    private static int TextHeight(Graphics? graphics, Font font, OverlayMetrics metrics)
        => MeasureText(graphics, "Ag", font).Height;

    /// <summary>
    /// 无 Graphics 时用临时位图测量（Measure 阶段没有 Graphics 可用）。
    /// 只在小对象上测量，开销可忽略。
    /// </summary>
    private static Size MeasureText(Graphics? graphics, string text, Font font)
    {
        if (graphics is not null)
        {
            return TextRenderer.MeasureText(graphics, text, font, new Size(int.MaxValue, int.MaxValue), Flags);
        }

        using var probe = new Bitmap(1, 1);
        using Graphics temp = Graphics.FromImage(probe);
        return TextRenderer.MeasureText(temp, text, font, new Size(int.MaxValue, int.MaxValue), Flags);
    }

    private void EnsureFonts(OverlayMetrics metrics)
    {
        if (_font is not null && Math.Abs(_fontPx - metrics.HudFontPx) < 0.01f)
        {
            return;
        }

        _font?.Dispose();
        _fontBold?.Dispose();

        _font = OverlayFonts.Create(metrics.HudSmallFontPx);
        _fontBold = OverlayFonts.Create(metrics.HudFontPx, FontStyle.Bold);
        _fontPx = metrics.HudFontPx;
    }

    public void Dispose()
    {
        _font?.Dispose();
        _fontBold?.Dispose();
        _font = null;
        _fontBold = null;
    }
}
