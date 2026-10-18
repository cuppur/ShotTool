// ============================================================================
//  App/Overlay/OverlayTheme.cs —— overlay 视觉常量与 DPI 换算（唯一一处）
//
//  数值来源（不是拍脑袋）：
//    * docs/06-toolbar-spec.md §6：遮罩 = 选区外整屏半透明黑（约 50%）、
//      选区亮绿描边 1–2 逻辑 px、四角 + 边中点白色方形控制柄、
//      工具条 = selection.right / selection.bottom + 15 逻辑 px；
//    * docs/01-recon.md §5.2：尺寸提示显示**物理像素**、放大镜 15×15 采样 @8×；
//    * docs/02-architecture.md ADR-003/005：单一不透明全屏窗、全链路物理像素，
//      只有 UI 元素尺寸需要 DIP→px 换算（DpiHelper.DipToPx）。
//
//  本文件是整个 overlay 里唯一允许出现「逻辑 px → 物理 px」的地方。
// ============================================================================

using System.Drawing;
using ShotTool.Core;

namespace ShotTool.App.Overlay;

/// <summary>overlay 配色（与工具条 #FAFAFA / #00C375 同源，避免两套色系）。</summary>
internal static class OverlayColors
{
    /// <summary>选区外遮罩（黑，约 50% —— 与微信实测一致）。</summary>
    internal static readonly Color Mask = Color.FromArgb(128, 0, 0, 0);

    /// <summary>选区描边（品牌绿）。</summary>
    internal static readonly Color SelectionBorder = Color.FromArgb(0xFF, 0x00, 0xC3, 0x75);

    /// <summary>控制柄填充（白色方块）。</summary>
    internal static readonly Color HandleFill = Color.White;

    /// <summary>控制柄描边（深色，保证白色方块在浅色背景上也有边界）。</summary>
    internal static readonly Color HandleBorder = Color.FromArgb(0xCC, 0x1B, 0x1B, 0x1F);

    /// <summary>全屏十字准星（半透明绿，不抢视线）。</summary>
    internal static readonly Color Crosshair = Color.FromArgb(0xB0, 0x00, 0xC3, 0x75);

    /// <summary>HUD 底板（深色半透明圆角块）。</summary>
    internal static readonly Color HudBackground = Color.FromArgb(0xC8, 0x1B, 0x1B, 0x1F);

    /// <summary>HUD 文字（白）。</summary>
    internal static readonly Color HudText = Color.FromArgb(0xFF, 0xF5, 0xF5, 0xF7);

    /// <summary>HUD 次要文字（坐标 / RGB）。</summary>
    internal static readonly Color HudTextSecondary = Color.FromArgb(0xFF, 0xA8, 0xA8, 0xB0);

    /// <summary>放大镜边框。</summary>
    internal static readonly Color MagnifierBorder = Color.FromArgb(0xFF, 0x40, 0x40, 0x46);

    /// <summary>放大镜中心像素十字准星。</summary>
    internal static readonly Color MagnifierCrosshair = Color.FromArgb(0xE0, 0xFF, 0x3B, 0x30);

    /// <summary>微调用的选区外浅色描边（在遮罩上仍可见）。</summary>
    internal static readonly Color SelectionShadow = Color.FromArgb(0x60, 0x00, 0x00, 0x00);
}

/// <summary>
/// overlay 物理像素度量。构造时把全部逻辑尺寸按 DPI 换算一次，
/// 绘制/命中测试只读结果 —— 避免每个绘制函数各算一遍导致不一致。
/// </summary>
internal sealed class OverlayMetrics
{
    /// <summary>放大镜可循环的倍率（滚轮切换，P1）。</summary>
    internal static readonly int[] ZoomLevels = { 4, 8, 12, 16 };

    internal const int DefaultZoom = 8;

    private OverlayMetrics(uint dpi, int zoom)
    {
        Dpi = DpiHelper.Sanitize(dpi);
        Zoom = zoom;

        BorderWidthPx = Math.Max(1, DpiHelper.DipToPx(1.0, Dpi));
        HandleSizePx = Math.Max(5, DpiHelper.DipToPx(7.0, Dpi));
        HandleHitPx = Math.Max(12, DpiHelper.DipToPx(14.0, Dpi));
        CornerRadiusPx = Math.Max(2, DpiHelper.DipToPx(4.0, Dpi));

        MagnifierSizePx = Math.Max(60, DpiHelper.DipToPx(120.0, Dpi));
        MagnifierSamplePerSide = Math.Max(5, MagnifierSizePx / Math.Max(1, Zoom));
        MagnifierGapPx = Math.Max(6, DpiHelper.DipToPx(20.0, Dpi));
        MagnifierBorderPx = Math.Max(1, DpiHelper.DipToPx(1.0, Dpi));

        HudFontPx = Math.Max(9f, DpiHelper.DipToPx(13.0, Dpi));
        HudSmallFontPx = Math.Max(8f, DpiHelper.DipToPx(12.0, Dpi));
        HudPaddingPx = Math.Max(3, DpiHelper.DipToPx(6.0, Dpi));
        HudGapPx = Math.Max(2, DpiHelper.DipToPx(4.0, Dpi));

        // 工具条定位（docs/06-toolbar-spec.md §5 实测真值）
        ToolbarGapBelowSelectionPx = DpiHelper.DipToPx(15.0, Dpi);
        ToolbarRightAlignOffsetPx = DpiHelper.DipToPx(1.0, Dpi);
        ToolbarEdgeMarginPx = DpiHelper.DipToPx(2.0, Dpi);
    }

    /// <summary>按 DPI 构建（默认 8× 放大）。</summary>
    internal static OverlayMetrics ForDpi(uint dpi) => new(dpi, DefaultZoom);

    internal uint Dpi { get; }

    /// <summary>放大镜倍率（4/8/12/16）。</summary>
    internal int Zoom { get; }

    /// <summary>DpiHelper 用的缩放系数（工具条/文字绘制传给它）。</summary>
    internal double DpiScale => DpiHelper.ScaleFactor(Dpi);

    internal int BorderWidthPx { get; }

    internal int HandleSizePx { get; }

    /// <summary>控制柄命中方块边长（比视觉尺寸大，便于鼠标点中）。</summary>
    internal int HandleHitPx { get; }

    internal int CornerRadiusPx { get; }

    internal int MagnifierSizePx { get; }

    /// <summary>放大镜采样的源像素边长（8× 时 = 120/8 = 15，与微信实测一致）。</summary>
    internal int MagnifierSamplePerSide { get; }

    internal int MagnifierGapPx { get; }

    internal int MagnifierBorderPx { get; }

    internal float HudFontPx { get; }

    internal float HudSmallFontPx { get; }

    internal int HudPaddingPx { get; }

    internal int HudGapPx { get; }

    internal int ToolbarGapBelowSelectionPx { get; }

    internal int ToolbarRightAlignOffsetPx { get; }

    internal int ToolbarEdgeMarginPx { get; }

    /// <summary>切换放大倍率（滚轮），返回新度量。</summary>
    internal OverlayMetrics WithNextZoom(int direction)
    {
        int index = Array.IndexOf(ZoomLevels, Zoom);
        if (index < 0)
        {
            index = Array.IndexOf(ZoomLevels, DefaultZoom);
        }

        int next = ((index + (direction >= 0 ? 1 : -1)) % ZoomLevels.Length + ZoomLevels.Length) % ZoomLevels.Length;
        return new OverlayMetrics(Dpi, ZoomLevels[next]);
    }
}
