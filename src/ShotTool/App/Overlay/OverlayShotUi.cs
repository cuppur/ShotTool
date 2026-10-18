// ============================================================================
//  App/Overlay/OverlayShotUi.cs —— IShotCaptureUi 实现（App 层与 Editor 层唯一调用面）
//
//  完整跑一次截图：
//    抓屏（ICaptureSource）→ 全屏 overlay（框选/放大镜/工具条）→ 完成或取消
//    → 台成选区位图 → 交给 ISelectionHandoff（默认实现留痕 + 走 IOutputSink）
//
//  两条路径的句柄/位图释放（任务要求"无泄漏"）：
//    取消：form.Dispose + frame.Dispose（using/finally）；
//    完成：先 ComposeSelectionBitmap() 生成**新**位图（所有权交给 handoff 的调用方，
//          本方法内 using 释放），再 form.Dispose + frame.Dispose。
//    任何异常路径都由 finally 兜住 —— 不允许"失败一次泄漏 33 MB"。
//
//  线程：RunAsync 必须在 UI 线程调用（托盘/热键都在 UI 线程）；await 之后
//        ConfigureAwait(true) 保证 ComposeSelectionBitmap / Dispose 仍在 UI 线程
//        （CapturedFrame.Dispose 的契约要求，见 Core/Contracts.cs 顶部线程模型）。
// ============================================================================

using System.Drawing;
using ShotTool.Core;

namespace ShotTool.App.Overlay;

/// <summary>采集层实现的截图 UI 门面。</summary>
public sealed class OverlayShotUi : IShotCaptureUi
{
    private readonly ShotToolContext _ctx;
    private readonly ISelectionHandoff _handoff;
    private readonly SynchronizationContext? _uiContext;

    private SelectionOverlayForm? _activeForm;

    public OverlayShotUi(ShotToolContext context)
        : this(context, null)
    {
    }

    /// <summary>可注入交付实现（自测用 LoggingSelectionHandoff；Editor 就位后换真实现）。</summary>
    internal OverlayShotUi(ShotToolContext context, ISelectionHandoff? handoff)
    {
        _ctx = context ?? throw new ArgumentNullException(nameof(context));
        _handoff = handoff ?? new SelectionHandoff(context);
        _uiContext = SynchronizationContext.Current;
    }

    /// <inheritdoc />
    public bool IsRunning { get; private set; }

    /// <summary>最近一次证据文件（自测/诊断用，可为 null）。</summary>
    public string? LastEvidencePath { get; private set; }

    public Task<ShotResult> RunAsync(ShotCompletion? forcedMode = null, CancellationToken cancellationToken = default)
    {
        if (IsRunning)
        {
            _ctx.Log.Info("已有截图会话在进行中，忽略本次触发。");
            return Task.FromResult(ShotResult.Ok());
        }

        // 防跨线程：overlay 是 WinForms 窗口，必须在创建它的那个线程上 Show()。
        // 热键回调天然在 UI 线程，但单实例唤醒/将来可能的 IPC 入口不一定 —— 这里兜一层。
        if (SynchronizationContext.Current != _uiContext && _uiContext is not null)
        {
            var completion = new TaskCompletionSource<ShotResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            _uiContext.Post(
                _ => _ = ForwardToUiThreadAsync(completion, forcedMode, cancellationToken),
                null);
            return completion.Task;
        }

        return Start(forcedMode, cancellationToken);
    }

    private async Task ForwardToUiThreadAsync(
        TaskCompletionSource<ShotResult> completion,
        ShotCompletion? forcedMode,
        CancellationToken cancellationToken)
    {
        try
        {
            completion.TrySetResult(await Start(forcedMode, cancellationToken).ConfigureAwait(true));
        }
        catch (Exception ex)
        {
            completion.TrySetResult(ShotResult.Fail(ex));
        }
    }

    private Task<ShotResult> Start(ShotCompletion? forcedMode, CancellationToken cancellationToken)
    {
        if (IsRunning)
        {
            _ctx.Log.Info("已有截图会话在进行中，忽略本次触发。");
            return Task.FromResult(ShotResult.Ok());
        }

        ICaptureSource? capture = _ctx.Capture;
        if (capture is null)
        {
            var error = new InvalidOperationException("ICaptureSource 未装配（采集层缺失）。");
            _ctx.Log.Error("无法开始截图。", error);
            return Task.FromResult(ShotResult.Fail(error));
        }

        CapturedFrame frame;
        try
        {
            frame = capture.CaptureVirtualDesktop(CaptureOptions.Default, cancellationToken);
        }
        catch (CaptureException ex)
        {
            _ctx.Log.Error($"抓屏失败（后端 {ex.Backend}，Win32={ex.Win32Error}）。", ex);
            return Task.FromResult(ShotResult.Fail(ex));
        }
        catch (OperationCanceledException ex)
        {
            _ctx.Log.Info("抓屏被取消。");
            return Task.FromResult(ShotResult.Fail(ex));
        }
        catch (Exception ex)
        {
            _ctx.Log.Error("抓屏抛出未预期异常。", ex);
            return Task.FromResult(ShotResult.Fail(ex));
        }

        IsRunning = true;
        return RunOverlayAsync(frame, forcedMode, cancellationToken);
    }

    private async Task<ShotResult> RunOverlayAsync(
        CapturedFrame frame,
        ShotCompletion? forcedMode,
        CancellationToken cancellationToken)
    {
        SelectionOverlayForm? form = null;
        try
        {
            form = new SelectionOverlayForm(_ctx, frame, forcedMode);

            // 交付实现注入 overlay：完成路径**只在一个地方**交付位图（session.CompleteAsync），
            // 避免两处都交付导致剪贴板写两次 / 证据 PNG 出两张。
            form.AttachHandoff(_handoff);
            _activeForm = form;

            OverlayOutcome outcome;
            using (CancellationTokenRegistration registration = cancellationToken.Register(
                       static state => ((SelectionOverlayForm)state!).RequestCancel(),
                       form))
            {
                outcome = await form.ShowAndWaitAsync().ConfigureAwait(true);
            }

            if (!outcome.Completed)
            {
                _ctx.Log.Info("截图会话已取消，未产生输出。");
                return ShotResult.Ok();
            }

            // 结果由 session.CompleteAsync 产生（含选区位图合成、标注合成、输出）
            ShotResult result = form.LastResult
                ?? ShotResult.Fail(new InvalidOperationException("会话已完成但没有产生结果对象。"));

            LastEvidencePath = result.FilePath;
            return result;
        }
        catch (Exception ex)
        {
            _ctx.Log.Error("截图管线抛出未处理异常。", ex);
            return ShotResult.Fail(ex);
        }
        finally
        {
            _activeForm = null;

            // overlay 自己 new 的位图/字体在这里释放
            form?.Dispose();

            // 抓屏帧必须在 UI 线程释放（Bitmap 被 UI 线程的 Graphics 持有）
            frame.Dispose();

            IsRunning = false;

            _ctx.Log.Info($"截图会话结束（IsRunning={IsRunning}）。");
        }
    }

    public void RequestCancel()
    {
        SelectionOverlayForm? form = _activeForm;
        if (form is null || !form.IsHandleCreated || form.IsDisposed)
        {
            return;
        }

        _ctx.Log.Info("收到取消请求（热键重按 / 托盘菜单）。");
        form.RequestCancel();
    }
}
