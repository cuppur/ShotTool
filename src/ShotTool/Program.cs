using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using ShotTool.App;
using ShotTool.Core;
using ShotTool.Interop;

namespace ShotTool;

// ============================================================================
//  Program.cs —— 进程引导（dev-capture 拥有）
//
//  启动顺序（顺序本身是契约，不要调整）:
//    1) DpiBootstrap.EnsurePerMonitorV2()   —— 必须最先，任何窗口/Graphics 之前
//    2) Application.SetHighDpiMode(PMv2)    —— 幂等兜底
//    3) EnableVisualStyles / SetCompatibleTextRenderingDefault
//    4) 日志（落盘，见 App/FileAppLog.cs）
//    5) 自测分支（--selftest / --selftest-overlay，不建托盘、不抢单实例）
//    6) 单实例互斥 + 唤醒通道
//    7) 组合根装配（CompositionRoot.TryCompose）
//    8) 托盘（App/TrayApplication）+ Application.Run
//
//  本文件只做「引导 + 装配」，具体能力都在 App/ 与 Capture/ 里：
//    App/GlobalHotkeyService.cs  全局热键
//    App/TrayApplication.cs      托盘常驻（图标/菜单/双击/自启/设置/状态）
//    App/Overlay/*               overlay 窗口 + 会话状态机 + 交付出口
//    Capture/GdiCaptureSource.cs GDI BitBlt 抓屏
//    Interop/*                   Win32 声明与屏幕几何读数
// ============================================================================

internal static class Program
{
    internal const string AppDisplayName = "ShotTool";

    /// <summary>单实例互斥体名。Local\ = 当前登录会话（不同用户各自一个实例，符合预期）。</summary>
    private const string SingleInstanceMutexName = @"Local\ShotTool.SingleInstance.v1";

    /// <summary>第二个实例唤醒第一个实例的命名事件。</summary>
    private const string SingleInstanceWakeEventName = @"Local\ShotTool.SingleInstance.Wake.v1";

    [STAThread]
    private static int Main(string[] args)
    {
        // ---- 1) DPI：必须最先 ----
        bool dpiOk = DpiBootstrap.EnsurePerMonitorV2();

        // ---- 2) WinForms 侧兜底（幂等；进程 DPI 已设置时返回 false 不算错误）----
        _ = Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);

        // ---- 3) 视觉样式 ----
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        // ---- 4) 日志（无控制台的 WinExe 必须落盘，否则事后无法诊断）----
        var log = new FileAppLog();
        log.Info($"启动 {AppDisplayName}；虚拟桌面 = {MonitorService.DescribeVirtualDesktop()}");

        if (!dpiOk)
        {
            // 注意：manifest 已声明 PerMonitorV2 时该调用**必然**返回失败（ERROR_ACCESS_DENIED），
            // 这是正常的双保险行为，不代表 DPI 没生效。真正的自证看虚拟桌面尺寸：
            // 物理像素（本机 3840x2160）即生效；若显示逻辑像素（1707x960）才是真失败。
            log.Info(
                "SetProcessDpiAwarenessContext(PER_MONITOR_AWARE_V2) 返回 false —— " +
                "manifest 已设置时属正常；DPI 是否生效以虚拟桌面物理尺寸为准。");
        }

        // ---- 5) 自测分支（离屏，不需要托盘；必须在单实例之前）----
        int? selfTestExitCode = SelfTest.TryRun(args, log);
        if (selfTestExitCode is not null)
        {
            log.Info($"自测结束，退出码 {selfTestExitCode.Value}。");
            return selfTestExitCode.Value;
        }

        // ---- 6) 单实例 ----
        using SingleInstanceGuard guard = SingleInstanceGuard.Acquire(
            SingleInstanceMutexName,
            SingleInstanceWakeEventName,
            log);

        if (!guard.IsFirstInstance)
        {
            // ★ 绝不静默退出：用户最常见的操作是"任务管理器结束进程"，若残留实例仍持有
            //   互斥体，新进程会走到这里。什么都不提示 = 用户以为程序坏了（t11 实测到的坑）。
            guard.SignalFirstInstance();

            // 唤醒后首实例立即显示截图；弹模态提示会抢走它的焦点并使截图取消。
            log.Info("已通知现有实例开始截图，第二个实例退出。");
            return 0;
        }

        // ---- 7) 组合根装配 ----
        var context = new ShotToolContext(log);
        CompositionRoot.TryCompose(context);
        ApplyCommandLineOverrides(args, context, log);

        // ---- 8) 托盘常驻 ----
        using var app = new TrayApplication(context, guard);
        app.Start();

        // 脚本化触发（自测/冒烟）：--capture-after <毫秒>
        if (TryGetIntOption(args, "--capture-after", out int captureDelayMs))
        {
            app.ScheduleCapture(captureDelayMs);
        }

        // 兜住一切能让进程"静默消失"的异常路径：任何未处理异常都必须先落日志。
        // （t11 观察到过一次"启动约 20 秒后进程自己没了"且日志无痕 —— 没有钩子就无法归因。）
        Application.ThreadException += (_, e) => log.Error("UI 线程未处理异常（已拦截，程序继续运行）。", e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            log.Error($"进程级未处理异常（IsTerminating={e.IsTerminating}）。", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            log.Error("未观察的 Task 异常（已 SetObserved，程序继续运行）。", e.Exception);
            e.SetObserved();
        };
        AppDomain.CurrentDomain.ProcessExit += (_, _) => log.Info("进程退出（ProcessExit 事件）。");

        try
        {
            Application.Run(app);
        }
        catch (Exception ex)
        {
            // Application.Run 本身抛出（窗口过程之外的致命错误）：记下来再退，别让进程悄悄消失
            log.Error("消息循环异常退出。", ex);
            return 1;
        }

        log.Info("退出。");
        return 0;
    }

    /// <summary>读取 <c>--name &lt;int&gt;</c> 形式的整数参数。</summary>
    private static bool TryGetIntOption(string[] args, string name, out int value)
    {
        value = 0;
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase)
                && int.TryParse(args[i + 1], out value))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 命令行覆盖（只影响本次运行，不落盘）：
    ///   --hotkey Ctrl+Alt+A    临时换截图热键（Alt+A 被微信占用时用来验证热键通路）
    ///   --show-missing         启动后弹窗显示装配缺项（诊断用）
    /// 自测开关（--selftest / --selftest-overlay）由 App/SelfTest.cs 处理。
    /// </summary>
    private static void ApplyCommandLineOverrides(string[] args, ShotToolContext context, IAppLog log)
    {
        for (int i = 0; i < args.Length; i++)
        {
            if (!string.Equals(args[i], "--hotkey", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (i + 1 >= args.Length || !HotkeyGesture.TryParse(args[i + 1], out HotkeyGesture gesture))
            {
                log.Warn("命令行 --hotkey 参数无效（示例：--hotkey Ctrl+Alt+A）。");
                return;
            }

            if (context.Settings is not null)
            {
                context.Settings.App.CaptureHotkey = gesture;
            }

            log.Info($"命令行覆盖截图热键为 {gesture}（仅本次运行，不写入配置）。");
            return;
        }
    }
}

/// <summary>
/// 进程级 DPI 引导。PMv2 是本项目所有坐标正确性的前提（本机 DPI 225%）。
/// </summary>
internal static class DpiBootstrap
{
    /// <summary>DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 == (HANDLE)-4。</summary>
    private static readonly IntPtr PerMonitorAwareV2 = new(-4);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetProcessDpiAwarenessContext(IntPtr value);

    /// <summary>
    /// 尝试把进程设为 PerMonitorV2。已由 manifest 设置时返回 false（ERROR_ACCESS_DENIED），
    /// 这属于正常情况 —— 调用方只需要在「两者都失败」时告警。
    /// </summary>
    internal static bool EnsurePerMonitorV2()
    {
        try
        {
            return SetProcessDpiAwarenessContext(PerMonitorAwareV2);
        }
        catch (EntryPointNotFoundException)
        {
            // Win7/8 没有该 API；本机不涉及，但保持不崩。
            return false;
        }
    }
}

/// <summary>
/// 单实例守卫。第一个实例持有互斥体并监听唤醒事件；
/// 后续实例只发送唤醒信号后退出。
/// </summary>
internal sealed class SingleInstanceGuard : IDisposable
{
    private readonly Mutex? _mutex;
    private readonly EventWaitHandle? _wakeEvent;
    private readonly EventWaitHandle? _stopEvent;
    private readonly IAppLog _log;
    private readonly SynchronizationContext? _uiContext;
    private Thread? _listener;
    private volatile bool _disposed;

    private SingleInstanceGuard(
        Mutex? mutex,
        EventWaitHandle? wakeEvent,
        EventWaitHandle? stopEvent,
        bool isFirstInstance,
        IAppLog log)
    {
        _mutex = mutex;
        _wakeEvent = wakeEvent;
        _stopEvent = stopEvent;
        IsFirstInstance = isFirstInstance;
        _log = log;
        _uiContext = SynchronizationContext.Current;
    }

    public bool IsFirstInstance { get; }

    /// <summary>另一个实例请求接管（在 UI 线程触发）。</summary>
    public event EventHandler? WakeRequested;

    public static SingleInstanceGuard Acquire(string mutexName, string eventName, IAppLog log)
    {
        // ★ 关键：不用 `new Mutex(initiallyOwned: true, ...)`。
        //   那种写法在"上一实例被强杀后立即启动"时会踩 abandoned mutex：
        //   内核对象可能尚未销毁（本进程打开的瞬间它还在），ctor 返回 createdNew=false
        //   且**不会**抛 AbandonedMutexException，于是本进程误判为"已有实例"→ 发唤醒信号给
        //   一个正在死掉的实例 → 自己立刻退出（用户看到"双击没反应/一闪而过"）。
        //
        //   正确做法：先不持有创建，再显式 WaitOne(0) 抢所有权；WaitOne 会抛
        //   AbandonedMutexException —— 那恰好说明"上一实例异常退出"，此时我们**就是**首实例。
        Mutex mutex;
        bool createdNew;
        try
        {
            mutex = new Mutex(initiallyOwned: false, mutexName, out createdNew);
        }
        catch (UnauthorizedAccessException)
        {
            // 互斥体属于其它会话/权限：退化为「非首实例」，交给唤醒通道处理。
            log.Warn("创建单实例互斥体失败（权限），按非首实例处理。");
            return new SingleInstanceGuard(null, null, null, false, log);
        }

        bool isFirstInstance = TryTakeOwnership(mutex, log);

        if (!isFirstInstance)
        {
            // 已存在实例：打开唤醒事件（不监听），并立刻关闭本地句柄
            EventWaitHandle? existing = null;
            try
            {
                existing = new EventWaitHandle(false, EventResetMode.AutoReset, eventName);
            }
            catch (Exception ex)
            {
                log.Warn($"打开唤醒事件失败：{ex.Message}");
            }

            mutex.Dispose();
            return new SingleInstanceGuard(null, existing, null, false, log);
        }

        EventWaitHandle wake;
        EventWaitHandle stop;
        try
        {
            wake = new EventWaitHandle(false, EventResetMode.AutoReset, eventName);
            stop = new EventWaitHandle(false, EventResetMode.ManualReset);
        }
        catch (Exception ex)
        {
            log.Error("创建唤醒/停止事件失败，单实例唤醒不可用（热键与托盘不受影响）。", ex);
            return new SingleInstanceGuard(mutex, null, null, true, log);
        }

        var guard = new SingleInstanceGuard(mutex, wake, stop, true, log);
        guard.StartListener();
        return guard;
    }

    /// <summary>
    /// 抢单实例所有权（带重试）。
    ///
    /// 三个必须区分的状态：
    ///   1) createdNew == true             → 全新对象，WaitOne 立即成功 → 首实例。
    ///   2) 上一实例正在运行并持有互斥体      → WaitOne 在整段重试窗口内都失败 → 非首实例（唤醒它并退出）。
    ///   3) 上一实例被强杀（kill -9 / 任务管理器结束任务）→
    ///        a. 已完全退出：内核对象随之销毁 → 本次是 1) 首实例；
    ///        b. 尚未被完全回收：WaitOne 抛 <see cref="AbandonedMutexException"/>，或短暂返回 false。
    ///           —— 这正对应 t11 报的"强杀上一实例后立即启动"场景，若不重试，
    ///           新进程会误判为"已有实例"给一个已死的进程发唤醒信号后**自己退出**，
    ///           用户看到的就是"双击没反应 / 一闪而过"。
    ///
    /// 重试窗口取 1000 ms：真·第二实例多等 1 秒无感；被强杀场景大多在几十毫秒内可接管。
    /// </summary>
    private static bool TryTakeOwnership(Mutex mutex, IAppLog log)
    {
        const int RetryWindowMs = 1000;
        const int StepMs = 50;

        for (int waited = 0; ; waited += StepMs)
        {
            try
            {
                if (mutex.WaitOne(0))
                {
                    if (waited > 0)
                    {
                        log.Info($"等待 {waited} ms 后取得单实例所有权（上一实例正在退出）。");
                    }

                    return true;
                }
            }
            catch (AbandonedMutexException)
            {
                log.Warn("检测到上一实例异常退出（abandoned mutex），本进程接管单实例所有权。");
                return true;
            }

            if (waited >= RetryWindowMs)
            {
                return false;
            }

            Thread.Sleep(StepMs);
        }
    }

    /// <summary>非首实例调用：唤醒并让首实例开始截图。</summary>
    public void SignalFirstInstance()
    {
        try
        {
            _wakeEvent?.Set();
        }
        catch (Exception ex)
        {
            _log.Warn($"发送唤醒信号失败：{ex.Message}");
        }
    }

    private void StartListener()
    {
        if (_wakeEvent is null || _stopEvent is null)
        {
            return;
        }

        _listener = new Thread(ListenLoop)
        {
            IsBackground = true,
            Name = "ShotTool.SingleInstanceListener",
        };
        _listener.Start();
    }

    private void ListenLoop()
    {
        WaitHandle[] handles = { _wakeEvent!, _stopEvent! };
        while (!_disposed)
        {
            int index;
            try
            {
                index = WaitHandle.WaitAny(handles);
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch (Exception ex)
            {
                // 监听线程上的未处理异常会**直接杀掉整个进程**（后台线程异常不走 UI 异常钩子），
                // 这是"托盘程序莫名其妙消失"的典型成因。这里必须兜住并记录。
                _log.Error("单实例监听线程异常，停止监听（进程继续运行）。", ex);
                return;
            }

            if (index != 0 || _disposed)
            {
                return;
            }

            try
            {
                if (_uiContext is not null)
                {
                    _uiContext.Post(_ => SafeRaiseWake(), null);
                }
                else
                {
                    SafeRaiseWake();
                }
            }
            catch (Exception ex)
            {
                // 订阅方/Post 失败同样不允许弄死进程
                _log.Error("派发单实例唤醒信号失败。", ex);
            }
        }
    }

    private void SafeRaiseWake()
    {
        try
        {
            WakeRequested?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            _log.Error("单实例唤醒处理器抛异常（已被吞掉，进程继续运行）。", ex);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _stopEvent?.Set();

        try
        {
            _listener?.Join(TimeSpan.FromMilliseconds(200));
        }
        catch (Exception ex)
        {
            _log.Warn($"等待监听线程退出失败：{ex.Message}");
        }

        _wakeEvent?.Dispose();
        _stopEvent?.Dispose();

        if (IsFirstInstance)
        {
            try
            {
                _mutex?.ReleaseMutex();
            }
            catch (ApplicationException)
            {
                // 未持有（例如异常路径）：忽略
            }
            catch (ObjectDisposedException)
            {
                // 已释放：忽略
            }
        }

        _mutex?.Dispose();
    }
}

/// <summary>
/// 组合根装配。只做 <c>new + 赋值</c>，不写业务逻辑。
///
/// 顺序：Settings 最先（其它层可能要读配置）→ Output（依赖 Settings）→
///       Capture / Hotkeys / CaptureUi（互不依赖）。
///
/// 用 <c>??=</c> 语义：任何一层若已自带实现（例如未来 Editor 提供 IShotCaptureUi），
/// 不会被这里覆盖 —— 这样四层可以按任意顺序接入。
/// </summary>
internal static class CompositionRoot
{
    internal static void TryCompose(ShotToolContext context)
    {
        // ① 设置仓库（dev-integration）：%LOCALAPPDATA%\ShotTool\settings.json + 变更监听
        ISettingsStore settings = context.Settings
            ??= new Settings.JsonSettingsStore(log: context.Log);

        // ② 输出通道（dev-integration）：CF_DIB + CF_DIBV5 + PNG 剪贴板 / PNG 保存
        context.Output ??= new Output.ImageOutputSink(settings, context.Log);

        // ③ 采集后端（dev-capture）：GDI BitBlt + CAPTUREBLT，逐屏拼接
        context.Capture ??= new Capture.GdiCaptureSource(context.Log);

        // ④ 全局热键（dev-capture）：RegisterHotKey + 消息专用窗口
        context.Hotkeys ??= new GlobalHotkeyService(context.Log);

        // ⑤ 截图 UI（dev-capture）：抓屏 → overlay 会话 → 交付下游
        context.CaptureUi ??= new App.Overlay.OverlayShotUi(context);

        context.ToolbarExtensions ??= new Editor.Toolbar.ToolbarRegistry();
        context.Ai ??= Ai.AiServiceRegistry.Initialize(context, context.ToolbarExtensions, settings);

        if (!context.IsComplete)
        {
            context.Log.Warn("组合根未装配完整，缺失：" + string.Join(", ", context.DescribeMissing()));
        }
        else
        {
            context.Log.Info("组合根装配完成：" + DescribeComposition(context));
        }
    }

    private static string DescribeComposition(ShotToolContext context)
        => string.Join(
            " | ",
            $"Settings={context.Settings?.GetType().Name}",
            $"Output={context.Output?.GetType().Name}",
            $"Capture={context.Capture?.GetType().Name}",
            $"Hotkeys={context.Hotkeys?.GetType().Name}",
            $"CaptureUi={context.CaptureUi?.GetType().Name}",
            $"ToolbarExtensions={(context.ToolbarExtensions is null ? "(未装配)" : "已装配")}",
            $"Painters={(context.Painters is null ? "(未装配)" : "已装配")}");
}
