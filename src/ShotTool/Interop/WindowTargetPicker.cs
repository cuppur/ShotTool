using System.Runtime.InteropServices;
using System.Text;
using ShotTool.Core;

namespace ShotTool.Interop;

/// <summary>Frozen front-to-back HWND tree. Mouse movement performs no cross-process calls.</summary>
internal sealed class WindowTargetPicker : IDisposable
{
    internal sealed record Target(IntPtr Handle, PhysRect Bounds, IReadOnlyList<Target> Children,
        Rectangle? RegionOrigin = null, Region? HitRegion = null);
    private readonly IReadOnlyList<Target> _windows;
    internal WindowTargetPicker(IReadOnlyList<Target> windows) => _windows = windows;

    internal static WindowTargetPicker Snapshot(PhysRect desktop, IntPtr excludedWindow = default)
    {
        var windows = new List<Target>();
        int budget = 4096;
        EnumWindowsCallback callback = (hwnd, _) =>
        {
            if (hwnd == excludedWindow || !Eligible(hwnd)) return true;
            var name = new StringBuilder(256);
            _ = GetClassName(hwnd, name, name.Capacity);
            if (name.ToString() is "Progman" or "WorkerW" or "SysShadow" or "tooltips_class32") return true;
            if (!TryGetBounds(hwnd, out var bounds)) return true;
            if (IsZoomed(hwnd)) bounds = bounds.Intersect(PhysRect.FromRectangle(Screen.FromHandle(hwnd).Bounds));
            bounds = bounds.Intersect(desktop);
            if (bounds.Width < 8 || bounds.Height < 8) return true;
            windows.Add(ReadTarget(hwnd, bounds, 0, ref budget));
            return windows.Count < 512;
        };
        _ = EnumWindows(callback, IntPtr.Zero);
        GC.KeepAlive(callback);
        return new WindowTargetPicker(windows);
    }

    // Whole window first; descend only through the frontmost matching native child.
    internal IReadOnlyList<Target> FindAt(PhysPoint point)
    {
        var result = new List<Target>();
        var target = _windows.FirstOrDefault(w => Hits(w, point));
        while (target is not null)
        {
            if (result.Count == 0 || result[^1].Bounds != target.Bounds) result.Add(target);
            target = target.Children.FirstOrDefault(w => Hits(w, point));
        }
        return result;
    }

    private static bool Hits(Target target, PhysPoint point)
        => target.Bounds.Contains(point) && (target.HitRegion is null || target.RegionOrigin is not { } origin
            || target.HitRegion.IsVisible(point.X - origin.X, point.Y - origin.Y));

    private static Target ReadTarget(IntPtr hwnd, PhysRect bounds, int depth, ref int budget)
    {
        Region? hitRegion = null;
        Rectangle? origin = null;
        IntPtr hrgn = CreateRectRgn(0, 0, 0, 0);
        if (hrgn != IntPtr.Zero)
        {
            try
            {
                if (GetWindowRgn(hwnd, hrgn) > 0 && GetWindowRect(hwnd, out var raw))
                {
                    hitRegion = Region.FromHrgn(hrgn);
                    origin = Rectangle.FromLTRB(raw.Left, raw.Top, raw.Right, raw.Bottom);
                }
            }
            finally { _ = DeleteObject(hrgn); }
        }
        var children = new List<Target>();
        if (depth < 12 && budget > 0)
        {
            PhysRect clip = bounds;
            if (GetClientRect(hwnd, out var client))
            {
                var pt = new NativePoint();
                if (ClientToScreen(hwnd, ref pt))
                    clip = clip.Intersect(new PhysRect(pt.X, pt.Y, client.Right, client.Bottom));
            }
            for (IntPtr child = GetWindow(hwnd, 5); child != IntPtr.Zero && budget-- > 0; child = GetWindow(child, 2))
            {
                if (!Eligible(child) || !GetWindowRect(child, out var rect)) continue;
                var childBounds = PhysRect.FromEdges(rect.Left, rect.Top, rect.Right, rect.Bottom).Intersect(clip);
                if (childBounds.Width < 8 || childBounds.Height < 8) continue;
                children.Add(ReadTarget(child, childBounds, depth + 1, ref budget));
            }
        }
        return new Target(hwnd, bounds, children, origin, hitRegion);
    }

    private static bool Eligible(IntPtr hwnd)
    {
        if (!IsWindowVisible(hwnd) || IsIconic(hwnd)) return false;
        if (DwmGetWindowAttribute(hwnd, 14, out int cloaked, sizeof(int)) >= 0 && cloaked != 0) return false;
        long style = GetWindowLongPtr(hwnd, -20).ToInt64();
        if ((style & 0x80020) == 0x80020) return false;
        if ((style & 0x80000) != 0 && GetLayeredWindowAttributes(hwnd, out _, out byte alpha, out uint flags)
            && (flags & 2) != 0 && alpha == 0) return false;
        return true;
    }

    internal static bool TryGetBounds(IntPtr hwnd, out PhysRect bounds)
    {
        int hr = DwmGetWindowAttribute(hwnd, 9, out NativeRect rect, Marshal.SizeOf<NativeRect>());
        if (hr < 0 && !GetWindowRect(hwnd, out rect)) { bounds = PhysRect.Empty; return false; }
        bounds = PhysRect.FromEdges(rect.Left, rect.Top, rect.Right, rect.Bottom);
        return !bounds.IsEmpty;
    }

    public void Dispose()
    {
        static void Release(Target t) { t.HitRegion?.Dispose(); foreach (var child in t.Children) Release(child); }
        foreach (var window in _windows) Release(window);
    }

    private delegate bool EnumWindowsCallback(IntPtr hwnd, IntPtr lParam);
    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X, Y; }
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsCallback callback, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool IsZoomed(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rect);
    [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr hwnd, out NativeRect rect);
    [DllImport("user32.dll")] private static extern bool ClientToScreen(IntPtr hwnd, ref NativePoint point);
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr hwnd, uint command);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hwnd, StringBuilder name, int count);
    [DllImport("user32.dll")] private static extern bool GetLayeredWindowAttributes(IntPtr hwnd, out uint key, out byte alpha, out uint flags);
    [DllImport("user32.dll")] private static extern int GetWindowRgn(IntPtr hwnd, IntPtr region);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateRectRgn(int left, int top, int right, int bottom);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr handle);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out NativeRect value, int size);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out int value, int size);
}
