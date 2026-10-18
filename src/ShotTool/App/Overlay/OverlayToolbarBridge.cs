// ============================================================================
//  App/Overlay/OverlayToolbarBridge.cs —— 把 dev-editor 的工具条「摆到正确位置 + 转发事件」
//
//  ★ 边界（captain 裁决 + docs/02 §9.1）：
//    - **工具条怎么画、命中怎么算，全是 dev-editor 的**（ToolbarPainter / ToolbarLayout /
//      ToolbarState / EditorDefaults，均来自 docs/06-toolbar-spec.md 的实测真值）；
//    - 本文件只做三件事：① 按实测规则算工具条矩形 ② 调它的纯函数绘制 ③ 把鼠标命中文档化
//      成语义动作（关闭/复制/保存/撤销/选工具）交回 overlay。
//    - **不**另开窗口、**不**自己画按钮、**不**持有 ToolbarState 之外的编辑器状态。
//
//  实测定位真值（docs/06-toolbar-spec.md §5）：
//    toolbar.right = selection.right（+1~3 物理 px）
//    toolbar.top   = selection.bottom + 15 逻辑 px（225% → +33 物理 px）
//    下方/右方空间不足时翻转（先翻到选区上方，再夹进虚拟桌面）。
// ============================================================================

using System.Drawing;
using ShotTool.Core;
using ShotTool.Editor.Model;
using ShotTool.Editor.Toolbar;

namespace ShotTool.App.Overlay;

/// <summary>工具条命中后的语义动作（overlay 只认这 7 种）。</summary>
internal enum OverlayToolbarAction
{
    None = 0,

    /// <summary>取消截图。</summary>
    Close,

    /// <summary>复制并退出。</summary>
    Copy,

    /// <summary>保存文件。</summary>
    Save,

    Pin,

    /// <summary>撤销（标注层为空时无实际效果）。</summary>
    Undo,

    /// <summary>切换标注工具（具体绘制归 dev-editor）。</summary>
    SelectTool,

    /// <summary>点在色板/笔触/字号弹层上（P1，当前仅保持状态一致）。</summary>
    StylePanel,
}

/// <summary>工具条宿主桥（定位 + 绘制 + 命中）。</summary>
internal sealed class OverlayToolbarBridge
{
    private readonly ToolbarState _state;
    private PhysPointF _topLeft = PhysPointF.Zero;
    private double _dpiScale = 1.0;

    internal OverlayToolbarBridge()
    {
        _state = ToolbarState.CreateDefault();
    }

    /// <summary>工具条状态（dev-editor 的模型；本层只改 ActiveIndex / Hover / Pressed）。</summary>
    internal ToolbarState State => _state;

    /// <summary>当前是否应显示（有选区才显示）。</summary>
    internal bool IsVisible { get; private set; }

    /// <summary>主条矩形（局部坐标 = 虚拟桌面 - 原点，物理像素）。</summary>
    internal PhysRect Bounds { get; private set; }

    /// <summary>主条 + 次级面板的联合矩形（脏矩形用）。</summary>
    internal PhysRect OverallBounds { get; private set; }

    /// <summary>当前激活工具（null = 无）。</summary>
    internal AnnotationKind? ActiveTool => _state.ActiveTool;

    /// <summary>按选区与虚拟桌面重算工具条位置。无选区时隐藏。</summary>
    internal void Layout(PhysRect? selection, PhysRect virtualBounds, uint dpi)
    {
        _dpiScale = DpiHelper.ScaleFactor(DpiHelper.Sanitize(dpi));

        if (selection is null || selection.Value.IsEmpty)
        {
            IsVisible = false;
            Bounds = PhysRect.Empty;
            OverallBounds = PhysRect.Empty;
            return;
        }

        PhysRect sel = selection.Value;

        // 1) 先量尺寸（topLeft 用 0,0 探一次即可，尺寸与位置无关）
        _topLeft = PhysPointF.Zero;
        ToolbarGeometry probe = ToolbarLayout.Compute(_state, _topLeft, _dpiScale);
        int width = (int)Math.Ceiling(probe.Bounds.Width);
        int height = (int)Math.Ceiling(probe.Bounds.Height);

        // 2) 右对齐 + 选区下方 15 逻辑 px
        int localSelectionRight = sel.Right - virtualBounds.X;
        int localSelectionBottom = sel.Bottom - virtualBounds.Y;
        int localSelectionTop = sel.Top - virtualBounds.Y;

        int left = localSelectionRight - width + (int)Math.Round(_dpiScale, MidpointRounding.AwayFromZero);
        int top = localSelectionBottom + DpiHelper.DipToPx(15.0, DpiHelper.Sanitize(dpi));

        // 3) 下方放不下 → 翻到选区上方
        if (top + height > virtualBounds.Height)
        {
            int above = localSelectionTop - DpiHelper.DipToPx(15.0, DpiHelper.Sanitize(dpi)) - height;
            top = above >= 0 ? above : Math.Max(0, virtualBounds.Height - height);
        }

        // 4) 水平夹紧
        left = Math.Clamp(left, 0, Math.Max(0, virtualBounds.Width - width));

        _topLeft = new PhysPointF(left, top);

        ToolbarGeometry geometry = ToolbarLayout.Compute(_state, _topLeft, _dpiScale);
        Bounds = new PhysRect(left, top, width, height);
        PhysRectF overall = geometry.OverallBounds;
        OverallBounds = new PhysRect(
            (int)Math.Floor(overall.X),
            (int)Math.Floor(overall.Y),
            (int)Math.Ceiling(overall.Width),
            (int)Math.Ceiling(overall.Height));

        IsVisible = true;
    }

    /// <summary>绘制工具条（纯函数转调 dev-editor 的 ToolbarPainter）。</summary>
    internal void Paint(Graphics graphics, uint dpi)
    {
        if (!IsVisible)
        {
            return;
        }

        ToolbarPainter.Paint(graphics, _state, _topLeft, DpiHelper.ScaleFactor(DpiHelper.Sanitize(dpi)));
    }

    /// <summary>命中测试（局部坐标）。</summary>
    internal bool ContainsPoint(PhysPoint localPoint)
        => IsVisible && OverallBounds.Contains(localPoint);

    /// <summary>
    /// 处理鼠标移动：刷新 hover 态（返回 true 表示需要重绘工具条）。
    /// </summary>
    internal bool OnMouseMove(PhysPoint localPoint)
    {
        if (!IsVisible)
        {
            return false;
        }

        ToolbarHit hit = ToolbarPainter.HitTest(
            _state,
            _topLeft,
            new PhysPointF(localPoint.X, localPoint.Y),
            _dpiScale);

        int hover = hit.Kind == ToolbarHitKind.Button ? hit.MainIndex : -1;
        if (hover == _state.HoverIndex)
        {
            return false;
        }

        _state.HoverIndex = hover;
        return true;
    }

    /// <summary>鼠标离开工具条时清 hover。</summary>
    internal bool ClearHover()
    {
        if (_state.HoverIndex < 0 && _state.PressedIndex < 0)
        {
            return false;
        }

        _state.HoverIndex = -1;
        _state.PressedIndex = -1;
        return true;
    }

    /// <summary>
    /// 鼠标按下：记录 pressed，并把命中翻译成语义动作。
    /// 返回动作；<paramref name="toolId"/> 在 SelectTool 时给出被点的工具 id。
    /// </summary>
    internal OverlayToolbarAction OnMouseDown(PhysPoint localPoint, out string? toolId)
    {
        toolId = null;
        if (!IsVisible)
        {
            return OverlayToolbarAction.None;
        }

        ToolbarHit hit = ToolbarPainter.HitTest(
            _state,
            _topLeft,
            new PhysPointF(localPoint.X, localPoint.Y),
            _dpiScale);

        if (hit.Kind == ToolbarHitKind.None)
        {
            return OverlayToolbarAction.None;
        }

        switch (hit.Kind)
        {
            case ToolbarHitKind.SubPanelItem:
                if (hit.SubIndex >= 0)
                {
                    switch (_state.SubPanel)
                    {
                        case SubPanelKind.Palette when hit.SubIndex < EditorDefaults.PaletteArgb.Length:
                            _state.ActiveColorArgb = EditorDefaults.PaletteArgb[hit.SubIndex]; break;
                        case SubPanelKind.Brush when hit.SubIndex < 4:
                            _state.Brush = (BrushLevel)hit.SubIndex; break;
                        case SubPanelKind.TextSize when hit.SubIndex < 3:
                            _state.TextSize = (TextSizeLevel)hit.SubIndex; break;
                    }
                }
                return OverlayToolbarAction.StylePanel;
            case ToolbarHitKind.SubPanelBody:
                return OverlayToolbarAction.StylePanel;

            case ToolbarHitKind.Separator:
                return OverlayToolbarAction.None;

            case ToolbarHitKind.Button:
                if (hit.MainIndex < 0 || hit.MainIndex >= _state.Items.Count)
                {
                    return OverlayToolbarAction.None;
                }

                ToolbarItem item = _state.Items[hit.MainIndex];
                _state.PressedIndex = hit.MainIndex;

                switch (item.Id)
                {
                    case "action.close":
                        return OverlayToolbarAction.Close;
                    case "action.copy":
                        return OverlayToolbarAction.Copy;
                    case "action.save":
                        return OverlayToolbarAction.Save;
                    case "action.pin":
                        return OverlayToolbarAction.Pin;
                    case "action.undo":
                        return OverlayToolbarAction.Undo;
                }

                if (item.Role == ToolbarItemRole.Tool)
                {
                    // 选中态交给 dev-editor 的模型表达（工具实际绘制由 Editor 负责）
                    _state.ActiveIndex = _state.ActiveIndex == hit.MainIndex ? -1 : hit.MainIndex;
                    _state.SubPanel = _state.ActiveIndex < 0 ? SubPanelKind.None
                        : item.Tool == AnnotationKind.Text ? SubPanelKind.TextSize : SubPanelKind.Palette;
                    toolId = item.Id;
                    return OverlayToolbarAction.SelectTool;
                }

                if (item.Role == ToolbarItemRole.Extension && item.Enabled)
                {
                    toolId = item.Id;
                    return OverlayToolbarAction.SelectTool;
                }

                return OverlayToolbarAction.None;

            default:
                return OverlayToolbarAction.None;
        }
    }

    /// <summary>鼠标抬起：清 pressed。返回是否需要重绘。</summary>
    internal bool OnMouseUp()
    {
        if (_state.PressedIndex < 0)
        {
            return false;
        }

        _state.PressedIndex = -1;
        return true;
    }
}
