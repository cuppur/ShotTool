// 工具条绘制器 + 命中测试：**纯函数式**（输入 Graphics/状态/位置，输出像素），不持有窗口、不读全局状态。
// 宿主是谁它不关心：overlay 内自绘、owned 小窗、甚至离屏位图都能直接调用。
//
// 视觉规则（docs/06-toolbar-spec.md 实测）：浅色 #FAFAFA、按钮 48 逻辑紧邻、圆角 8、
// 图标常态 #4C4C4C / 禁用 #AEAEAE / 选中整枚染 #00C375 / 取消红 #FA5151 / 完成绿 #00C375；
// **悬停与选中都不画背景块**。
#nullable enable
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using ShotTool.Core;
using ShotTool.Editor.Model;

namespace ShotTool.Editor.Toolbar
{
    /// <summary>命中结果类别。</summary>
    public enum ToolbarHitKind
    {
        None = 0,
        Button = 1,
        Separator = 2,
        SubPanelItem = 3,
        SubPanelBody = 4,
    }

    /// <summary>命中结果：MainIndex = 主条按钮（Items 索引），SubIndex = 次级面板项序号。</summary>
    public readonly record struct ToolbarHit(ToolbarHitKind Kind, int MainIndex, int SubIndex)
    {
        public static ToolbarHit None
        {
            get { return new ToolbarHit(ToolbarHitKind.None, -1, -1); }
        }
    }

    /// <summary>工具条绘制与命中（纯函数，无状态）。</summary>
    public static class ToolbarPainter
    {
        /// <summary>绘制整条工具条 +（可选）次级面板。topLeft 为物理像素；调用方负责 clip（选区外不画）。</summary>
        public static void Paint(Graphics graphics, ToolbarState state, PhysPointF topLeft, double dpiScale)
        {
            if (graphics == null)
            {
                throw new ArgumentNullException(nameof(graphics));
            }

            if (state == null)
            {
                return;
            }

            ToolbarGeometry geometry = ToolbarLayout.Compute(state, topLeft, dpiScale);
            SmoothingMode previousSmoothing = graphics.SmoothingMode;
            graphics.SmoothingMode = SmoothingMode.AntiAlias;

            DrawContainer(graphics, geometry.Bounds, geometry.DpiScale);
            for (int i = 0; i < geometry.Slots.Count; i++)
            {
                int itemIndex = geometry.SlotItemIndex[i];
                if (itemIndex < 0)
                {
                    // 语义分组自动补的分隔线（不依赖调用方是否构造了 Separator 项）
                    DrawSeparator(graphics, geometry.Slots[i], geometry.DpiScale);
                    continue;
                }

                ToolbarItem item = state.Items[itemIndex];
                DrawSlot(graphics, state, item, itemIndex, geometry.Slots[i], geometry.DpiScale);
            }

            if (!geometry.SubPanelBounds.IsEmpty)
            {
                DrawContainer(graphics, geometry.SubPanelBounds, geometry.DpiScale);
                DrawSubPanel(graphics, state, geometry.SubPanelBounds, geometry.SubPanelSlots, geometry.DpiScale);
            }

            graphics.SmoothingMode = previousSmoothing;
        }

        /// <summary>命中测试：优先主条，其次次级面板。</summary>
        public static ToolbarHit HitTest(ToolbarState state, PhysPointF topLeft, PhysPointF point, double dpiScale)
        {
            if (state == null)
            {
                return ToolbarHit.None;
            }

            ToolbarGeometry geometry = ToolbarLayout.Compute(state, topLeft, dpiScale);

            for (int i = 0; i < geometry.Slots.Count; i++)
            {
                if (!geometry.Slots[i].ContainsPoint(point))
                {
                    continue;
                }

                int itemIndex = geometry.SlotItemIndex[i];
                if (itemIndex < 0)
                {
                    return new ToolbarHit(ToolbarHitKind.Separator, -1, -1);
                }

                ToolbarItem item = state.Items[itemIndex];
                if (item.Role == ToolbarItemRole.Separator)
                {
                    return new ToolbarHit(ToolbarHitKind.Separator, itemIndex, -1);
                }

                return new ToolbarHit(ToolbarHitKind.Button, itemIndex, -1);
            }

            if (!geometry.SubPanelBounds.IsEmpty && geometry.SubPanelBounds.ContainsPoint(point))
            {
                for (int i = 0; i < geometry.SubPanelSlots.Count; i++)
                {
                    if (geometry.SubPanelSlots[i].Inflated(2.0 * geometry.DpiScale).ContainsPoint(point))
                    {
                        return new ToolbarHit(ToolbarHitKind.SubPanelItem, -1, i);
                    }
                }

                return new ToolbarHit(ToolbarHitKind.SubPanelBody, -1, -1);
            }

            return ToolbarHit.None;
        }

        private static void DrawContainer(Graphics graphics, PhysRectF bounds, double dpiScale)
        {
            float radius = (float)(EditorDefaults.ToolbarCornerRadiusLogical * dpiScale);
            float x = (float)bounds.X;
            float y = (float)bounds.Y;
            float w = (float)bounds.Width;
            float h = (float)bounds.Height;
            if (w <= 0f || h <= 0f)
            {
                return;
            }

            using (GraphicsPath path = BuildRounded(x, y, w, h, radius))
            using (SolidBrush brush = new SolidBrush(ToColor(EditorDefaults.ToolbarBackgroundArgb)))
            {
                graphics.FillPath(brush, path);
            }
        }

        /// <summary>
        /// 画一条分组分隔线。用「整数像素矩形填充」而不是 Pen 画线：
        /// Pen 在 2.249 缩放下宽度是 2.249 物理 px，抗锯齿会把 #E0E0E0 与背景 #FAFAFA 混色，
        /// 实测渲出色会变成 #EAEAEA / #F0F0F0（偏淡）；矩形填充 + 关闭该次抗锯齿 = 精确 #E0E0E0。
        private static void DrawSeparator(Graphics graphics, PhysRectF slot, double dpiScale)
        {
            int widthPx = Math.Max(1, (int)Math.Round(EditorDefaults.ToolbarSeparatorWidthLogical * dpiScale));
            double lineHeight = slot.Height;
            int left = (int)Math.Round(slot.CenterX - widthPx / 2.0);
            int top = (int)Math.Round(slot.Top + (slot.Height - lineHeight) * 0.5);
            int bottom = (int)Math.Round(slot.Top + (slot.Height + lineHeight) * 0.5);
            if (bottom <= top)
            {
                return;
            }

            SmoothingMode previous = graphics.SmoothingMode;
            graphics.SmoothingMode = SmoothingMode.None;
            try
            {
                using (SolidBrush brush = new SolidBrush(ToColor(EditorDefaults.ToolbarSeparatorArgb)))
                {
                    graphics.FillRectangle(brush, left, top, widthPx, bottom - top);
                }
            }
            finally
            {
                graphics.SmoothingMode = previous;
            }
        }

        private static void DrawSlot(
            Graphics graphics,
            ToolbarState state,
            ToolbarItem item,
            int itemIndex,
            PhysRectF slot,
            double dpiScale)
        {
            if (item.Role == ToolbarItemRole.Separator)
            {
                DrawSeparator(graphics, slot, dpiScale);
                return;
            }

            // 悬停/选中都不画背景块（实测），因此这里不绘制背景。
            Color color = ResolveIconColor(state, item, itemIndex);
            double iconLogical = EditorDefaults.ToolbarIconLogical(state.Style);
            double iconPhys = iconLogical * dpiScale;
            RectangleF box = new RectangleF(
                (float)(slot.CenterX - iconPhys * 0.5),
                (float)(slot.CenterY - iconPhys * 0.5),
                (float)iconPhys,
                (float)iconPhys);

            ToolbarIcons.Draw(graphics, item.Icon, box, color);
        }

        private static Color ResolveIconColor(ToolbarState state, ToolbarItem item, int itemIndex)
        {
            if (!item.Enabled)
            {
                return ToColor(EditorDefaults.ToolbarDisabledArgb);
            }

            if (item.IsDanger)
            {
                return ToColor(EditorDefaults.DangerArgb);
            }

            // 完成/复制是品牌绿且**恒亮**：微信实测 ✓ 始终是 #00C375（不随"有无可撤销"之类状态变灰）。
            // 三重兜底（显式标记 / 图标 / Id 语义）—— 联调时真实出现过"调用方构造按钮漏设 IsConfirm，
            // 于是落进常态灰 #4C4C4C"的缺陷，这里从绘制层根治，不再依赖调用方是否记得设标记。
            if (IsConfirmItem(item))
            {
                return ToColor(EditorDefaults.ConfirmArgb);
            }

            if (!item.Enabled)
            {
                return ToColor(EditorDefaults.ToolbarDisabledArgb);
            }

            bool active = itemIndex == state.ActiveIndex || (item.IsToggle && item.Checked);
            if (active && item.Role == ToolbarItemRole.Tool)
            {
                return ToColor(EditorDefaults.ToolbarSelectedArgb);
            }

            return ToColor(EditorDefaults.ToolbarIconArgb);
        }

        /// <summary>判断是否为「完成/复制」类按钮（恒绿）。</summary>
        private static bool IsConfirmItem(ToolbarItem item)
        {
            if (item.IsConfirm || item.Icon == ToolbarIconKey.Confirm)
            {
                return true;
            }

            string id = item.Id ?? string.Empty;
            return id.EndsWith(".copy", StringComparison.Ordinal)
                || id.EndsWith(".confirm", StringComparison.Ordinal)
                || id.EndsWith(".done", StringComparison.Ordinal);
        }

        private static void DrawSubPanel(
            Graphics graphics,
            ToolbarState state,
            PhysRectF bounds,
            List<PhysRectF> slots,
            double dpiScale)
        {
            switch (state.SubPanel)
            {
                case SubPanelKind.Palette:
                    for (int i = 0; i < slots.Count && i < EditorDefaults.PaletteArgb.Length; i++)
                    {
                        uint argb = EditorDefaults.PaletteArgb[i];
                        DrawColorSwatch(graphics, slots[i], argb, i == state.SubPanelSelectedIndex(), dpiScale);
                    }

                    break;

                case SubPanelKind.Brush:
                    for (int i = 0; i < slots.Count; i++)
                    {
                        ToolbarIconKey icon = i switch
                        {
                            0 => ToolbarIconKey.BrushThin,
                            1 => ToolbarIconKey.BrushMiddle,
                            2 => ToolbarIconKey.BrushThick,
                            _ => ToolbarIconKey.BrushFill,
                        };
                        bool selected = (int)state.Brush == i;
                        Color color = ToColor(selected ? EditorDefaults.ToolbarSelectedArgb : EditorDefaults.ToolbarIconArgb);
                        DrawIconInSlot(graphics, icon, slots[i], color, dpiScale, state.Style);
                    }

                    break;

                case SubPanelKind.TextSize:
                    for (int i = 0; i < slots.Count; i++)
                    {
                        ToolbarIconKey icon = i switch
                        {
                            0 => ToolbarIconKey.TextSizeSmall,
                            1 => ToolbarIconKey.TextSizeMiddle,
                            _ => ToolbarIconKey.TextSizeLarge,
                        };
                        bool selected = (int)state.TextSize == i;
                        Color color = ToColor(selected ? EditorDefaults.ToolbarSelectedArgb : EditorDefaults.ToolbarIconArgb);
                        DrawIconInSlot(graphics, icon, slots[i], color, dpiScale, state.Style);
                    }

                    break;
            }
        }

        private static void DrawIconInSlot(
            Graphics graphics,
            ToolbarIconKey icon,
            PhysRectF slot,
            Color color,
            double dpiScale,
            ToolbarStyle style)
        {
            double iconPhys = EditorDefaults.ToolbarIconLogical(style) * dpiScale;
            RectangleF box = new RectangleF(
                (float)(slot.CenterX - iconPhys * 0.5),
                (float)(slot.CenterY - iconPhys * 0.5),
                (float)iconPhys,
                (float)iconPhys);
            ToolbarIcons.Draw(graphics, icon, box, color);
        }

        private static void DrawColorSwatch(Graphics graphics, PhysRectF slot, uint argb, bool selected, double dpiScale)
        {
            double size = Math.Min(slot.Width, slot.Height) * 0.78;
            float x = (float)(slot.CenterX - size * 0.5);
            float y = (float)(slot.CenterY - size * 0.5);
            float r = (float)(3.0 * dpiScale);
            using (GraphicsPath path = BuildRounded(x, y, (float)size, (float)size, r))
            using (SolidBrush brush = new SolidBrush(ToColor(argb)))
            {
                graphics.FillPath(brush, path);
                using (Pen border = new Pen(Color.FromArgb(40, 0, 0, 0), 1f))
                {
                    graphics.DrawPath(border, path);
                }
            }

            if (selected)
            {
                float ring = (float)(2.0 * dpiScale);
                using (Pen ringPen = new Pen(ToColor(EditorDefaults.PaletteSelectedRingArgb), ring))
                using (GraphicsPath path = BuildRounded(x - ring, y - ring, (float)size + ring * 2f, (float)size + ring * 2f, r + ring))
                {
                    graphics.DrawPath(ringPen, path);
                }
            }
        }

        private static GraphicsPath BuildRounded(float x, float y, float w, float h, float radius)
        {
            GraphicsPath path = new GraphicsPath();
            if (w <= 0f || h <= 0f)
            {
                return path;
            }

            float r = Math.Min(radius, Math.Min(w, h) * 0.5f);
            if (r <= 0.1f)
            {
                path.AddRectangle(new RectangleF(x, y, w, h));
                return path;
            }

            float d = r * 2f;
            path.AddArc(x, y, d, d, 180, 90);
            path.AddArc(x + w - d, y, d, d, 270, 90);
            path.AddArc(x + w - d, y + h - d, d, d, 0, 90);
            path.AddArc(x, y + h - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        private static Color ToColor(uint argb)
        {
            return Color.FromArgb(unchecked((int)argb));
        }
    }

    /// <summary>ToolbarState 的次级面板选中项（色板下标 / 笔触 / 字号）。</summary>
    internal static class ToolbarStateSubPanelExtensions
    {
        /// <summary>色板当前选中项（按 ActiveColorArgb 在色板里的位置，找不到返回 -1）。</summary>
        public static int SubPanelSelectedIndex(this ToolbarState state)
        {
            if (state == null)
            {
                return -1;
            }

            uint[] palette = EditorDefaults.PaletteArgb;
            for (int i = 0; i < palette.Length; i++)
            {
                if (palette[i] == state.ActiveColorArgb)
                {
                    return i;
                }
            }

            return -1;
        }
    }
}
