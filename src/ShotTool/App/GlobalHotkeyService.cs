// ============================================================================
//  App/GlobalHotkeyService.cs —— 全局热键（IGlobalHotkeyService 实现）
//
//  实现要点：
//    * 一个 **消息专用窗口**（parent = HWND_MESSAGE）承接所有 WM_HOTKEY，
//      不出现在 Alt+Tab / 任务栏 / 枚举窗口里（不会干扰"截图时窗口枚举"这类诊断）。
//    * 命名注册（"capture" / "pin"）：同名重复注册等价于改键（先注销再注册）。
//    * 注册失败**必须**触发 RegistrationFailed 事件 —— 契约要求"不可静默失败"，
//      因为 Alt+A 被微信 / 其它截图工具占用是最常见的上手障碍。
//    * 回调在 UI 线程（WM_HOTKEY 由创建窗口的线程处理），调用方无需 marshal。
//    * 引擎 id 从 0x7A00 起，避开骨架期固定 id，且与 id 0 区分（RegisterHotKey 的 0 无效）。
// ============================================================================

using System.Runtime.InteropServices;
using ShotTool.Core;
using ShotTool.Interop;

namespace ShotTool.App;

/// <summary>基于 RegisterHotKey 的全局热键服务。</summary>
public sealed class GlobalHotkeyService : IGlobalHotkeyService
{
    private const int FirstHotkeyId = 0x7A00;
    private const int MaxHotkeys = 64;

    private readonly IAppLog? _log;
    private readonly Dictionary<string, Registration> _registrations = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<int, Registration> _byId = new();
    private readonly object _gate = new();

    private HotkeyWindow? _window;
    private int _nextId = FirstHotkeyId;
    private bool _disposed;

    public GlobalHotkeyService(IAppLog? log = null)
    {
        _log = log;
    }

    /// <summary>注册失败（被占用 / 无权限 / 组合非法）。</summary>
    public event EventHandler<HotkeyRegistrationFailedEventArgs>? RegistrationFailed;

    /// <summary>当前已注册的热键（诊断用）。</summary>
    public IReadOnlyDictionary<string, HotkeyGesture> RegisteredGestures
    {
        get
        {
            lock (_gate)
            {
                var snapshot = new Dictionary<string, HotkeyGesture>(_registrations.Count, StringComparer.OrdinalIgnoreCase);
                foreach (KeyValuePair<string, Registration> pair in _registrations)
                {
                    snapshot[pair.Key] = pair.Value.Gesture;
                }

                return snapshot;
            }
        }
    }

    public bool TryRegister(string name, HotkeyGesture gesture, Action callback)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("热键名称不能为空。", nameof(name));
        }

        ArgumentNullException.ThrowIfNull(callback);

        if (gesture.IsEmpty)
        {
            _log?.Warn($"热键「{name}」的组合为空，跳过注册。");
            RegistrationFailed?.Invoke(this, new HotkeyRegistrationFailedEventArgs(name, gesture, 0));
            return false;
        }

        lock (_gate)
        {
            if (_disposed)
            {
                return false;
            }

            IntPtr handle = EnsureWindowHandle();

            // 同名先注销（改键路径）
            if (_registrations.TryGetValue(name, out Registration? existing))
            {
                UnregisterCore(existing);
            }

            int id = AllocateId();
            if (id == 0)
            {
                _log?.Error($"热键「{name}」注册失败：热键槽位已满（最多 {MaxHotkeys} 个）。");
                RegistrationFailed?.Invoke(this, new HotkeyRegistrationFailedEventArgs(name, gesture, 0));
                return false;
            }

            if (!NativeMethods.RegisterHotKey(handle, id, gesture.RegisterFlags, gesture.VirtualKey))
            {
                int error = Marshal.GetLastWin32Error();
                _log?.Warn(
                    $"注册全局热键 {gesture}（{name}）失败，Win32={error}。" +
                    (error == 1409 ? "错误 1409 = 该组合已被其它程序占用。" : string.Empty));
                RegistrationFailed?.Invoke(this, new HotkeyRegistrationFailedEventArgs(name, gesture, error));
                return false;
            }

            var registration = new Registration(name, id, gesture, callback);
            _registrations[name] = registration;
            _byId[id] = registration;
            _log?.Info($"已注册全局热键 {gesture}（{name}，id=0x{id:X}）。");
            return true;
        }
    }

    public void Unregister(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        lock (_gate)
        {
            if (_registrations.TryGetValue(name, out Registration? registration))
            {
                UnregisterCore(registration);
                _log?.Info($"已注销全局热键 {registration.Gesture}（{name}）。");
            }
        }
    }

    public void UnregisterAll()
    {
        lock (_gate)
        {
            foreach (Registration registration in _registrations.Values)
            {
                UnregisterCore(registration);
            }

            _registrations.Clear();
            _byId.Clear();
        }
    }

    private void UnregisterCore(Registration registration)
    {
        if (_window is not null && _window.Handle != IntPtr.Zero)
        {
            NativeMethods.UnregisterHotKey(_window.Handle, registration.Id);
        }

        _registrations.Remove(registration.Name);
        _byId.Remove(registration.Id);
    }

    private int AllocateId()
    {
        if (_byId.Count >= MaxHotkeys)
        {
            return 0;
        }

        for (int attempt = 0; attempt < MaxHotkeys; attempt++)
        {
            int candidate = FirstHotkeyId + (_nextId - FirstHotkeyId) % MaxHotkeys;
            _nextId = candidate + 1;
            if (_nextId >= FirstHotkeyId + MaxHotkeys)
            {
                _nextId = FirstHotkeyId;
            }

            if (!_byId.ContainsKey(candidate))
            {
                return candidate;
            }
        }

        return 0;
    }

    private IntPtr EnsureWindowHandle()
    {
        _window ??= new HotkeyWindow(this);
        return _window.Handle;
    }

    /// <summary>WM_HOTKEY 派发（UI 线程）。</summary>
    private void OnHotkeyMessage(int id)
    {
        Registration? registration;
        lock (_gate)
        {
            if (!_byId.TryGetValue(id, out registration))
            {
                return;
            }
        }

        try
        {
            _log?.Info($"热键触发（{registration.Name}，{registration.Gesture}）。");
            registration.Callback();
        }
        catch (Exception ex)
        {
            // 绝不让异常冒到消息循环（会弹未处理异常框并退出托盘程序）
            _log?.Error($"热键「{registration.Name}」回调抛出异常。", ex);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            UnregisterAll();
            _window?.Destroy();
            _window = null;
        }
    }

    /// <summary>一条注册记录。</summary>
    private sealed class Registration
    {
        internal Registration(string name, int id, HotkeyGesture gesture, Action callback)
        {
            Name = name;
            Id = id;
            Gesture = gesture;
            Callback = callback;
        }

        internal string Name { get; }

        internal int Id { get; }

        internal HotkeyGesture Gesture { get; }

        internal Action Callback { get; }
    }

    /// <summary>
    /// 热键消息窗口。**顶层**不可见窗口（Style=0 且从不 ShowWindow，因此不显示、
    /// 不进 Alt+Tab；ExStyle 加 WS_EX_TOOLWINDOW 双保险）。
    ///
    /// ★ 为什么不是消息专用窗口（parent = HWND_MESSAGE）：
    ///   实测（t3，本机 Win11 x64）：以 message-only 子窗口注册 Ctrl+Alt+A 能成功（返回 true），
    ///   但**收不到 WM_HOTKEY** —— 合成按键与真实按键都不触发。改回顶层窗口后立即收到。
    ///   RegisterHotKey 的 hWnd 必须是能进队列的顶层窗口，这个坑只能靠实跑发现。
    /// </summary>
    private sealed class HotkeyWindow : NativeWindow
    {
        private readonly GlobalHotkeyService _owner;

        internal HotkeyWindow(GlobalHotkeyService owner)
        {
            _owner = owner;
            CreateHandle(new CreateParams
            {
                Caption = "ShotTool.HotkeySink",
                Style = 0,
                ExStyle = NativeConstants.WS_EX_TOOLWINDOW,
            });
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == NativeConstants.WM_HOTKEY)
            {
                _owner.OnHotkeyMessage((int)m.WParam.ToInt64());
                return;
            }

            base.WndProc(ref m);
        }

        internal void Destroy()
        {
            if (Handle != IntPtr.Zero)
            {
                DestroyHandle();
            }
        }
    }
}
