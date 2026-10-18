// ============================================================================
//  App/Overlay/SelectionOverlayForm.cs —— 全屏框选覆盖层（本任务的交付核心）
//
//  形态（ADR-003，与微信 Alt+A 实测一致）：
//    * **单个 HWND**，Bounds == 虚拟桌面（物理像素，可为负原点），WS_EX_TOOLWINDOW（不进 Alt+Tab），
//      不透明（不使用 layered 窗口），TopMost；
//    * 抓屏位图当背景贴图 → 遮罩 → 边框 → 控制柄 → 尺寸 HUD → 放大镜 → 工具条 → 扩展叠加层；
//    * 工具条**同窗口自绘**（由 OverlayToolbarBridge 调 dev-editor 的 ToolbarPainter），
//      不另开窗口：避免第二个 topmost 抢焦点、避免 Alt+Tab 出现两条目、避免双 DPI 上下文。
//
//  坐标系（ADR-005，最容易错的地方）：
//    overlay 客户区坐标 ≡ 虚拟桌面坐标 - VirtualDesktopBounds.Location。
//    PMv2 下 WinForms 的 MouseEventArgs 就是**物理像素**，因此：
//        virtual = (e.X + vd.X, e.Y + vd.Y)      ← 不做任何 DPI 换算
//    本文件里除 OverlayMetrics 之外**不允许**出现 DIP→px 换算。
//
//  交互（docs/02 §8.4）：
//    左键拖 → 框选；框选后拖 Body → 平移；拖 8 个柄 → 调整；双击 / Enter → 完成；
//    右键 / Esc → 取消；Ctrl+A → 全屏；Ctrl+1..6 → 切工具；滚轮 → 放大镜倍率。
//
//  释放契约（完成与取消两条路径都必须无泄漏）：
//    本 Form 只持有「它自己 new 出来的东西」：_dimmed 位图、_magnifier（字体）；
//    CapturedFrame **不**由本类 Dispose（由 OverlayShotUi 的 using 负责）；
//    选区位图由 ComposeSelectionBitmap() 每次新生成，所有权交给调用方。
// ============================================================================

using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using ShotTool.Core;
using ShotTool.Editor.Toolbar;
using ShotTool.Interop;

namespace ShotTool.App.Overlay;

/// <summary>overlay 的终局：完成（带方式与选区）或取消。</summary>
internal readonly struct OverlayOutcome
{
    private OverlayOutcome(bool completed, ShotCompletion mode, PhysRect selection)
    {
        Completed = completed;
        Mode = mode;
        Selection = selection;
    }

    internal bool Completed { get; }

    internal ShotCompletion Mode { get; }

    internal PhysRect Selection { get; }

    internal static OverlayOutcome Cancelled => new(false, ShotCompletion.Copy, PhysRect.Empty);

    internal static OverlayOutcome Done(ShotCompletion mode, PhysRect selection) => new(true, mode, selection);
}

/// <summary>全屏框选覆盖层。</summary>
internal sealed partial class SelectionOverlayForm : Form, IOverlayHost, IShotSession
{
    private const int WM_MOUSEACTIVATE = 0x0021;
    private const int MA_ACTIVATE = 1;

    private readonly ShotToolContext _ctx;
    private readonly CapturedFrame _frame;
    private readonly PhysRect _virtualBounds;
    private readonly Bitmap _dimmed;
    private readonly MagnifierRenderer _magnifier;
    private readonly OverlayToolbarBridge _toolbar = new();
    private readonly ToolTip _toolbarTip = new() { InitialDelay = 500, ReshowDelay = 100, AutoPopDelay = 3000 };
    private string _toolbarTipText = string.Empty;
    private readonly TaskCompletionSource<OverlayOutcome> _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private OverlayMetrics _metrics;
    private uint _dpi;

    private PhysRect? _selection;
    private PhysPoint _cursorVirtual;
    private DragMode _drag;
    private SelectionHandle _dragHandle;
    private PhysPoint _dragOriginVirtual;
    private PhysRect _dragSelection;
    private PhysRect _lastFootprint;
    private OverlayOutcome? _outcome;
    private bool _closing;

    public SelectionOverlayForm(ShotToolContext context, CapturedFrame frame, ShotCompletion? forcedMode = null)
    {
        _ctx = context ?? throw new ArgumentNullException(nameof(context));
        _frame = frame ?? throw new ArgumentNullException(nameof(frame));
        _virtualBounds = frame.VirtualDesktopBounds;
        ForcedMode = forcedMode;

        _dpi = ResolveDpi(frame.CursorPosition);
        _metrics = OverlayMetrics.ForDpi(_dpi);

        // ---- 预暗化整屏：把「半透明黑」这一步从每帧绘制里拿掉 ----
        // 每帧 alpha 混合 33 MB 会明显掉帧（拖框时每像素移动一次），
        // 预合成一次后，绘制退化为纯 BitBlt 拷贝。
        _dimmed = BuildDimmed(frame.Bitmap);

        _magnifier = new MagnifierRenderer(frame.Bitmap, _virtualBounds);
        _cursorVirtual = frame.CursorPosition;

        AutoScaleMode = AutoScaleMode.None;      // 我们按物理像素自己换算，不让 WinForms 再缩放一次
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = Environment.GetCommandLineArgs().Contains("--diagnostic-window");
        TopMost = true;
        KeyPreview = true;
        BackColor = Color.Black;
        Cursor = Cursors.Cross;
        Bounds = _virtualBounds.ToRectangle();

        // 鼠标移动会更新十字线与放大镜，重绘包含背景、选区与工具条多层。
        // 必须先在缓冲区合成完整帧再呈现，否则用户会看到各层依次覆盖的中间态。
        // Opaque / AllPaintingInWmPaint 同时避免独立的背景擦除；仍保留脏区域失效。
        SetStyle(
            ControlStyles.Opaque | ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint
            | ControlStyles.OptimizedDoubleBuffer,
            true);

        Text = "ShotTool 截图";
        InitializeEditor();
    }

    /// <summary>诊断模式：不抢焦点、不因失活而取消（自测渲染用）。</summary>
    internal bool DiagnosticMode { get; set; }

    protected override bool ShowWithoutActivation => DiagnosticMode;

    /// <summary>强制完成方式（托盘「保存截图」等入口用；null = 由用户按键决定）。</summary>
    internal ShotCompletion? ForcedMode { get; }

    /// <summary>当前选区（虚拟桌面坐标）。同时是 <see cref="IShotSession.Selection"/> 的实现。</summary>
    public PhysRect? Selection => _selection;

    /// <summary>当前光标（虚拟桌面坐标）。</summary>
    internal PhysPoint CursorVirtual => _cursorVirtual;

    /// <summary>当前 overlay 使用的 DPI。</summary>
    internal uint EffectiveDpi => _dpi;

    // ------------------------------------------------------------------ 生命周期

    protected override CreateParams CreateParams
    {
        get
        {
            CreateParams parameters = base.CreateParams;
            // 不进 Alt+Tab / 不显示在任务视图；保持 TopMost
            parameters.ExStyle |= NativeConstants.WS_EX_TOPMOST;
            if (!ShowInTaskbar) parameters.ExStyle |= NativeConstants.WS_EX_TOOLWINDOW;
            return parameters;
        }
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);

        if (DiagnosticMode)
        {
            return;
        }

        // 抢前台：否则键盘（Esc/Enter/Ctrl+Z）到不了 overlay
        NativeMethods.SetForegroundWindow(Handle);
        Activate();
        NativeMethods.SetFocus(Handle);

        _ctx.Log.Info(
            $"overlay 已显示：{_virtualBounds.Width}x{_virtualBounds.Height} @({_virtualBounds.X},{_virtualBounds.Y})，" +
            $"DPI={_dpi}，光标=({_cursorVirtual.X},{_cursorVirtual.Y})。");

        InvalidateOverlay(_lastFootprint);
    }

    protected override void OnDeactivate(EventArgs e)
    {
        base.OnDeactivate(e);

        if (DiagnosticMode || _closing || _ownedDialog || _phase == ShotPhase.Committing)
        {
            return;
        }

        // 用户点了别的窗口 / 托盘菜单：与微信一致，直接放弃本次截图
        _ctx.Log.Info("overlay 失活，取消本次截图。");
        Cancel();
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_MOUSEACTIVATE)
        {
            m.Result = MA_ACTIVATE;
            return;
        }

        base.WndProc(ref m);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        base.OnFormClosed(e);

        OverlayOutcome outcome = _outcome ?? OverlayOutcome.Cancelled;
        _completion.TrySetResult(outcome);
    }

    /// <summary>
    /// 显示并等待用户操作。**非模态** Show()：让托盘的消息循环继续跑，
    /// 同时把结果通过 TaskCompletionSource 交回调用方。
    /// </summary>
    internal Task<OverlayOutcome> ShowAndWaitAsync()
    {
        var tcs = _completion.Task;
        Show();
        return tcs;
    }

    /// <summary>外部请求取消（热键重按 / 托盘「取消截图」）。</summary>
    internal void RequestCancel()
    {
        if (IsHandleCreated && !IsDisposed)
        {
            Cancel();
        }
    }

    /// <summary>取消（Esc / 右键 / 失活）。</summary>
    public void Cancel()
    {
        FinishSession(OverlayOutcome.Cancelled);
    }

    private void Complete(ShotCompletion mode)
    {
        if (_selection is null || _selection.Value.IsEmpty)
        {
            return;
        }

        _ = CompleteAsync(mode);
    }

    private void FinishSession(OverlayOutcome outcome)
    {
        if (_closing)
        {
            return;
        }

        _closing = true;
        _outcome = outcome;

        _ctx.Log.Info(outcome.Completed
            ? $"截图完成：方式={outcome.Mode}，选区={outcome.Selection}"
            : "截图已取消。");

        Close();
    }

    /// <summary>
    /// 合成选区位图（每次调用都新建，所有权归调用方）。
    /// 由 OverlayShotUi 在完成路径调用 —— 这是「位图交给下游」的唯一出口。
    /// </summary>
    internal Bitmap? ComposeSelectionBitmap()
    {
        if (_selection is null)
        {
            return null;
        }

        return ShotTool.Capture.SelectionCropper.Crop(_frame, _selection.Value);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _toastTimer?.Dispose();
            _toastTimer = null;
            _magnifier.Dispose();
            _dimmed.Dispose();
            _toolbarTip.Dispose();
            _editorHost?.Dispose();
            _annotationComposite?.Dispose();
        }

        base.Dispose(disposing);
    }

    // ------------------------------------------------------------------ 绘制

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        PaintScene(e.Graphics);
    }

    /// <summary>
    /// 绘制整场景（固定顺序，ADR §9.2）。internal 以便 --selftest-overlay 离屏渲染取证。
    /// </summary>
    internal void PaintScene(Graphics graphics)
    {
        graphics.SmoothingMode = SmoothingMode.None;
        graphics.PixelOffsetMode = PixelOffsetMode.Half;

        PhysRect? selection = _selection;
        Rectangle localSelection = selection is null
            ? Rectangle.Empty
            : ToLocalRect(selection.Value);

        // ---- ① 背景：暗化整屏 ----
        graphics.DrawImageUnscaled(_dimmed, 0, 0);

        // ---- ② 选区保持原亮度（从原图再贴一次，1:1，无缩放）----
        if (selection is not null && !localSelection.IsEmpty)
        {
            Rectangle sourceRect = _frame.ToBitmapRect(selection.Value);
            if (!sourceRect.IsEmpty)
            {
                graphics.CompositingMode = CompositingMode.SourceCopy;
                graphics.DrawImage(
                    _frame.Bitmap,
                    localSelection,
                    sourceRect,
                    GraphicsUnit.Pixel);
                graphics.CompositingMode = CompositingMode.SourceOver;
            }
        }

        // ---- ③ 未框选：全屏十字准星 ----
        if (selection is null)
        {
            SelectionPainter.PaintCrosshair(graphics, _cursorVirtual, _virtualBounds, _metrics);
        }

        // ---- ④ 选区边框 + 8 控制柄 ----
        if (selection is not null && !localSelection.IsEmpty)
        {
            PhysRect localRect = ToLocalPhysRect(selection.Value);
            SelectionPainter.PaintBorder(graphics, localRect, _metrics);
            SelectionPainter.PaintHandles(graphics, localRect, _metrics);

            // ---- ④⑤ 标注层：**必须在 HUD / 放大镜 / 工具条之前**（ADR §9.2 固定序），
            //      否则标注会盖住尺寸提示与工具条 ----
            PaintAnnotationsAndToast(graphics, selection, sinceHandles: true);

            // ---- ⑥ 尺寸 HUD（物理像素，跟随框选实时变化）----
            using Font hudFont = OverlayFonts.Create(_metrics.HudFontPx, FontStyle.Bold);
            SelectionPainter.PaintSizeHud(
                graphics,
                localRect,
                new PhysRect(0, 0, _virtualBounds.Width, _virtualBounds.Height),
                _metrics,
                hudFont,
                live: _drag == DragMode.Creating);
        }

        // ---- ⑥ 放大镜 + 像素信息 ----
        if (ShouldShowMagnifier())
        {
            EnsureMagnifierMeasured();
            Color pixel = SamplePixel(_cursorVirtual);
            _magnifier.Paint(
                graphics,
                _cursorVirtual,
                _metrics,
                pixel,
                FormatHex(pixel),
                FormatRgb(pixel),
                $"({_cursorVirtual.X}, {_cursorVirtual.Y})");
        }

        // ---- ⑨ toast（无选区时也要能显示，所以单独调用；标注层已在上面画过）----
        PaintAnnotationsAndToast(graphics, selection, sinceHandles: false);

        // ---- ⑦ 工具条（dev-editor 绘制，本层只定位）----
        if (_toolbar.IsVisible)
        {
            _toolbar.Paint(graphics, _dpi);
        }

        // ---- ⑧ 扩展叠加层（dev-ai 注册；本层不认识其内容）----
        PaintExtensions(graphics, selection);
    }

    private void PaintExtensions(Graphics graphics, PhysRect? selection)
    {
        if (_ctx.Painters is null || selection is null)
        {
            return;
        }

        var context = new AnnotationPaintContext
        {
            Dpi = _dpi,
            Selection = selection.Value,
            BitmapOrigin = new PhysPoint(_virtualBounds.X, _virtualBounds.Y),
            IsPreview = _drag != DragMode.None,
        };

        foreach (IAnnotationOverlayPainter painter in _ctx.Painters.ResolveAll())
        {
            try
            {
                if (painter.ShouldPaint(context))
                {
                    painter.Paint(graphics, context);
                }
            }
            catch (Exception ex)
            {
                // 第三方叠加层异常不能打断截图
                _ctx.Log.Error($"扩展叠加层 {painter.GetType().Name} 绘制失败。", ex);
            }
        }
    }

    /// <summary>把「半透明黑」预合成进一张位图（一次，避免每帧混合）。</summary>
    private static Bitmap BuildDimmed(Bitmap source)
    {
        var dimmed = new Bitmap(source.Width, source.Height, PixelFormat.Format32bppPArgb);
        using Graphics graphics = Graphics.FromImage(dimmed);
        graphics.CompositingMode = CompositingMode.SourceCopy;
        graphics.DrawImageUnscaled(source, 0, 0);
        graphics.CompositingMode = CompositingMode.SourceOver;
        using var brush = new SolidBrush(OverlayColors.Mask);
        graphics.FillRectangle(brush, 0, 0, source.Width, source.Height);
        return dimmed;
    }

    // ------------------------------------------------------------------ 鼠标

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        UpdateCursorPosition(e.Location);

        if (_drawing)
        {
            UpdateAnnotation();
            InvalidateOverlay(_lastFootprint);
            return;
        }

        switch (_drag)
        {
            case DragMode.Creating:
            {
                PhysRect rect = PhysRect.FromPoints(_dragOriginVirtual, _cursorVirtual).Clamp(_virtualBounds);
                _selection = rect.IsEmpty ? null : rect;
                break;
            }

            case DragMode.Moving:
            {
                int dx = _cursorVirtual.X - _dragOriginVirtual.X;
                int dy = _cursorVirtual.Y - _dragOriginVirtual.Y;
                _selection = SelectionGeometry.ResizeBy(
                    _dragSelection,
                    SelectionHandle.Body,
                    dx,
                    dy,
                    SelectionGeometry.DefaultMinimumSelection,
                    _virtualBounds);
                break;
            }

            case DragMode.Resizing:
            {
                int dx = _cursorVirtual.X - _dragOriginVirtual.X;
                int dy = _cursorVirtual.Y - _dragOriginVirtual.Y;
                _selection = SelectionGeometry.ResizeBy(
                    _dragSelection,
                    _dragHandle,
                    dx,
                    dy,
                    SelectionGeometry.DefaultMinimumSelection,
                    _virtualBounds);
                break;
            }

            default:
            {
                // 悬停：更新工具条 hover + 光标形状（工具条命中优先）
                PhysPoint local = ToLocalPoint(_cursorVirtual);
                _toolbar.OnMouseMove(local);
                int hover = _toolbar.State.HoverIndex;
                string tip = hover >= 0 ? _toolbar.State.Items[hover].Label : string.Empty;
                if (tip != _toolbarTipText) { _toolbarTipText = tip; _toolbarTip.SetToolTip(this, tip); }
                break;
            }
        }

        UpdateCursorShape();
        InvalidateOverlay(_lastFootprint);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        UpdateCursorPosition(e.Location);

        if (e.Button == MouseButtons.Right)
        {
            // 与微信一致：右键任意状态 → 取消整个截图
            Cancel();
            return;
        }

        if (e.Button != MouseButtons.Left)
        {
            return;
        }

        PhysPoint local = ToLocalPoint(_cursorVirtual);

        if (_phase == ShotPhase.Committing) return;

        // ① 工具条命中优先（工具条在选区外，必须先判）
        if (_toolbar.IsVisible && _toolbar.ContainsPoint(local))
        {
            OverlayToolbarAction action = _toolbar.OnMouseDown(local, out string? toolId);
            HandleToolbarAction(action, toolId);
            InvalidateOverlay(_lastFootprint);
            return;
        }

        if (BeginAnnotation()) return;

        // ② 8 个控制柄 / 选区本体
        if (_selection is not null)
        {
            SelectionHandle handle = SelectionGeometry.HitTest(
                _selection.Value,
                _cursorVirtual,
                _metrics.HandleHitPx);

            if (handle is not SelectionHandle.None)
            {
                _drag = handle == SelectionHandle.Body ? DragMode.Moving : DragMode.Resizing;
                _dragHandle = handle;
                _dragOriginVirtual = _cursorVirtual;
                _dragSelection = _selection.Value;
                Capture = true;
                return;
            }
        }

        // ③ 新框选（点了选区外 = 重开一个框，与微信一致）
        _history.Clear();
        _toolbar.State.ActiveIndex = -1;
        _selection = null;
        _toolbar.Layout(null, _virtualBounds, _dpi);
        _drag = DragMode.Creating;
        _dragOriginVirtual = _cursorVirtual;
        _dragSelection = PhysRect.Empty;
        Capture = true;
        InvalidateOverlay(_lastFootprint);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);

        if (e.Button != MouseButtons.Left)
        {
            return;
        }

        Capture = false;
        _toolbar.OnMouseUp();

        if (_drawing)
        {
            UpdateCursorPosition(e.Location);
            UpdateAnnotation();
            _drawing = false;
            if (_previewAnnotation is { } annotation && !((IAnnotation)annotation).IsEmpty)
                _history.Add(annotation);
            _previewAnnotation = null;
            ResetAnnotationComposite();
            InvalidateOverlay(_lastFootprint);
            return;
        }

        if (_drag == DragMode.Creating)
        {
            PhysSize minimum = SelectionGeometry.DefaultMinimumSelection;
            if (_selection is { } rect && rect.Width >= minimum.Width && rect.Height >= minimum.Height)
            {
                _ctx.Log.Info($"框选成立：{rect}（{rect.Width}x{rect.Height}）。");
                _toolbar.Layout(rect, _virtualBounds, _dpi);
            }
            else
            {
                // 误触（< 4px）：丢弃，回到全屏十字态
                _selection = null;
                _toolbar.Layout(null, _virtualBounds, _dpi);
            }
        }

        _drag = DragMode.None;
        _dragHandle = SelectionHandle.None;
        SetPhase(_selection is null ? ShotPhase.Selecting : ShotPhase.Annotating);
        _toolbar.Layout(_selection, _virtualBounds, _dpi);
        ResetAnnotationComposite();
        UpdateCursorShape();
        InvalidateOverlay(_lastFootprint);
    }

    protected override void OnMouseDoubleClick(MouseEventArgs e)
    {
        base.OnMouseDoubleClick(e);

        if (e.Button != MouseButtons.Left || _selection is null || _toolbar.ActiveTool is not null)
        {
            return;
        }

        // 双击选区内部 = 复制并退出（docs/02 §8.4）
        if (!_selection.Value.Contains(_cursorVirtual))
        {
            return;
        }

        Complete(ForcedMode ?? ShotCompletion.Copy);
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);

        // 滚轮切换放大镜倍率（4/8/12/16）
        _metrics = _metrics.WithNextZoom(e.Delta);
        _ctx.Log.Info($"放大镜倍率切换为 {_metrics.Zoom}x。");
        InvalidateOverlay(_lastFootprint);
    }

    private void HandleToolbarAction(OverlayToolbarAction action, string? toolId)
    {
        switch (action)
        {
            case OverlayToolbarAction.Close:
                Cancel();
                return;

            case OverlayToolbarAction.Copy:
                Complete(ShotCompletion.Copy);
                return;

            case OverlayToolbarAction.Save:
                Complete(ShotCompletion.Save);
                return;

            case OverlayToolbarAction.Pin:
                Complete(ShotCompletion.Pin);
                return;

            case OverlayToolbarAction.Undo:
                _history.Undo();
                return;

            case OverlayToolbarAction.SelectTool:
                if (toolId is not null && toolId.StartsWith("ext.", StringComparison.Ordinal))
                    InvokeExtension(toolId);
                _toolbar.Layout(_selection, _virtualBounds, _dpi);
                return;

            case OverlayToolbarAction.StylePanel:
                return;
        }
    }

    // ------------------------------------------------------------------ 键盘

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        // 结果面板中的 Ctrl+C/A、Tab 等交给文本框处理，不能触发截图复制/全屏框选。
        if (_editorHost?.CurrentPanel is { ContainsFocus: true } && keyData != Keys.Escape)
            return base.ProcessCmdKey(ref msg, keyData);
        // 先给编辑器（本窗口）处理，避免误传给托盘
        switch (keyData)
        {
            case Keys.Tab:
                if (_toolbar.ActiveTool is not null)
                {
                    _toolbar.State.SubPanel = _toolbar.State.SubPanel == SubPanelKind.Palette
                        ? (_toolbar.ActiveTool == ShotTool.Editor.Model.AnnotationKind.Text ? SubPanelKind.TextSize : SubPanelKind.Brush)
                        : SubPanelKind.Palette;
                    _toolbar.Layout(_selection, _virtualBounds, _dpi);
                    InvalidateOverlay(_lastFootprint);
                }
                return true;
            case Keys.Escape:
                if (_editorHost?.CurrentPanel is not null) { _editorHost.ShowPanel(null); return true; }
                Cancel();
                return true;

            case Keys.Enter:
            case Keys.Control | Keys.C:
                if (_selection is not null)
                {
                    Complete(ForcedMode ?? ShotCompletion.Copy);
                    return true;
                }

                return true;

            case Keys.Control | Keys.S:
                if (_selection is not null)
                {
                    Complete(ShotCompletion.Save);
                    return true;
                }

                return true;

            case Keys.F3:
                Complete(ShotCompletion.Pin);
                return true;

            case Keys.Control | Keys.A:
                // 全屏框选
                SelectFullVirtualDesktop();
                return true;
            case Keys.Control | Keys.Z:
                _history.Undo();
                return true;
            case Keys.Control | Keys.Y:
            case Keys.Control | Keys.Shift | Keys.Z:
                _history.Redo();
                return true;
        }

        // Ctrl+1..6 → 切工具（按工具条里 Role == Tool 的顺序）
        if ((keyData & Keys.Control) == Keys.Control)
        {
            Keys pressed = keyData & Keys.KeyCode;
            if (pressed is >= Keys.D1 and <= Keys.D6)
            {
                int index = pressed - Keys.D1;
                SelectToolByOrdinal(index);
                return true;
            }
        }

        return base.ProcessCmdKey(ref msg, keyData);
    }

    private void SelectFullVirtualDesktop()
    {
        _selection = _virtualBounds;
        _drag = DragMode.None;
        _toolbar.Layout(_selection, _virtualBounds, _dpi);
        _ctx.Log.Info($"Ctrl+A：全屏选区 {_selection.Value}。");
        UpdateCursorShape();
        InvalidateOverlay(_lastFootprint);
    }

    private void SelectToolByOrdinal(int ordinal)
    {
        IReadOnlyList<ToolbarItem> items = _toolbar.State.Items;
        int seen = -1;
        for (int i = 0; i < items.Count; i++)
        {
            if (items[i].Role != ToolbarItemRole.Tool)
            {
                continue;
            }

            seen++;
            if (seen != ordinal)
            {
                continue;
            }

            _toolbar.State.ActiveIndex = i;
            _toolbar.State.SubPanel = items[i].Tool == ShotTool.Editor.Model.AnnotationKind.Text
                ? SubPanelKind.TextSize : SubPanelKind.Palette;
            _toolbar.Layout(_selection, _virtualBounds, _dpi);
            _ctx.Log.Info($"Ctrl+{ordinal + 1}：选中工具 {items[i].Id}。");
            InvalidateOverlay(_lastFootprint);
            return;
        }
    }

    // ------------------------------------------------------------------ 状态与坐标

    private enum DragMode
    {
        None = 0,
        Creating = 1,
        Moving = 2,
        Resizing = 3,
    }

    private void UpdateCursorPosition(Point clientPoint)
    {
        _cursorVirtual = new PhysPoint(clientPoint.X + _virtualBounds.X, clientPoint.Y + _virtualBounds.Y);

        uint dpi = ResolveDpi(_cursorVirtual);
        if (dpi != _dpi)
        {
            // 跨到另一块不同 DPI 的屏：重建度量（工具条/放大镜尺寸随之更新）
            _dpi = dpi;
            _metrics = OverlayMetrics.ForDpi(dpi);
            _toolbar.Layout(_selection, _virtualBounds, _dpi);
        }
    }

    private uint ResolveDpi(PhysPoint virtualPoint)
    {
        MonitorInfo? monitor = _frame.MonitorAt(virtualPoint);
        return monitor?.Dpi ?? (uint)DpiHelper.DefaultDpi;
    }

    private PhysPoint ToLocalPoint(PhysPoint virtualPoint)
        => new(virtualPoint.X - _virtualBounds.X, virtualPoint.Y - _virtualBounds.Y);

    private Rectangle ToLocalRect(PhysRect virtualRect)
        => new(
            virtualRect.X - _virtualBounds.X,
            virtualRect.Y - _virtualBounds.Y,
            Math.Max(0, virtualRect.Width),
            Math.Max(0, virtualRect.Height));

    private PhysRect ToLocalPhysRect(PhysRect virtualRect)
        => new(
            virtualRect.X - _virtualBounds.X,
            virtualRect.Y - _virtualBounds.Y,
            virtualRect.Width,
            virtualRect.Height);

    private bool ShouldShowMagnifier()
    {
        // 放大镜在框选中/未框选都显示；指针必须在虚拟桌面内（否则采不到像素）
        bool enabled = _ctx.Settings?.App.ShowMagnifier ?? true;
        return enabled && _virtualBounds.Contains(_cursorVirtual);
    }

    private void UpdateCursorShape()
    {
        if (_toolbar.IsVisible && _toolbar.ContainsPoint(ToLocalPoint(_cursorVirtual)))
        {
            Cursor = Cursors.Hand;
            return;
        }

        if (_selection is null)
        {
            Cursor = Cursors.Cross;
            return;
        }

        if (_toolbar.ActiveTool is not null && _selection.Value.Contains(_cursorVirtual))
        {
            Cursor = Cursors.Cross;
            return;
        }

        SelectionHandle handle = SelectionGeometry.HitTest(
            _selection.Value,
            _cursorVirtual,
            _metrics.HandleHitPx);

        Cursor = handle switch
        {
            SelectionHandle.NW or SelectionHandle.SE => Cursors.SizeNWSE,
            SelectionHandle.NE or SelectionHandle.SW => Cursors.SizeNESW,
            SelectionHandle.N or SelectionHandle.S => Cursors.SizeNS,
            SelectionHandle.E or SelectionHandle.W => Cursors.SizeWE,
            SelectionHandle.Body => Cursors.SizeAll,
            _ => Cursors.Cross,
        };
    }

    // ------------------------------------------------------------------ 脏矩形

    /// <summary>
    /// 计算当前「overlay 自己画的东西」占用的矩形（局部坐标），用于精确失效。
    /// 只包含会变化的内容：十字准星（仅无选区时）、选区外扩、工具条、放大镜、HUD。
    /// </summary>
    private PhysRect ComputeFootprint()
    {
        int border = Math.Max(1, _metrics.BorderWidthPx);
        int inflate = _metrics.HandleSizePx + border;

        PhysRect footprint = PhysRect.Empty;

        if (_selection is { } selection && !selection.IsEmpty)
        {
            PhysRect localSelection = ToLocalPhysRect(selection);

            // 边框 + 控制柄的溢出量
            footprint = localSelection.Inflate(inflate);

            // 尺寸 HUD 位于选区上方（或内部顶端）：多估一行字高 + 内边距
            int hudEstimate = (int)Math.Ceiling(_metrics.HudFontPx) + (_metrics.HudPaddingPx * 3);
            footprint = footprint.Union(localSelection.Inflate(hudEstimate));
        }
        else
        {
            // 十字准星：横竖两条带
            int vertical = _cursorVirtual.X - _virtualBounds.X;
            int horizontal = _cursorVirtual.Y - _virtualBounds.Y;
            footprint = new PhysRect(0, horizontal - border, _virtualBounds.Width, border * 2 + 1)
                .Union(new PhysRect(vertical - border, 0, border * 2 + 1, _virtualBounds.Height));
        }

        if (_toolbar.IsVisible)
        {
            footprint = footprint.Union(_toolbar.OverallBounds.Inflate(4));
        }

        if (ShouldShowMagnifier())
        {
            EnsureMagnifierMeasured();
            footprint = footprint.Union(_magnifier.LastBounds);
        }

        return footprint;
    }

    /// <summary>把上次与本次的占用矩形并集失效（避免残影，又不整屏重绘）。</summary>
    private void InvalidateOverlay(PhysRect previousFootprint)
    {
        if (!IsHandleCreated || IsDisposed)
        {
            return;
        }

        PhysRect current = ComputeFootprint();
        _lastFootprint = current;

        PhysRect dirty = current.Union(previousFootprint).Clamp(new PhysRect(0, 0, _virtualBounds.Width, _virtualBounds.Height));
        if (dirty.IsEmpty)
        {
            Invalidate();
            return;
        }

        Invalidate(dirty.ToRectangle());
    }

    // ------------------------------------------------------------------ 像素采样

    private void EnsureMagnifierMeasured()
    {
        if (_cursorVirtual.X < _virtualBounds.X || _cursorVirtual.Y < _virtualBounds.Y
            || _cursorVirtual.X >= _virtualBounds.Right || _cursorVirtual.Y >= _virtualBounds.Bottom)
        {
            return;
        }

        Color pixel = SamplePixel(_cursorVirtual);
        _magnifier.Measure(_cursorVirtual, _metrics, FormatHex(pixel), FormatRgb(pixel), $"({_cursorVirtual.X}, {_cursorVirtual.Y})");
    }

    /// <summary>取抓屏帧上某点像素（虚拟桌面坐标）。越界返回黑。</summary>
    private Color SamplePixel(PhysPoint virtualPoint)
    {
        PhysPoint bitmapPoint = _frame.ToBitmapPoint(virtualPoint);
        Bitmap bitmap = _frame.Bitmap;

        if (bitmapPoint.X < 0 || bitmapPoint.Y < 0 || bitmapPoint.X >= bitmap.Width || bitmapPoint.Y >= bitmap.Height)
        {
            return Color.Black;
        }

        return bitmap.GetPixel(bitmapPoint.X, bitmapPoint.Y);
    }

    private static string FormatHex(Color color) => $"#{color.R:X2}{color.G:X2}{color.B:X2}";

    private static string FormatRgb(Color color) => $"RGB({color.R}, {color.G}, {color.B})";

    // ------------------------------------------------------------------ 自测支持

    /// <summary>
    /// 诊断模式：直接把状态摆到"已框选 + 指针在某处"的样子，
    /// 供 --selftest-overlay 离屏渲染取证（不产生真实窗口交互）。
    /// </summary>
    internal void ApplyDiagnosticState(PhysRect? selection, PhysPoint cursorVirtual, bool annotating)
    {
        DiagnosticMode = true;
        _cursorVirtual = cursorVirtual;
        _selection = selection;

        if (selection is not null)
        {
            _toolbar.Layout(selection, _virtualBounds, _dpi);
            if (annotating)
            {
                // 摆出「已选中矩形工具」的状态，让工具条渲染选中色
                IReadOnlyList<ToolbarItem> items = _toolbar.State.Items;
                for (int i = 0; i < items.Count; i++)
                {
                    if (items[i].Role == ToolbarItemRole.Tool)
                    {
                        _toolbar.State.ActiveIndex = i;
                        break;
                    }
                }
            }
        }

        _lastFootprint = ComputeFootprint();
    }
}
