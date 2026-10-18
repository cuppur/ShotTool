// ============================================================================
//  Interop/NativeMethods.cs —— Win32 P/Invoke 声明（dev-capture 独占）
//
//  范围：只声明本层真正用到的 API（user32 / gdi32 / shcore 三家）。
//  约定：
//    1) 全部 internal，不对其它层暴露（其它层要能力走 Core 契约，不要直接 P/Invoke）。
//    2) 结构与常量在此集中定义，任何调用点不得自造魔数（避免 SRCCOPY=0x00CC0020
//       写错一位导致"抓屏全黑"这类静默故障）。
//    3) 所有 SetLastError=true + Marshal.GetLastWin32Error() 的用法由调用方负责，
//       本文件不吞错误码。
//
//  DPI 语境（ADR-005）：进程 PerMonitorV2 下，下列 API 的坐标全部是**物理像素**：
//    GetSystemMetrics(SM_*VIRTUALSCREEN) / EnumDisplayMonitors 的 rcMonitor / GetCursorPos。
//    任何调用点都不许再乘除 DPI 系数（那是 2.25 倍偏移的直接来源）。
// ============================================================================

using System.Runtime.InteropServices;

namespace ShotTool.Interop;

/// <summary>Win32 常量（只放本层用到的）。</summary>
internal static class NativeConstants
{
    // ---- GetSystemMetrics 索引 ----
    internal const int SM_XVIRTUALSCREEN = 76;
    internal const int SM_YVIRTUALSCREEN = 77;
    internal const int SM_CXVIRTUALSCREEN = 78;
    internal const int SM_CYVIRTUALSCREEN = 79;
    internal const int SM_CMONITORS = 80;

    // ---- BitBlt ROP ----
    /// <summary>源拷贝。</summary>
    internal const uint SRCCOPY = 0x00CC0020;

    /// <summary>
    /// 包含分层窗口（= 让 DWM 先把合成结果画出来再拷）。
    /// ADR-002：默认开启，代价 &lt; 5 ms，收益是能抓到现代应用的分层窗口。
    /// </summary>
    internal const uint CAPTUREBLT = 0x40000000;

    // ---- 窗口样式 ----
    internal const int WS_EX_TOOLWINDOW = 0x00000080;
    internal const int WS_EX_TOPMOST = 0x00000008;
    internal const int WS_EX_NOACTIVATE = 0x08000000;

    internal const uint SWP_NOSIZE = 0x0001;
    internal const uint SWP_NOMOVE = 0x0002;
    internal const uint SWP_NOZORDER = 0x0004;
    internal const uint SWP_NOACTIVATE = 0x0010;
    internal const uint SWP_SHOWWINDOW = 0x0040;

    /// <summary>HWND_TOPMOST 的 IntPtr 形态（-1）。</summary>
    internal static readonly IntPtr HWND_TOPMOST = new(-1);

    /// <summary>消息专用窗口的父句柄（HWND_MESSAGE = -3）。</summary>
    internal static readonly IntPtr HWND_MESSAGE = new(-3);

    // ---- 热键 ----
    internal const int WM_HOTKEY = 0x0312;

    // ---- DPI ----
    internal const int MDT_EFFECTIVE_DPI = 0;

    internal const uint DefaultDpi = 96;
}

/// <summary>Win32 POINT（物理像素）。</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct NativePoint
{
    internal int X;
    internal int Y;

    internal NativePoint(int x, int y)
    {
        X = x;
        Y = y;
    }
}

/// <summary>Win32 RECT（物理像素，右/下为开区间外沿）。</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct NativeRect
{
    internal int Left;
    internal int Top;
    internal int Right;
    internal int Bottom;

    internal readonly int Width => Right - Left;

    internal readonly int Height => Bottom - Top;

    public override readonly string ToString() => $"({Left},{Top})-({Right},{Bottom}) {Width}x{Height}";
}

/// <summary>MONITORINFOEX 的前半部分（GetMonitorInfo 所需最小布局）。</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct NativeMonitorInfo
{
    internal int Size;
    internal NativeRect Monitor;
    internal NativeRect Work;
    internal uint Flags;
}

/// <summary>枚举显示器回调。</summary>
[UnmanagedFunctionPointer(CallingConvention.Winapi)]
internal delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdcMonitor, ref NativeRect lprcMonitor, IntPtr dwData);

/// <summary>Win32 API 声明。每个函数的"为什么需要"写在成员注释里。</summary>
internal static class NativeMethods
{
    // ------------------------------------------------------------------ user32

    /// <summary>虚拟桌面尺寸（PMv2 下为物理像素）。</summary>
    [DllImport("user32.dll")]
    internal static extern int GetSystemMetrics(int index);

    /// <summary>光标位置（PMv2 下为物理像素，虚拟桌面坐标系）。</summary>
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetCursorPos(out NativePoint point);

    /// <summary>取屏幕 DC；<paramref name="hWnd"/> = IntPtr.Zero 即整屏（含多屏，坐标可为负）。</summary>
    [DllImport("user32.dll", SetLastError = true)]
    internal static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    /// <summary>抓屏核心：从屏幕 DC 拷到目标 DC。不包含光标（ADR-002 §2.4-4）。</summary>
    /// <remarks>
    /// ★ 注意 DLL 归属：BitBlt 属于 **gdi32.dll**（不是 user32.dll）。
    /// 写成 user32 会在运行期抛 "Unable to find an entry point named 'BitBlt'" ——
    /// 编译期查不出来（P/Invoke 是运行期解析），只能靠实跑自测暴露。
    /// 声明放在本类底部 gdi32 区域，这里保留注释避免以后又被挪错。
    /// </remarks>
    [DllImport("gdi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool BitBlt(
        IntPtr hdcDest,
        int xDest,
        int yDest,
        int width,
        int height,
        IntPtr hdcSrc,
        int xSrc,
        int ySrc,
        uint rop);

    /// <summary>逐个显示器回调（跨 DPI / 负原点必须逐屏拼，见 ADR-002 §2.4-1）。</summary>
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, MonitorEnumProc lpfnEnum, IntPtr dwData);

    /// <summary>显示器矩形与工作区。</summary>
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetMonitorInfo(IntPtr hMonitor, ref NativeMonitorInfo lpmi);

    /// <summary>overlay 置顶（不改变尺寸/位置时用 SWP_NOMOVE|SWP_NOSIZE）。</summary>
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetWindowPos(
        IntPtr hWnd,
        IntPtr hWndInsertAfter,
        int x,
        int y,
        int cx,
        int cy,
        uint flags);

    /// <summary>overlay 显示后抢前台（否则键盘事件到不了 overlay）。</summary>
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern IntPtr SetFocus(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern IntPtr GetForegroundWindow();

    /// <summary>全局热键注册（MOD_NOREPEAT 由 HotkeyGesture.RegisterFlags 提供）。</summary>
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    /// <summary>托盘图标若走 HICON 路径需要释放；当前实现用 PNG-in-ICO 流，不产生 HICON。</summary>
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DestroyIcon(IntPtr hIcon);

    /// <summary>单显示器 DPI（Win10 1607+）。取不到时回落 96 并记录日志。</summary>
    [DllImport("user32.dll", SetLastError = true)]
    internal static extern uint GetDpiForWindow(IntPtr hWnd);

    // ------------------------------------------------------------------ psapi

    /// <summary>
    /// 当前进程的 GDI 对象数（自测用：验证"完成/取消两条路径都不泄漏画笔/DC/位图"）。
    /// GR_GDIOBJECTS = 0；调用进程句柄用 <see cref="GetCurrentProcessHandle"/>。
    /// </summary>
    [DllImport("user32.dll")]
    internal static extern uint GetGuiResources(IntPtr hProcess, uint flags);

    /// <summary>当前伪进程句柄（GetCurrentProcess 的形态，不需要 CloseHandle）。</summary>
    [DllImport("kernel32.dll")]
    internal static extern IntPtr GetCurrentProcess();

    // ------------------------------------------------------------------- gdi32

    [DllImport("gdi32.dll", SetLastError = true)]
    internal static extern IntPtr CreateCompatibleDC(IntPtr hdc);

    [DllImport("gdi32.dll", SetLastError = true)]
    internal static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int width, int height);

    [DllImport("gdi32.dll", SetLastError = true)]
    internal static extern IntPtr SelectObject(IntPtr hdc, IntPtr gdiObject);

    [DllImport("gdi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DeleteObject(IntPtr gdiObject);

    [DllImport("gdi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DeleteDC(IntPtr hdc);

    // ------------------------------------------------------------------ shcore

    /// <summary>按显示器读有效 DPI（PerMonitorV2 下唯一正确的取值方式）。</summary>
    [DllImport("shcore.dll", SetLastError = true)]
    internal static extern int GetDpiForMonitor(IntPtr hMonitor, int dpiType, out uint dpiX, out uint dpiY);
}
