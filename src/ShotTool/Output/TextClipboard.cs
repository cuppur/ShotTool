using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace ShotTool.Output;

// 直接使用 Unicode 文本格式，与图片输出一样对短暂剪贴板占用重试。
internal static class TextClipboard
{
    private const uint UnicodeText = 13;

    internal static void Write(string text)
    {
        byte[] bytes = Encoding.Unicode.GetBytes(text + '\0');
        IntPtr memory = GlobalAlloc(0x42, (nuint)bytes.Length);
        if (memory == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
        bool opened = false;
        try
        {
            IntPtr data = GlobalLock(memory);
            if (data == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
            try { Marshal.Copy(bytes, 0, data, bytes.Length); }
            finally { GlobalUnlock(memory); }
            Open();
            opened = true;
            if (!EmptyClipboard() || SetClipboardData(UnicodeText, memory) == IntPtr.Zero)
                throw new Win32Exception(Marshal.GetLastWin32Error());
            memory = IntPtr.Zero; // Windows 接管内存。
        }
        finally
        {
            if (opened) CloseClipboard();
            if (memory != IntPtr.Zero) GlobalFree(memory);
        }
    }

    internal static string Read()
    {
        Open();
        try
        {
            IntPtr memory = GetClipboardData(UnicodeText);
            if (memory == IntPtr.Zero) return string.Empty;
            IntPtr data = GlobalLock(memory);
            if (data == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
            try { return Marshal.PtrToStringUni(data) ?? string.Empty; }
            finally { GlobalUnlock(memory); }
        }
        finally { CloseClipboard(); }
    }

    private static void Open()
    {
        for (int i = 0; i < 30; i++)
        {
            if (OpenClipboard(IntPtr.Zero)) return;
            Thread.Sleep(20);
        }
        throw new InvalidOperationException("剪贴板暂时被占用，请稍后重试。");
    }

    [DllImport("user32.dll", SetLastError = true)] private static extern bool OpenClipboard(IntPtr owner);
    [DllImport("user32.dll")] private static extern bool CloseClipboard();
    [DllImport("user32.dll", SetLastError = true)] private static extern bool EmptyClipboard();
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetClipboardData(uint format, IntPtr data);
    [DllImport("user32.dll")] private static extern IntPtr GetClipboardData(uint format);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr GlobalAlloc(uint flags, nuint size);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr GlobalLock(IntPtr memory);
    [DllImport("kernel32.dll")] private static extern bool GlobalUnlock(IntPtr memory);
    [DllImport("kernel32.dll")] private static extern IntPtr GlobalFree(IntPtr memory);
}
