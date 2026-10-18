// ============================================================================
//  Interop/MonitorService.cs —— 虚拟桌面 / 显示器 / DPI / 光标读数
//
//  这是全项目**唯一**读取屏幕几何的地方（Capture 与 overlay 都调它）。
//  输出一律是 ShotTool.Core 的 Phys* 物理像素类型（ADR-005）。
//
//  为什么不是 WinForms 的 Screen.AllScreens：
//    * Screen.Bounds 在 PerMonitorV2 下虽也是设备像素，但不提供每屏 DPI，
//      且 v1 版本对混合 DPI 的处理依赖进程 DPI 上下文，行为不如直接 EnumDisplayMonitors 确定；
//    * 采集层（Capture/）不应依赖 System.Windows.Forms（GDI 抓屏本身与 WinForms 无关，
//      将来换 WGC/DXGI 后端时也不该拖着 WinForms）。
// ============================================================================

using System.Runtime.InteropServices;
using ShotTool.Core;

namespace ShotTool.Interop;

/// <summary>屏幕几何读数（虚拟桌面、显示器列表、光标、DPI）。全部无状态。</summary>
internal static class MonitorService
{
    /// <summary>虚拟桌面并集（可为负原点；物理像素）。</summary>
    internal static PhysRect GetVirtualDesktopBounds()
    {
        int x = NativeMethods.GetSystemMetrics(NativeConstants.SM_XVIRTUALSCREEN);
        int y = NativeMethods.GetSystemMetrics(NativeConstants.SM_YVIRTUALSCREEN);
        int w = NativeMethods.GetSystemMetrics(NativeConstants.SM_CXVIRTUALSCREEN);
        int h = NativeMethods.GetSystemMetrics(NativeConstants.SM_CYVIRTUALSCREEN);

        if (w <= 0 || h <= 0)
        {
            // 无桌面会话（服务/断开的 RDP）：返回空，让上层抛 CaptureException 而不是画个 0×0 窗口
            return PhysRect.Empty;
        }

        return new PhysRect(x, y, w, h);
    }

    /// <summary>光标位置（物理像素，虚拟桌面坐标系）。失败返回 (0,0) 并交给调用方判断。</summary>
    internal static PhysPoint GetCursorPosition()
        => NativeMethods.GetCursorPos(out NativePoint p) ? new PhysPoint(p.X, p.Y) : PhysPoint.Zero;

    /// <summary>
    /// 枚举所有显示器（rcMonitor / rcWork / 每屏 DPI / 是否主屏）。
    /// 枚举失败或系统未返回时返回空列表 —— 上层据此退化为「单屏 = 虚拟桌面」的假设。
    /// </summary>
    internal static IReadOnlyList<MonitorInfo> EnumerateMonitors(IAppLog? log = null)
    {
        var result = new List<MonitorInfo>(4);

        bool ok = NativeMethods.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, Callback, IntPtr.Zero);
        if (!ok)
        {
            int error = Marshal.GetLastWin32Error();
            log?.Warn($"EnumDisplayMonitors 失败（Win32={error}），退化为单屏假设。");
        }

        return result;

        bool Callback(IntPtr hMonitor, IntPtr hdcMonitor, ref NativeRect rect, IntPtr data)
        {
            var info = new NativeMonitorInfo { Size = Marshal.SizeOf<NativeMonitorInfo>() };
            if (!NativeMethods.GetMonitorInfo(hMonitor, ref info))
            {
                // 拿不到信息就跳过这一屏，但仍继续枚举其它屏
                log?.Warn($"GetMonitorInfo 失败（Win32={Marshal.GetLastWin32Error()}），跳过该显示器。");
                return true;
            }

            uint dpi = QueryMonitorDpi(hMonitor, log) ?? DefaultDpi;

            var bounds = new PhysRect(
                info.Monitor.Left,
                info.Monitor.Top,
                info.Monitor.Width,
                info.Monitor.Height);
            var work = new PhysRect(
                info.Work.Left,
                info.Work.Top,
                info.Work.Width,
                info.Work.Height);

            // MONITORINFOF_PRIMARY == 0x1
            bool primary = (info.Flags & 0x1) != 0;
            result.Add(new MonitorInfo($"\\\\.\\DISPLAY{result.Count + 1}", bounds, work, dpi, primary));
            return true;
        }
    }

    /// <summary>
    /// 读取显示器 DPI。三种来源依次尝试：
    ///   1) GetDpiForMonitor(MDT_EFFECTIVE_DPI)  —— PerMonitorV2 下的权威值；
    ///   2) 失败时返回 null（由调用方回落 96 并记录日志）。
    /// </summary>
    private static uint? QueryMonitorDpi(IntPtr hMonitor, IAppLog? log)
    {
        try
        {
            int hr = NativeMethods.GetDpiForMonitor(hMonitor, NativeConstants.MDT_EFFECTIVE_DPI, out uint dpiX, out _);
            if (hr == 0 && dpiX >= 48)
            {
                return DpiHelper.Sanitize(dpiX);
            }

            if (hr != 0)
            {
                log?.Warn($"GetDpiForMonitor 返回 HRESULT=0x{hr:X8}，该屏回落 96 DPI。");
            }
        }
        catch (DllNotFoundException)
        {
            // Win8.1 之前没有 shcore；本机不涉及，保持不崩
            log?.Warn("shcore.dll 不可用，DPI 回落 96。");
        }
        catch (EntryPointNotFoundException)
        {
            log?.Warn("GetDpiForMonitor 入口点缺失，DPI 回落 96。");
        }

        return null;
    }

    /// <summary>本进程默认 DPI：取主屏有效 DPI（overlay / 工具条的 DIP→px 换算基数）。</summary>
    internal static uint GetPrimaryDpi()
    {
        IReadOnlyList<MonitorInfo> monitors = EnumerateMonitors();
        foreach (MonitorInfo monitor in monitors)
        {
            if (monitor.IsPrimary)
            {
                return monitor.Dpi;
            }
        }

        return monitors.Count > 0 ? monitors[0].Dpi : DefaultDpi;
    }

    /// <summary>默认 DPI（100%）。</summary>
    internal static uint DefaultDpi => NativeConstants.DefaultDpi;

    /// <summary>诊断串：托盘「状态」菜单用它自证 PMv2 是否生效（本机应为 <c>3840x2160</c>）。</summary>
    internal static string DescribeVirtualDesktop()
    {
        PhysRect bounds = GetVirtualDesktopBounds();
        if (bounds.IsEmpty)
        {
            return "(无桌面会话)";
        }

        int monitorCount = NativeMethods.GetSystemMetrics(NativeConstants.SM_CMONITORS);
        return $"{bounds.Width}x{bounds.Height} @ ({bounds.X},{bounds.Y})，{monitorCount} 屏";
    }

    /// <summary>把显示器列表压成一行诊断（每屏尺寸 + DPI）。</summary>
    internal static string DescribeMonitors(IReadOnlyList<MonitorInfo> monitors)
    {
        if (monitors.Count == 0)
        {
            return "(未枚举到显示器)";
        }

        var parts = new List<string>(monitors.Count);
        foreach (MonitorInfo monitor in monitors)
        {
            parts.Add($"{monitor.Bounds.Width}x{monitor.Bounds.Height}@{monitor.Dpi}{(monitor.IsPrimary ? "*" : string.Empty)}");
        }

        return string.Join(" | ", parts);
    }
}
