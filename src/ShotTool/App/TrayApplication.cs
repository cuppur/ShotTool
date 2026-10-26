// ============================================================================
//  App/TrayApplication.cs —— 托盘常驻（通知区图标 + 菜单 + 热键 + 单实例唤醒）
//
//  契约归属：本类**不**实现任何 Core 契约，只做「装配后的用户入口」：
//    * IGlobalHotkeyService（App 层）→ 热键触发截图；
//    * IShotCaptureUi（App 层 OverlayShotUi）→ 真正跑一次截图；
//    * ISettingsStore / IOutputSink（dev-integration）→ 只经由 ShotToolContext 取用。
//
//  交互口径（与微信一致的地方都写清楚了）：
//    * Alt+A / 托盘「截图」/ 双击图标 → 开始截图；
//    * **截图进行中再按热键 → 取消**（连按两次 = 进/出，符合直觉，也避免叠加第二层 overlay）；
//    * 截图进行中点托盘菜单「截图」→ 忽略（菜单是明确意图，不当作撤销）；
//    * 热键注册失败 → 托盘气泡提示 + 菜单出现「改键…」入口（**不允许静默失败**）。
//
//  诊断自证：菜单「状态…」里的虚拟桌面尺寸必须是**物理像素**（本机 3840x2160）。
//  若显示 1707x960 说明 PerMonitorV2 没生效（ADR-005 的唯一自证点）。
// ============================================================================

using System.Diagnostics;
using System.Drawing;
using ShotTool.Core;
using ShotTool.Interop;
using ShotTool.Settings;

namespace ShotTool.App;

/// <summary>托盘常驻上下文（internal：只由 Program 装配，不进产品契约面）。</summary>
internal sealed class TrayApplication : ApplicationContext
{
    private const string HotkeyName = "capture";

    private readonly ShotToolContext _ctx;
    private readonly SingleInstanceGuard _guard;
    private readonly NotifyIcon _tray;
    private readonly Icon _icon;
    private readonly ContextMenuStrip _menu;
    private readonly ToolStripMenuItem _captureItem;
    private readonly ToolStripMenuItem _cancelItem;
    private readonly ToolStripMenuItem _startupItem;
    private readonly ToolStripMenuItem _changeHotkeyItem;

    private readonly SynchronizationContext _uiContext;
    private bool _hotkeyRegistered;
    private HotkeyGesture _activeHotkey;
    private bool _disposed;

    public TrayApplication(ShotToolContext context, SingleInstanceGuard guard)
    {
        _ctx = context ?? throw new ArgumentNullException(nameof(context));
        _guard = guard ?? throw new ArgumentNullException(nameof(guard));

        _icon = TrayIconFactory.Create();

        _captureItem = new ToolStripMenuItem("截图(&S)", null, (_, _) => TriggerCapture(fromHotkey: false));
        _cancelItem = new ToolStripMenuItem("取消截图(&C)", null, (_, _) => CancelCapture()) { Enabled = false };
        _changeHotkeyItem = new ToolStripMenuItem("改键…", null, (_, _) => ShowSettings()) { Visible = false };
        _startupItem = new ToolStripMenuItem("开机自启(&A)", null, (_, _) => ToggleStartup())
        {
            CheckOnClick = false,
        };

        _menu = new ContextMenuStrip();
        _menu.Items.Add(_captureItem);
        _menu.Items.Add(_cancelItem);
        _menu.Items.Add(_changeHotkeyItem);
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(_startupItem);
        _menu.Items.Add(new ToolStripMenuItem("设置…", null, (_, _) => ShowSettings()));
        _menu.Items.Add(new ToolStripMenuItem("状态…", null, (_, _) => ShowDiagnostics()));
        _menu.Items.Add(new ToolStripMenuItem("打开日志目录", null, (_, _) => OpenLogDirectory()));
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(new ToolStripMenuItem("退出(&X)", null, (_, _) => ExitApplication()));

        _tray = new NotifyIcon
        {
            Icon = _icon,
            Text = BuildTooltip(),
            Visible = true,
            ContextMenuStrip = _menu,
        };
        _tray.DoubleClick += (_, _) => TriggerCapture(fromHotkey: false);
        _tray.BalloonTipClicked += (_, _) => TriggerCapture(fromHotkey: false);

        if (_ctx.Hotkeys is not null)
        {
            _ctx.Hotkeys.RegistrationFailed += OnHotkeyRegistrationFailed;
        }

        // 唤醒信号来自 SingleInstanceGuard 的监听线程：必须 marshal 回 UI 线程，
        // 否则 Show() 会在非 UI 线程创建窗口（WinForms 会抛 InvalidOperationException，
        // 表现为"托盘点了没反应、日志里有抓屏但没有 overlay"）。
        _uiContext = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
        _guard.WakeRequested += (_, _) => _uiContext.Post(_ => TriggerCapture(fromHotkey: false), null);

        RefreshStartupState();
    }

    /// <summary>托盘就绪后调用：注册热键（失败只提示，不阻断启动）。</summary>
    public void Start()
    {
        // Upgrade a user-enabled registration only on an ordinary launch. Never resurrect
        // a task disabled in Windows, and never rewrite a Run entry during its logon launch.
        if (!Environment.GetCommandLineArgs().Contains("--startup") && _ctx.Settings?.App.StartupWithWindows == true
            && StartupRegistration.ReadStatus().Enabled == true)
        {
            try { StartupRegistration.Apply(true); }
            catch (Exception ex) { _ctx.Log.Warn("更新自启程序失败：" + ex.Message); }
        }
        _ctx.Log.Info("登录自启状态：" + StartupRegistration.ReadStatus().Description);
        RegisterHotkey();

        if (_ctx.Capture is Capture.GdiCaptureSource gdi)
        {
            // 预热屏幕 DC，让第一次 Alt+A 少一次 GDI 初始化
            gdi.Warm();
        }

        _ctx.Log.Info(
            $"托盘已就绪：虚拟桌面={MonitorService.DescribeVirtualDesktop()}，" +
            $"采集后端={_ctx.Capture?.Backend.ToString() ?? "(未装配)"}，" +
            $"输出={(_ctx.Output is null ? "未装配" : _ctx.Output.GetType().Name)}，" +
            $"截图UI={(_ctx.CaptureUi is null ? "未装配" : _ctx.CaptureUi.GetType().Name)}。");
    }

    /// <summary>
    /// 延迟触发一次截图（脚本化自测/冒烟用，对应 Program 的 <c>--capture-after &lt;ms&gt;</c>）。
    /// 走的是和热键/托盘完全相同的入口（<see cref="TriggerCapture"/> → IShotCaptureUi.RunAsync）。
    /// </summary>
    internal void ScheduleCapture(int delayMs)
    {
        var timer = new System.Windows.Forms.Timer { Interval = Math.Max(100, delayMs) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            timer.Dispose();
            _ctx.Log.Info($"脚本化触发截图（--capture-after {delayMs} ms）。");
            TriggerCapture(fromHotkey: false);
        };
        timer.Start();
    }

    // ------------------------------------------------------------------ 热键

    private void RegisterHotkey()
    {
        IGlobalHotkeyService? hotkeys = _ctx.Hotkeys;
        if (hotkeys is null)
        {
            _ctx.Log.Warn("IGlobalHotkeyService 未装配，热键不可用（托盘菜单仍可截图）。");
            return;
        }

        HotkeyGesture configured = _ctx.Settings?.App.CaptureHotkey ?? HotkeyGesture.AltA;

        // 降级链（captain 裁决）：配置键 → 若失败且配置是 Alt+A（原味默认），自动再试 Ctrl+Alt+A。
        // 原因：Alt+A 是**微信截图默认热键**，装了微信的人永远拿不到；只靠"气泡+改键"
        // 要求用户自己动手，属能用但不好用。
        _hotkeyRegistered = hotkeys.TryRegister(HotkeyName, configured, () => TriggerCapture(fromHotkey: true));

        HotkeyGesture active = configured;
        if (!_hotkeyRegistered && configured == HotkeyGesture.AltA)
        {
            var fallback = new HotkeyGesture(HotkeyGesture.ModControl | HotkeyGesture.ModAlt, (uint)Keys.A);
            if (hotkeys.TryRegister(HotkeyName, fallback, () => TriggerCapture(fromHotkey: true)))
            {
                _hotkeyRegistered = true;
                active = fallback;
                _ctx.Log.Warn(
                    $"配置热键 {configured} 被占用（常见：微信），已自动降级为 {fallback}；可在「设置…」里改键。");
                Notify("热键已自动降级", $"{configured} 被占用，本次使用 {fallback}。", ToolTipIcon.Info);
            }
        }

        if (_hotkeyRegistered)
        {
            _changeHotkeyItem.Visible = false;
            _activeHotkey = active;
            _captureItem.Text = $"截图(&S)  {active}";
            _tray.Text = BuildTooltip();
            return;
        }

        // 连降级键也失败：露出「改键…」入口（气泡已由 RegistrationFailed 事件弹出）
        _activeHotkey = configured;
        _changeHotkeyItem.Visible = true;
        _captureItem.Text = $"截图(&S)  ({configured} 不可用)";
    }

    private void OnHotkeyRegistrationFailed(object? sender, HotkeyRegistrationFailedEventArgs e)
    {
        string reason = e.Win32Error switch
        {
            1409 => "该组合已被其它程序占用（常见：微信 / QQ / 其它截图工具）",
            0 => "组合键无效",
            _ => $"Win32 错误 {e.Win32Error}",
        };

        _ctx.Log.Warn($"热键 {e.Gesture}（{e.Name}）注册失败：{reason}");

        if (!ShowNotifications)
        {
            return;
        }

        _tray.ShowBalloonTip(
            5000,
            "全局热键不可用",
            $"{e.Gesture} 注册失败：{reason}。可从托盘菜单「改键…」换一个，或直接右键托盘图标截图。",
            ToolTipIcon.Warning);
    }

    // ------------------------------------------------------------------ 截图

    private bool ShowNotifications => _ctx.Settings?.App.ShowTrayNotifications ?? true;

    private void TriggerCapture(bool fromHotkey)
    {
        IShotCaptureUi? ui = _ctx.CaptureUi;
        if (ui is null)
        {
            Notify(
                "截图不可用",
                "采集/编辑层尚未装配：" + string.Join("、", _ctx.DescribeMissing()),
                ToolTipIcon.Info);
            return;
        }

        if (ui.IsRunning)
        {
            if (fromHotkey)
            {
                // 热键重按 = 取消（与微信一致；避免叠加第二层 overlay）
                ui.RequestCancel();
            }
            else
            {
                _ctx.Log.Info("已有截图会话在进行中，忽略托盘菜单触发。");
            }

            return;
        }

        _ = RunCaptureAsync(ui);
    }

    private async Task RunCaptureAsync(IShotCaptureUi ui)
    {
        UpdateRunningState(true);
        try
        {
            ShotResult result = await ui.RunAsync(null).ConfigureAwait(true);

            if (!result.Success && result.Error is not null)
            {
                _ctx.Log.Error("截图失败。", result.Error);
                Notify("截图失败", result.Error.Message, ToolTipIcon.Error);
            }
            else if (result.FilePath is not null)
            {
                _ctx.Log.Info($"截图产物：{result.FilePath}");
            }
        }
        catch (Exception ex)
        {
            // 兜底：异常冒到消息循环会弹 .NET 未处理异常框并退出托盘程序
            _ctx.Log.Error("截图管线抛出未处理异常。", ex);
            Notify("截图失败", ex.Message, ToolTipIcon.Error);
        }
        finally
        {
            UpdateRunningState(false);
        }
    }

    private void CancelCapture()
    {
        _ctx.CaptureUi?.RequestCancel();
    }

    private void UpdateRunningState(bool running)
    {
        if (_cancelItem is null || _tray is null)
        {
            return;
        }

        _cancelItem.Enabled = running;
        _tray.Text = running ? Truncate("ShotTool · 正在截图（Esc 取消）") : BuildTooltip();
    }

    // ------------------------------------------------------------------ 菜单动作

    private void ShowSettings()
    {
        ISettingsStore? settings = _ctx.Settings;
        if (settings is null)
        {
            Notify("设置不可用", "ISettingsStore 未装配。", ToolTipIcon.Info);
            return;
        }

        try
        {
            using var form = new SettingsForm(settings, onApplied: OnSettingsApplied);
            form.Icon = _icon;
            form.ShowDialog();
        }
        catch (Exception ex)
        {
            _ctx.Log.Error("打开设置窗口失败。", ex);
            Notify("设置打开失败", ex.Message, ToolTipIcon.Error);
        }
    }

    private void OnSettingsApplied()
    {
        (_ctx.Ai as IDisposable)?.Dispose();
        _ctx.ToolbarExtensions = new ShotTool.Editor.Toolbar.ToolbarRegistry();
        _ctx.Ai = ShotTool.Ai.AiServiceRegistry.Initialize(_ctx, _ctx.ToolbarExtensions, _ctx.Settings);
        // 热键/自启项可能在设置窗里改过：重新对齐运行时状态
        if (_ctx.Hotkeys is not null)
        {
            _ctx.Hotkeys.Unregister(HotkeyName);
            RegisterHotkey();
        }

        RefreshStartupState();
        _tray.Text = BuildTooltip();
    }

    private void ToggleStartup()
    {
        bool enable = !_startupItem.Checked;
        try
        {
            StartupRegistration.Apply(enable);
            if (_ctx.Settings is not null)
            {
                _ctx.Settings.App.StartupWithWindows = enable;
                _ctx.Settings.Save();
            }

            _startupItem.Checked = enable;
            _ctx.Log.Info($"开机自启已{(enable ? "启用" : "关闭")}。");
        }
        catch (Exception ex)
        {
            _ctx.Log.Error("设置开机自启失败。", ex);
            Notify("开机自启设置失败", ex.Message, ToolTipIcon.Error);
        }
    }

    private void RefreshStartupState()
    {
        bool enabled;
        try
        {
            enabled = StartupRegistration.IsEnabled() ?? (_ctx.Settings?.App.StartupWithWindows ?? false);
        }
        catch (Exception ex)
        {
            _ctx.Log.Warn($"读取开机自启状态失败：{ex.Message}");
            enabled = _ctx.Settings?.App.StartupWithWindows ?? false;
        }

        _startupItem.Checked = enabled;
    }

    private void OpenLogDirectory()
    {
        try
        {
            string directory = _ctx.Log is FileAppLog fileLog
                ? fileLog.Directory
                : FileAppLog.ResolveDirectory();

            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{directory}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _ctx.Log.Warn($"打开日志目录失败：{ex.Message}");
        }
    }

    private void ShowDiagnostics()
    {
        PhysRect bounds = MonitorService.GetVirtualDesktopBounds();
        IReadOnlyList<MonitorInfo> monitors = MonitorService.EnumerateMonitors(_ctx.Log);

        var lines = new List<string>
        {
            $"虚拟桌面：{MonitorService.DescribeVirtualDesktop()}",
            $"显示器：{MonitorService.DescribeMonitors(monitors)}",
            $"DPI 感知：{DescribeDpiAwareness()}",
            $"采集后端：{_ctx.Capture?.Backend.ToString() ?? "未装配"}",
            $"采集统计：{(_ctx.Capture as Capture.GdiCaptureSource)?.DescribeStats() ?? "-"}",
            $"热键：{DescribeHotkey()}",
            $"截图 UI：{_ctx.CaptureUi?.GetType().Name ?? "未装配"}",
            $"输出：{_ctx.Output?.GetType().Name ?? "未装配"}",
            $"日志：{(_ctx.Log as FileAppLog)?.FilePath ?? "(内存)"}",
            $"缺项：{DescribeMissing()}",
        };

        string text = string.Join(Environment.NewLine, lines);
        _ctx.Log.Info("诊断快照：" + text.Replace(Environment.NewLine, " | "));

        MessageBox.Show(text, "ShotTool 状态", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private static string DescribeDpiAwareness()
    {
        // app.manifest 已声明 PMv2 时此处返回 "由 manifest 提供"
        bool forced = DpiBootstrap.EnsurePerMonitorV2();
        return forced ? "本次强制设置成功（PerMonitorV2）" : "由 app.manifest 提供（PerMonitorV2）";
    }

    private string DescribeHotkey()
    {
        if (_ctx.Hotkeys is not null && _ctx.Hotkeys is GlobalHotkeyService service)
        {
            if (service.RegisteredGestures.TryGetValue(HotkeyName, out HotkeyGesture gesture))
            {
                return $"{gesture}（已注册，配置值 {_ctx.Settings?.App.CaptureHotkey}）";
            }
        }

        HotkeyGesture configured = _ctx.Settings?.App.CaptureHotkey ?? HotkeyGesture.AltA;
        return $"{configured}（未注册成功，请改键）";
    }

    private string DescribeMissing()
    {
        IReadOnlyList<string> missing = _ctx.DescribeMissing();
        return missing.Count == 0 ? "无" : string.Join("、", missing);
    }

    private string BuildTooltip()
        => Truncate($"{Program.AppDisplayName} · {MonitorService.DescribeVirtualDesktop()}");

    private static string Truncate(string text) => text.Length <= 63 ? text : text[..63];

    private void Notify(string title, string message, ToolTipIcon icon)
    {
        if (!ShowNotifications)
        {
            return;
        }

        _tray.ShowBalloonTip(4000, title, Truncate(message), icon);
    }

    private void ExitApplication()
    {
        _ctx.Log.Info("用户从托盘菜单退出。");
        ExitThread();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            _disposed = true;

            PinWindow.CloseAll();

            if (_ctx.Hotkeys is not null)
            {
                _ctx.Hotkeys.RegistrationFailed -= OnHotkeyRegistrationFailed;
            }

            _tray.Visible = false;
            _tray.Dispose();
            _menu.Dispose();
            _icon.Dispose();

            // ISettingsStore 可能持有 FileSystemWatcher（dev-integration 的实现是 IDisposable）
            if (_ctx.Settings is IDisposable disposableSettings)
            {
                disposableSettings.Dispose();
            }

            if (_ctx.Capture is IDisposable disposableCapture)
            {
                disposableCapture.Dispose();
            }

            _ctx.Hotkeys?.Dispose();
            (_ctx.Ai as IDisposable)?.Dispose();

            if (_ctx.Log is IDisposable disposableLog)
            {
                disposableLog.Dispose();
            }
        }

        base.Dispose(disposing);
    }
}
