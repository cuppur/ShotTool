using Microsoft.Win32;

namespace ShotTool.Settings;

/// <summary>
/// 「开机自启」的注册表落地（<c>HKCU\Software\Microsoft\Windows\CurrentVersion\Run</c>）。
///
/// <para>
/// 用 HKCU 而不是计划任务：不需要管理员权限，用户可在「任务管理器 → 启动」里自行禁用。
/// 只写当前 exe 的绝对路径（带引号，防路径含空格）。
/// </para>
/// <para>
/// 读取失败（策略限制/权限）返回 <c>null</c>，调用方应回退到配置值而不是报错。
/// </para>
/// </summary>
public static class StartupRegistration
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

    /// <summary>注册表值名。产品名固定为 ShotTool（与 exe 名一致）。</summary>
    public const string ValueName = "ShotTool";

    /// <summary>
    /// 写入或删除自启项。
    /// </summary>
    /// <param name="enabled">true = 开机启动；false = 移除。</param>
    /// <param name="executablePath">默认当前进程 exe（单文件发布时即 ShotTool.exe）。</param>
    /// <exception cref="UnauthorizedAccessException">注册表不可写（策略/权限）。</exception>
    public static void Apply(bool enabled, string? executablePath = null)
    {
        using RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true)
            ?? throw new UnauthorizedAccessException($"无法打开注册表键 HKCU\\{RunKeyPath}。");

        if (!enabled)
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
            return;
        }

        string exe = executablePath ?? ResolveExecutablePath();
        key.SetValue(ValueName, $"\"{exe}\"", RegistryValueKind.String);
    }

    /// <summary>
    /// 查询当前是否已登记自启。
    /// 返回 null 表示读取失败（调用方应回退到配置值）。
    /// </summary>
    public static bool? IsEnabled()
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            object? value = key?.GetValue(ValueName);
            return value is string text && !string.IsNullOrWhiteSpace(text);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>当前进程 exe 路径（单文件发布时就是 ShotTool.exe 本身）。</summary>
    public static string ResolveExecutablePath()
    {
        string? processPath = Environment.ProcessPath;
        if (!string.IsNullOrWhiteSpace(processPath))
        {
            return processPath;
        }

        return Path.Combine(AppContext.BaseDirectory, "ShotTool.exe");
    }
}
