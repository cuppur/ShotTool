using System.Runtime.InteropServices;
using ShotTool.Core;

namespace ShotTool.Interop;

/// <summary>Finds the visible top-level window under a screen point, excluding ShotTool.</summary>
internal static class WindowTargetPicker
{
    private const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;
    private const uint GA_ROOT = 2;
    private delegate bool EnumWindowsCallback(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsCallback callback, IntPtr lParam);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rect);
    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out NativeRect value, int size);

    internal static PhysRect? FindAt(PhysPoint point, IntPtr excludedWindow, PhysRect virtualBounds)
    {
        PhysRect? found = null;
        EnumWindowsCallback callback = (hwnd, _) =>
        {
            if (hwnd == excludedWindow || !IsWindowVisible(hwnd) || IsIconic(hwnd))
                return true;

            IntPtr root = GetAncestor(hwnd, GA_ROOT);
            if (root != IntPtr.Zero && root != hwnd)
                hwnd = root;

            if (!TryGetBounds(hwnd, out PhysRect bounds) || !bounds.Contains(point))
                return true;

            bounds = bounds.Intersect(virtualBounds);
            if (bounds.IsEmpty || bounds.Width < 8 || bounds.Height < 8)
                return true;

            found = bounds;
            return false; // EnumWindows follows top-to-bottom z-order.
        };
        _ = EnumWindows(callback, IntPtr.Zero);
        GC.KeepAlive(callback);
        return found;
    }

    private static bool TryGetBounds(IntPtr hwnd, out PhysRect bounds)
    {
        NativeRect rect;
        int hr = DwmGetWindowAttribute(hwnd, DWMWA_EXTENDED_FRAME_BOUNDS, out rect, Marshal.SizeOf<NativeRect>());
        if (hr < 0 && !GetWindowRect(hwnd, out rect))
        {
            bounds = PhysRect.Empty;
            return false;
        }
        bounds = PhysRect.FromEdges(rect.Left, rect.Top, rect.Right, rect.Bottom);
        return !bounds.IsEmpty;
    }
}
