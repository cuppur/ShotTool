// 工具条模型与布局（纯数据 + 纯几何，不绘制、不持有窗口）。
// 数值全部来自 EditorDefaults（= docs/06-toolbar-spec.md 实测）。
#nullable enable
using System;
using System.Collections.Generic;
using ShotTool.Core;
using ShotTool.Editor.Model;

namespace ShotTool.Editor.Toolbar
{
    /// <summary>按钮角色。</summary>
    public enum ToolbarItemRole
    {
        Tool = 0,
        Action = 1,
        Separator = 2,
        /// <summary>Core.IToolbarItem 注入的扩展按钮（OCR / 翻译等）。</summary>
        Extension = 3,
    }

    /// <summary>次级（弹出）面板种类。</summary>
    public enum SubPanelKind
    {
        None = 0,
        Palette = 1,
        Brush = 2,
        TextSize = 3,
    }

    /// <summary>工具条上的一个按钮（或分隔线）。</summary>
    public sealed class ToolbarItem
    {
        public ToolbarItem(string id, string label, ToolbarIconKey icon, ToolbarItemRole role, AnnotationKind? tool)
        {
            Id = id ?? string.Empty;
            Label = label ?? string.Empty;
            Icon = icon;
            Role = role;
            Tool = tool;
        }

        public static ToolbarItem Separator(string id)
        {
            return new ToolbarItem(id, string.Empty, ToolbarIconKey.None, ToolbarItemRole.Separator, null);
        }

        public string Id { get; private set; }

        public string Label { get; set; }

        public ToolbarIconKey Icon { get; set; }

        public ToolbarItemRole Role { get; private set; }

        /// <summary>标注工具对应的类型（Action/Extension 为 null）。</summary>
        public AnnotationKind? Tool { get; private set; }

        public bool Visible { get; set; } = true;

        public bool Enabled { get; set; } = true;

        public bool IsToggle { get; set; }

        public bool Checked { get; set; }

        /// <summary>取消类按钮（危险红）。</summary>
        public bool IsDanger { get; set; }

        /// <summary>确认/复制类按钮（品牌绿）。</summary>
        public bool IsConfirm { get; set; }

        /// <summary>Core 扩展按钮（非 null 时按 IToolbarItem 的 IsVisible/IsEnabled/IsChecked 同步）。</summary>
        public IToolbarItem? Extension { get; set; }
    }

    /// <summary>工具条 + 次级面板的完整状态（绘制器唯一输入）。</summary>
    public sealed class ToolbarState
    {
        private readonly List<ToolbarItem> _items = new List<ToolbarItem>();

        public IReadOnlyList<ToolbarItem> Items
        {
            get { return _items; }
        }

        public ToolbarStyle Style { get; set; } = ToolbarStyle.Normal;

        /// <summary>当前激活的工具按钮索引（-1 = 无）。</summary>
        public int ActiveIndex { get; set; } = -1;

        public int HoverIndex { get; set; } = -1;

        public int PressedIndex { get; set; } = -1;

        public SubPanelKind SubPanel { get; set; } = SubPanelKind.None;

        public int SubPanelHover { get; set; } = -1;

        public uint ActiveColorArgb { get; set; } = EditorDefaults.DefaultColorArgb;

        public BrushLevel Brush { get; set; } = EditorDefaults.DefaultBrush;

        public TextSizeLevel TextSize { get; set; } = EditorDefaults.DefaultTextSize;

        /// <summary>当前激活工具的标注类型（无激活工具 = null）。</summary>
        public AnnotationKind? ActiveTool
        {
            get
            {
                if (ActiveIndex < 0 || ActiveIndex >= _items.Count)
                {
                    return null;
                }

                return _items[ActiveIndex].Tool;
            }
        }

        public void Add(ToolbarItem item)
        {
            if (item != null)
            {
                _items.Add(item);
            }
        }

        public void Clear()
        {
            _items.Clear();
            ActiveIndex = -1;
            HoverIndex = -1;
            PressedIndex = -1;
            SubPanel = SubPanelKind.None;
            SubPanelHover = -1;
        }

        /// <summary>按 P0 顺序构建默认按钮集（7 工具 + 分隔 + 撤销 + 扩展位 + 保存/复制/取消）。</summary>
        public static ToolbarState CreateDefault()
        {
            ToolbarState state = new ToolbarState();
            state.Add(new ToolbarItem("tool.rect", "矩形", ToolbarIconKey.Rect, ToolbarItemRole.Tool, AnnotationKind.Rectangle));
            state.Add(new ToolbarItem("tool.ellipse", "椭圆", ToolbarIconKey.Ellipse, ToolbarItemRole.Tool, AnnotationKind.Ellipse));
            state.Add(new ToolbarItem("tool.arrow", "箭头", ToolbarIconKey.Arrow, ToolbarItemRole.Tool, AnnotationKind.Arrow));
            state.Add(new ToolbarItem("tool.pen", "画笔", ToolbarIconKey.Pen, ToolbarItemRole.Tool, AnnotationKind.Pen));
            state.Add(new ToolbarItem("tool.mosaic", "马赛克", ToolbarIconKey.Mosaic, ToolbarItemRole.Tool, AnnotationKind.Mosaic));
            state.Add(new ToolbarItem("tool.text", "文字", ToolbarIconKey.Text, ToolbarItemRole.Tool, AnnotationKind.Text));
            state.Add(new ToolbarItem("tool.number", "序号", ToolbarIconKey.Number, ToolbarItemRole.Tool, AnnotationKind.Number));
            // 注意：这里**不再**加显式 Separator。分组分隔线由 ToolbarLayout 按语义分组自动生成
            // （GroupOf：标注工具组 / 编辑+扩展组 / 输出确认组），这样增减按钮不会丢分隔线。

            ToolbarItem undo = new ToolbarItem("action.undo", "撤销", ToolbarIconKey.Undo, ToolbarItemRole.Action, null);
            undo.Enabled = false;
            state.Add(undo);

            ToolbarItem save = new ToolbarItem("action.save", "保存", ToolbarIconKey.Save, ToolbarItemRole.Action, null);
            // 微信实测第 16 个按钮是「✓ 对勾」（语义 = 完成并复制），不是双矩形"复制"图标；
            // 用户对微信的肌肉记忆就是点那个对勾，因此这里必须用 Confirm 图标。
            ToolbarItem copy = new ToolbarItem("action.copy", "完成并复制", ToolbarIconKey.Confirm, ToolbarItemRole.Action, null);
            copy.IsConfirm = true;
            ToolbarItem close = new ToolbarItem("action.close", "取消", ToolbarIconKey.Close, ToolbarItemRole.Action, null);
            close.IsDanger = true;
            state.Add(save);
            state.Add(new ToolbarItem("action.pin", "固定截图（F3）", ToolbarIconKey.Pin, ToolbarItemRole.Action, null));
            state.Add(close);
            state.Add(copy);
            return state;
        }
    }

    /// <summary>布局结果（物理像素）。</summary>
    public sealed class ToolbarGeometry
    {
        public ToolbarGeometry()
        {
            Slots = new List<PhysRectF>();
            SlotItemIndex = new List<int>();
            SubPanelSlots = new List<PhysRectF>();
        }

        public PhysRectF Bounds { get; set; }

        /// <summary>与 SlotItemIndex 对齐的按钮/分隔线矩形。</summary>
        public List<PhysRectF> Slots { get; private set; }

        /// <summary>Slots[i] 对应的 ToolbarState.Items 索引。</summary>
        public List<int> SlotItemIndex { get; private set; }

        public PhysRectF SubPanelBounds { get; set; }

        public List<PhysRectF> SubPanelSlots { get; private set; }

        public double DpiScale { get; set; }

        /// <summary>主条 + 次级面板的并集（用于脏矩形）。</summary>
        public PhysRectF OverallBounds
        {
            get
            {
                if (SubPanelBounds.IsEmpty)
                {
                    return Bounds;
                }

                return EditorGeometry.Union(Bounds, SubPanelBounds);
            }
        }
    }

    /// <summary>工具条布局计算（纯函数；绘制与命中测试共用，保证一致）。</summary>
    public static class ToolbarLayout
    {
        /// <summary>次级面板单项逻辑尺寸（色块/笔触圆/字号）。</summary>
        public const double SubPanelItemLogical = 28.0;

        /// <summary>次级面板项间距（逻辑）。</summary>
        public const double SubPanelGapLogical = 8.0;

        /// <summary>次级面板与工具条的间距（逻辑）。</summary>
        public const double SubPanelOffsetLogical = 8.0;

        /// <summary>次级面板高度（逻辑）。</summary>
        public const double SubPanelHeightLogical = 32.0;

        /// <summary>
        /// 工具条语义分组（用于**自动**分组分隔线）：
        /// 0 = 标注工具组（矩形…序号）；1 = 编辑/扩展组（撤销/重做 + OCR/翻译扩展按钮）；2 = 输出确认组（保存/完成/取消）。
        /// <para>
        /// 为什么要按语义而不是按"原按钮是否存在"：微信原生分隔线的锚点是"表情/长截图"等按钮，
        /// 我们砍掉这些按钮后，如果只在 `CreateDefault()` 里写死 Separator，任何一次按钮增减都会丢分隔线。
        /// 按语义分组后，增减按钮不影响分组结构。
        /// </para>
        /// </summary>
        public static int GroupOf(ToolbarItem item)
        {
            if (item == null)
            {
                return 0;
            }

            if (item.Role == ToolbarItemRole.Tool)
            {
                return 0;
            }

            if (item.Role == ToolbarItemRole.Extension)
            {
                return 1;
            }

            switch (item.Id)
            {
                case "action.undo":
                case "action.redo":
                case "action.collapse":
                      return 2;
                default:
                    return 2;
            }
        }

        /// <summary>槽计划：LogicalWidth 为逻辑 px；ItemIndex = -1 表示隐式分隔线。</summary>
        private readonly struct SlotPlan
        {
            public SlotPlan(int itemIndex, double logicalWidth)
            {
                ItemIndex = itemIndex;
                LogicalWidth = logicalWidth;
            }

            public int ItemIndex { get; }

            public double LogicalWidth { get; }
        }

        private static List<SlotPlan> BuildPlan(ToolbarState state, double buttonLogical, double separatorLogical)
        {
            List<SlotPlan> plan = new List<SlotPlan>();
            IReadOnlyList<ToolbarItem> items = state.Items;
            int lastGroup = -1;

            for (int i = 0; i < items.Count; i++)
            {
                ToolbarItem item = items[i];
                if (!item.Visible)
                {
                    continue;
                }

                if (item.Role == ToolbarItemRole.Separator)
                {
                    plan.Add(new SlotPlan(i, separatorLogical));
                    lastGroup = -1; // 已有显式分隔线：重置，避免重复补一条
                    continue;
                }

                int group = GroupOf(item);
                if (lastGroup >= 0 && group != lastGroup)
                {
                    // 语义分组边界：自动补一条分隔线（即使调用方没构造 Separator 项）
                    plan.Add(new SlotPlan(-1, separatorLogical));
                }

                plan.Add(new SlotPlan(i, buttonLogical));
                lastGroup = group;
            }

            return plan;
        }

        public static ToolbarGeometry Compute(ToolbarState state, PhysPointF topLeft, double dpiScale)
        {
            if (state == null)
            {
                throw new ArgumentNullException(nameof(state));
            }

            double scale = dpiScale > 0.05 ? dpiScale : 1.0;
            double button = EditorDefaults.ToolbarButtonLogical(state.Style);
            double separator = EditorDefaults.ToolbarSeparatorSlotLogical;
            double padding = EditorDefaults.ToolbarPaddingLogical;
            double height = EditorDefaults.ToolbarHeightLogical;

            ToolbarGeometry geometry = new ToolbarGeometry { DpiScale = scale };

            // 1) 槽计划：按语义分组自动补隐式分隔线（不依赖调用方是否构造了 Separator 项）
            List<SlotPlan> plan = BuildPlan(state, button, separator);

            // 2) 主条宽度（逻辑）→ 物理
            double logicalWidth = padding * 2.0;
            for (int i = 0; i < plan.Count; i++)
            {
                logicalWidth += plan[i].LogicalWidth;
            }

            geometry.Bounds = new PhysRectF(topLeft.X, topLeft.Y, logicalWidth * scale, height * scale);

            // 3) 逐个槽（ItemIndex = -1 表示"语义分组自动补的分隔线"）
            double x = topLeft.X + padding * scale;
            double slotHeight = height * scale;
            for (int i = 0; i < plan.Count; i++)
            {
                geometry.Slots.Add(new PhysRectF(x, topLeft.Y, plan[i].LogicalWidth * scale, slotHeight));
                geometry.SlotItemIndex.Add(plan[i].ItemIndex);
                x += plan[i].LogicalWidth * scale;
            }

            // 3) 次级面板（位于主条上方 8 逻辑 px，左对齐）
            int subCount = SubPanelItemCount(state.SubPanel);
            if (subCount > 0)
            {
                double itemLogical = state.SubPanel == SubPanelKind.Palette ? SubPanelItemLogical : button;
                double panelLogicalWidth = subCount * itemLogical + (subCount - 1) * SubPanelGapLogical + 2.0 * padding;
                double panelLogicalHeight = SubPanelHeightLogical;
                double panelLeft = topLeft.X;
                double panelTop = topLeft.Y - (SubPanelOffsetLogical + panelLogicalHeight) * scale;
                geometry.SubPanelBounds = new PhysRectF(
                    panelLeft,
                    panelTop,
                    panelLogicalWidth * scale,
                    panelLogicalHeight * scale);

                double sx = panelLeft + padding * scale;
                for (int i = 0; i < subCount; i++)
                {
                    geometry.SubPanelSlots.Add(new PhysRectF(
                        sx,
                        panelTop + (panelLogicalHeight - SubPanelItemLogical) * 0.5 * scale,
                        itemLogical * scale,
                        (state.SubPanel == SubPanelKind.Palette ? SubPanelItemLogical : button) * scale));
                    sx += (itemLogical + SubPanelGapLogical) * scale;
                }
            }

            return geometry;
        }

        /// <summary>次级面板项数（色板 6 / 笔触 4 / 字号 3）。</summary>
        public static int SubPanelItemCount(SubPanelKind kind)
        {
            switch (kind)
            {
                case SubPanelKind.Palette:
                    return EditorDefaults.PaletteArgb.Length;
                case SubPanelKind.Brush:
                    return 4;
                case SubPanelKind.TextSize:
                    return 3;
                default:
                    return 0;
            }
        }

        /// <summary>仅测主条尺寸（逻辑 → 物理）。</summary>
        public static PhysSizeF Measure(ToolbarState state, double dpiScale)
        {
            ToolbarGeometry geometry = Compute(state, PhysPointF.Zero, dpiScale);
            return new PhysSizeF(geometry.Bounds.Width, geometry.Bounds.Height);
        }
    }
}
