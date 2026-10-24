// ============================================================================
//  App/Overlay/SelectionOverlayForm.Contracts.cs —— overlay 的契约实现（partial）
//
//  ★ 契约 → 类 映射表（captain 要求，t7 reviewer 按此核对所有权）：
//
//    | 契约（Core/Contracts.cs） | 落在哪个类                                        |
//    |---------------------------|---------------------------------------------------|
//    | `ICaptureSource`          | `Capture/GdiCaptureSource.cs`（GDI BitBlt）        |
//    | `IGlobalHotkeyService`    | `App/GlobalHotkeyService.cs`                       |
//    | `IAppLog`                 | `App/FileAppLog.cs`                                |
//    | `IOverlayHost`            | **本文件 → SelectionOverlayForm**（overlay 窗口本身）|
//    | `IShotSession`            | **本文件 → SelectionOverlayForm**（会话状态机）      |
//    | `IShotCaptureUi`          | `App/Overlay/OverlayShotUi.cs`（启动器/防重入）      |
//
//  为什么 overlay 与会话是同一个对象（而不是两个类）：
//    会话状态（选区 / 阶段 / 标注 / 撤销栈）与 overlay 的失效重绘是**同一份状态**。
//    拆成两个类只有一个结果：要么会话里存一份副本，要么每次交互都双向同步 ——
//    两者都会引入"两份状态不一致"的 bug（选区在会话里改了、overlay 还画旧的）。
//    契约本身没有要求不同对象（IShotSession 只描述行为），因此单对象是最小实现。
//
//  职责边界（不越界）：
//    * 标注的**绘制**调 dev-editor 的 `AnnotationRasterizer`（纯函数），
//      **模型与算法**也归 Editor（Annotation / AnnotationHistory / MosaicProcessor）；
//      本文件只持有一个 `AnnotationHistory` 实例当"会话里的标注栈"。
//    * 文字输入 / 扩展面板宿主归 Editor（TextInputForm / ToolbarHost），本文件不实现。
//    * 输出归 dev-integration（IOutputSink），本文件只经 ISelectionHandoff 转交。
// ============================================================================

using System.Drawing;
using System.Drawing.Drawing2D;
using ShotTool.Core;
using ShotTool.Editor.Algorithms;
using ShotTool.Editor.Model;

namespace ShotTool.App.Overlay;

internal sealed partial class SelectionOverlayForm
{
    private readonly AnnotationHistory _history = new();
    private ISelectionHandoff? _handoff;
    private ShotPhase _phase = ShotPhase.Selecting;
    private string? _toastText;
    private System.Windows.Forms.Timer? _toastTimer;
    private ShotResult? _lastResult;

    /// <summary>注入交付实现（由 OverlayShotUi 在构造后立即调用）。</summary>
    internal void AttachHandoff(ISelectionHandoff handoff) => _handoff = handoff;

    /// <summary>最近一次完成结果（OverlayShotUi 用它构造返回值）。</summary>
    internal ShotResult? LastResult => _lastResult;

    /// <summary>会话里的标注栈（Editor 的模型；本层只读 + 提供增删入口）。</summary>
    internal AnnotationHistory History => _history;

    // ------------------------------------------------------------------ IShotSession

    /// <inheritdoc />
    public ShotPhase Phase => _phase;

    /// <inheritdoc />
    public CapturedFrame Frame => _frame;

    /// <inheritdoc />
    public IReadOnlyList<IAnnotation> Annotations => _history.Items;

    /// <inheritdoc />
    public IUndoStack Undo => _history;

    /// <inheritdoc />
    public event EventHandler<ShotSessionChangedEventArgs>? SessionChanged;

    /// <inheritdoc />
    public void SetSelection(PhysRect? selection)
    {
        bool wasCandidate = _windowCandidate;
        _windowCandidate = false;
        _pendingWindowPick = false;
        PhysRect? clamped = selection is null
            ? null
            : selection.Value.Clamp(_virtualBounds) is { IsEmpty: false } hit ? hit : null;

        if (Nullable.Equals(clamped, _selection) && !wasCandidate)
        {
            return;
        }

        _selection = clamped;
        _toolbar.Layout(_selection, _virtualBounds, _dpi);
        SetPhase(_selection is null ? ShotPhase.Selecting : ShotPhase.Annotating);
        RaiseSessionChanged(ShotChangeKind.Selection);
        InvalidateOverlay(_lastFootprint);
    }

    /// <inheritdoc />
    public Bitmap? ComposeSelectionBitmap(bool includeAnnotations)
    {
        if (_selection is null)
        {
            return null;
        }

        Bitmap? cropped = ShotTool.Capture.SelectionCropper.Crop(_frame, _selection.Value);
        if (cropped is null)
        {
            return null;
        }

        if (!includeAnnotations || _history.Items.Count == 0)
        {
            return cropped;
        }

        // 标注坐标 = 选区局部坐标（dev-editor 的约定），因此可以直接合成到裁剪图上
        Bitmap composed = AnnotationRasterizer.Compose(
            cropped,
            _history.Items,
            RenderOptions.Create(_metrics.DpiScale));
        cropped.Dispose();
        return composed;
    }

    /// <inheritdoc />
    public async Task<ShotResult> CompleteAsync(ShotCompletion mode, CancellationToken cancellationToken = default)
    {
        if (_phase == ShotPhase.Committing || _closing)
            return ShotResult.Fail(new InvalidOperationException("截图正在输出或已经结束。"));
        if (_selection is null || _selection.Value.IsEmpty)
        {
            var error = new InvalidOperationException("尚未框选任何区域。");
            _ctx.Log.Warn("[session] CompleteAsync 被调用但没有选区，忽略。");
            return ShotResult.Fail(error);
        }

        PhysRect selection = _selection.Value;
        SetPhase(ShotPhase.Committing);

        ShotResult result;
        try
        {
            // 含标注的成品图（用户看到什么就交什么）
            using Bitmap? composed = ComposeSelectionBitmap(includeAnnotations: true);
            if (composed is null)
            {
                result = ShotResult.Fail(new InvalidOperationException($"选区 {selection} 无法合成位图。"));
                _ctx.Log.Error("[session] 合成选区位图失败。", result.Error);
            }
            else if (_handoff is null)
            {
                result = ShotResult.Fail(new InvalidOperationException("未注入 ISelectionHandoff。"));
                _ctx.Log.Error("[session] 没有交付实现，位图无法交下游。", result.Error);
            }
            else
            {
                result = await _handoff
                    .DeliverAsync(composed, selection, mode, cancellationToken)
                    .ConfigureAwait(true);
            }
        }
        catch (OperationCanceledException ex)
        {
            result = ShotResult.Fail(ex);
            _ctx.Log.Info("[session] 交付被取消。");
        }
        catch (Exception ex)
        {
            result = ShotResult.Fail(ex);
            _ctx.Log.Error("[session] 交付抛出异常。", ex);
        }

        _lastResult = result;
        SetPhase(result.Success ? ShotPhase.Done : ShotPhase.Annotating);
        if (result.Success)
            FinishSession(OverlayOutcome.Done(mode, selection));
        else
            ShowToast("输出失败，选区已保留，请重试：" + result.Error?.Message);
        return result;
    }

    /// <summary>阶段切换（同时通知消费者重绘/刷新工具条可用态）。</summary>
    private void SetPhase(ShotPhase phase)
    {
        if (_phase == phase)
        {
            return;
        }

        _phase = phase;
        RaiseSessionChanged(ShotChangeKind.Phase);
    }

    private void RaiseSessionChanged(ShotChangeKind kind, PhysRect dirtyRect = default)
    {
        EventHandler<ShotSessionChangedEventArgs>? handler = SessionChanged;
        if (handler is null)
        {
            return;
        }

        try
        {
            handler(this, new ShotSessionChangedEventArgs(kind, dirtyRect));
        }
        catch (Exception ex)
        {
            // 订阅方（未来 Editor / dev-ai）异常不能打断截图
            _ctx.Log.Error("[session] SessionChanged 订阅者抛异常。", ex);
        }
    }

    // ------------------------------------------------------------------ IOverlayHost

    // 注意：Control.Handle 已是 public IntPtr Handle，天然满足 IOverlayHost.Handle（无需重复实现）

    /// <inheritdoc />
    PhysRect IOverlayHost.VirtualBounds => _virtualBounds;

    /// <inheritdoc />
    uint IOverlayHost.Dpi => _dpi;

    /// <inheritdoc />
    void IOverlayHost.Invalidate(PhysRect rect)
        => InvalidateOverlay(rect);

    /// <inheritdoc />
    void IOverlayHost.InvalidateAll() => InvalidateOverlay(_lastFootprint);

    /// <inheritdoc />
    void IOverlayHost.Refocus()
    {
        if (!IsHandleCreated || IsDisposed)
        {
            return;
        }

        Interop.NativeMethods.SetForegroundWindow(Handle);
        Activate();
        Interop.NativeMethods.SetFocus(Handle);
    }

    /// <inheritdoc />
    void IOverlayHost.ShowToast(string text) => ShowToast(text);

    /// <summary>轻提示（约 2 秒自动消失，画在虚拟桌面底部中央）。</summary>
    internal void ShowToast(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        _toastText = text;
        _ctx.Log.Info($"[toast] {text}");

        _toastTimer ??= CreateToastTimer();
        _toastTimer.Stop();
        _toastTimer.Start();

        InvalidateOverlay(_lastFootprint);
    }

    private System.Windows.Forms.Timer CreateToastTimer()
    {
        var timer = new System.Windows.Forms.Timer { Interval = 2000 };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            _toastText = null;
            InvalidateOverlay(_lastFootprint);
        };
        return timer;
    }

    /// <summary>暂停态提示：把 toast 清空并停表（诊断渲染前调用，避免 2 秒后被定时器改状态）。</summary>
    internal void FreezeToastForDiagnostics(string? text)
    {
        _toastTimer?.Stop();
        _toastText = text;
    }

    // ------------------------------------------------------------------ 绘制：标注层 + toast

    /// <summary>
    /// 绘制标注层（④⑤）与 toast（⑨）。标注用 dev-editor 的纯函数栅格化器，
    /// 坐标系是「选区局部」，因此这里做 clip + translate。
    /// <paramref name="sinceHandles"/> = true 时只画标注（在 HUD/工具条之前调用，
    /// 保证标注不会盖住它们）；= false 时只画 toast（在整场景末尾调用）。
    /// </summary>
    private void PaintAnnotationsAndToast(Graphics graphics, PhysRect? selection, bool sinceHandles)
    {
        if (sinceHandles)
        {
            if (selection is { } sel && !sel.IsEmpty) PaintEditorAnnotations(graphics, sel);
            return;
        }

        if (_toastText is null)
        {
            return;
        }

        using Font font = OverlayFonts.Create(_metrics.HudFontPx);
        const System.Windows.Forms.TextFormatFlags flags =
            System.Windows.Forms.TextFormatFlags.NoPadding
            | System.Windows.Forms.TextFormatFlags.SingleLine
            | System.Windows.Forms.TextFormatFlags.HorizontalCenter;

        Size textSize = System.Windows.Forms.TextRenderer.MeasureText(
            graphics,
            _toastText,
            font,
            new Size(int.MaxValue, int.MaxValue),
            flags);

        int padX = _metrics.HudPaddingPx * 3;
        int padY = _metrics.HudPaddingPx * 2;
        int width = textSize.Width + (padX * 2);
        int height = textSize.Height + (padY * 2);

        var box = new Rectangle(
            (_virtualBounds.Width - width) / 2,
            _virtualBounds.Height - height - (_metrics.HudPaddingPx * 8),
            width,
            height);

        using (var brush = new SolidBrush(OverlayColors.HudBackground))
        {
            graphics.FillRectangle(brush, box);
        }

        System.Windows.Forms.TextRenderer.DrawText(
            graphics,
            _toastText,
            font,
            new Rectangle(box.X, box.Y + padY, box.Width, box.Height),
            OverlayColors.HudText,
            flags);
    }
}
