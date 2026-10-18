// Editor 共享常量与档位换算：UI 层与算法层共用同一份数值来源。
//
// 数值状态标注：
//   [实测] = 来自 recon 的 docs/06-toolbar-spec.md（t10 像素级测量，本机 DPI 2.2494）；
//   [推定] = 由证据/观感推得，待补测后可只改本文件。
#nullable enable
using System;

namespace ShotTool.Editor.Model
{
    /// <summary>文字档位（对应原生 text_small / text_middle / text_large）。</summary>
    public enum TextSizeLevel
    {
        Small = 0,
        Middle = 1,
        Large = 2,
    }

    /// <summary>工具条样式（对应原生配置键 screenshot_toolbar_style；只有 Normal 是实测值）。</summary>
    public enum ToolbarStyle
    {
        Compact = 0,
        Normal = 1,
        Large = 2,
    }

    /// <summary>
    /// 编辑器默认值与档位换算表。全部为纯常量/纯函数，不依赖任何 UI 类型。
    /// </summary>
    public static class EditorDefaults
    {
        // ================== 色板（6 色，对应原生 color_square_black/blue/green/orange/red/white）==================
        public const uint ColorBlack = 0xFF000000u;
        public const uint ColorBlue = 0xFF1E6FFFu;
        public const uint ColorGreen = 0xFF00C853u;
        public const uint ColorOrange = 0xFFFF9500u;
        public const uint ColorRed = 0xFFF5222Du;
        public const uint ColorWhite = 0xFFFFFFFFu;

        /// <summary>色板顺序：黑 / 蓝 / 绿 / 橙 / 红 / 白（与原生资源命名顺序一致）。</summary>
        public static readonly uint[] PaletteArgb = new uint[]
        {
            ColorBlack, ColorBlue, ColorGreen, ColorOrange, ColorRed, ColorWhite,
        };

        public const uint DefaultColorArgb = ColorRed;

        public const BrushLevel DefaultBrush = BrushLevel.Middle;

        public const TextSizeLevel DefaultTextSize = TextSizeLevel.Middle;

        // ================== 档位换算 ==================
        /// <summary>文字档位 → 逻辑字号（96 DPI 基准）[推定：16 / 22 / 30]。</summary>
        public static double TextSizeToLogical(TextSizeLevel level)
        {
            switch (level)
            {
                case TextSizeLevel.Small:
                    return 16.0;
                case TextSizeLevel.Large:
                    return 30.0;
                default:
                    return 22.0;
            }
        }

        /// <summary>文字档位 → 物理像素字号（导出栅格化用这个值，见 docs/04-editor-spec.md §5）。</summary>
        public static double TextSizeToPhys(TextSizeLevel level, double dpiScale)
        {
            double scale = dpiScale > 0.1 ? dpiScale : 1.0;
            double size = TextSizeToLogical(level) * scale;
            return size < 10.0 ? 10.0 : size;
        }

        public static double BrushToLogical(BrushLevel level)
        {
            return level.ToLogicalLineWidth();
        }

        public static double BrushToPhys(BrushLevel level, double dpiScale)
        {
            return level.ToPhysLineWidth(dpiScale);
        }

        /// <summary>
        /// 马赛克块尺寸（物理像素）。configuredPhys &lt;= 0 时自动：max(8, round(10 × dpiScale))。
        /// 本机 225% 下自动值 ≈ 22 px。
        /// </summary>
        public static int ResolveMosaicBlockSize(int configuredPhys, double dpiScale)
        {
            if (configuredPhys > 1)
            {
                return configuredPhys;
            }

            double scale = dpiScale > 0.1 ? dpiScale : 1.0;
            int auto = (int)Math.Round(10.0 * scale, MidpointRounding.AwayFromZero);
            return auto < 8 ? 8 : auto;
        }

        /// <summary>序号圆半径（物理像素）：max(9, round(字号 × 0.62))。</summary>
        public static double ResolveNumberRadius(double fontSizePhys)
        {
            double r = Math.Round(fontSizePhys * 0.62, MidpointRounding.AwayFromZero);
            return r < 9.0 ? 9.0 : r;
        }

        /// <summary>箭头头长默认值（物理像素）：max(10×dpiScale, 线宽×3.5)。</summary>
        public static double ResolveArrowHeadLength(double lineWidthPhys, double dpiScale)
        {
            double scale = dpiScale > 0.1 ? dpiScale : 1.0;
            double byDpi = 10.0 * scale;
            double byWidth = lineWidthPhys * 3.5;
            return byDpi > byWidth ? byDpi : byWidth;
        }

        // ================== 工具条几何（[实测]，docs/06-toolbar-spec.md，逻辑 px）==================
        /// <summary>工具条高度 [实测 48]。普通/紧凑/大三种样式只缩放按钮，不改高度基准。</summary>
        public const double ToolbarHeightLogical = 48.0;

        /// <summary>圆角半径 [实测 8]。</summary>
        public const double ToolbarCornerRadiusLogical = 8.0;

        /// <summary>按钮热区边长（Normal = 实测 48，撑满工具条全高、紧邻无间隙）。</summary>
        public static double ToolbarButtonLogical(ToolbarStyle style)
        {
            switch (style)
            {
                case ToolbarStyle.Compact:
                    return 40.0;
                case ToolbarStyle.Large:
                    return 56.0;
                default:
                    return 48.0;
            }
        }

        /// <summary>图标视觉尺寸 = 按钮 × 0.4（实测 16~20 逻辑 px）[实测区间]。</summary>
        public static double ToolbarIconLogical(ToolbarStyle style)
        {
            // 图元在 24 网格内仍有留白，不能把整个画布再缩到 19.2 DIP。
            return ToolbarButtonLogical(style) * 0.5;
        }

        /// <summary>左右内边距 [实测各 4]。</summary>
        public const double ToolbarPaddingLogical = 4.0;

        /// <summary>分隔线的占位宽度（含两侧留白）[实测 17]。</summary>
        public const double ToolbarSeparatorSlotLogical = 17.0;

        /// <summary>分隔线自身宽度 [实测 1]。</summary>
        public const double ToolbarSeparatorWidthLogical = 1.0;

        /// <summary>工具条与选区的垂直间距：top = 选区 bottom + 15 [实测]。</summary>
        public const double ToolbarGapLogical = 15.0;

        /// <summary>
        /// 工具条水平定位规则 [实测]：右边缘与选区右边缘对齐（toolbar.right == selection.right）。
        /// 这里记常数 0 表示"右对齐，无额外偏移"。
        /// </summary>
        public const double ToolbarRightAlignOffsetLogical = 0.0;

        // ================== 工具条配色（[实测]）==================
        /// <summary>工具条背景（浅灰近白，不透明）。</summary>
        public const uint ToolbarBackgroundArgb = 0xFFFAFAFAu;

        /// <summary>分隔线颜色 #E0E0E0。</summary>
        public const uint ToolbarSeparatorArgb = 0xFFE0E0E0u;

        /// <summary>图标常态 #4C4C4C（线性图标描边）。</summary>
        public const uint ToolbarIconArgb = 0xFF4C4C4Cu;

        /// <summary>图标禁用 #AEAEAE。</summary>
        public const uint ToolbarDisabledArgb = 0xFFAEAEAEu;

        /// <summary>图标选中态 #00C375（整枚图标染色，**无背景块** — 实测）。</summary>
        public const uint ToolbarSelectedArgb = 0xFF00C375u;

        /// <summary>危险色（取消 ✕）#FA5151。</summary>
        public const uint DangerArgb = 0xFFFA5151u;

        /// <summary>确认色（完成 ✓）#00C375。</summary>
        public const uint ConfirmArgb = 0xFF00C375u;

        /// <summary>
        /// 悬停底色 [实测：不做背景变化]。保留键位以支持"可配置样式"，
        /// 默认 0 = 不绘制；若产品后续要加浅灰底，只改这里。
        /// </summary>
        public const uint ToolbarHoverArgb = 0x00000000u;

        /// <summary>选中底色 [实测：不做背景块]。</summary>
        public const uint ToolbarActiveArgb = 0x00000000u;

        /// <summary>色板点击态：选中颜色外圈描边色。</summary>
        public const uint PaletteSelectedRingArgb = 0xFF00C375u;
    }
}
