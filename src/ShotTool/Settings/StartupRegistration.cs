using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using Microsoft.Win32;

namespace ShotTool.Settings;

/// <summary>Per-user logon task, immutable installed payload, and legacy Run migration.</summary>
public static class StartupRegistration
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ApprovedPath = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    public const string ValueName = "ShotTool";
    internal static string TaskName => "ShotTool Startup " + WindowsIdentity.GetCurrent().User!.Value;
    public sealed record Status(bool? Enabled, string Description, string? Executable = null);

    public static void Apply(bool enabled, string? executablePath = null)
    {
        if (!enabled)
        {
            DeleteTask(TaskName);
            using var old = Registry.CurrentUser.OpenSubKey(RunKeyPath, true);
            old?.DeleteValue(ValueName, false);
            return;
        }
        string installed = InstallPayload(executablePath ?? ResolveExecutablePath());
        try
        {
            RegisterTask(TaskName, installed);
            using var old = Registry.CurrentUser.OpenSubKey(RunKeyPath, true);
            old?.DeleteValue(ValueName, false); // one mechanism, no duplicate launch
        }
        catch (COMException taskError)
        {
            // Restricted PCs may deny task creation. Keep an explicit, verifiable fallback.
            using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, true)
                ?? throw new UnauthorizedAccessException("无法登记登录自启。", taskError);
            key.SetValue(ValueName, BuildCommand(installed));
            if (ReadRunApproval() == false)
                throw new InvalidOperationException("系统已禁用 ShotTool 启动项，请在 Windows 设置 → 应用 → 启动中启用；程序不会改写系统的禁用记录。", taskError);
        }
        var status = ReadStatus();
        if (status.Enabled != true) throw new InvalidOperationException(status.Description);
    }

    public static Status ReadStatus()
    {
        try
        {
            (bool Enabled, string Path)? task;
            try { task = ReadTask(TaskName); }
            catch (COMException) { task = null; }
            if (task is not null)
                return new Status(task.Value.Enabled && File.Exists(task.Value.Path),
                    !File.Exists(task.Value.Path) ? "登录任务的程序文件已丢失，请重新启用。"
                    : !task.Value.Enabled ? "登录任务已被系统禁用，请重新启用。"
                    : "已启用：登录任务；使用用户应用目录，允许电池供电，启动失败自动重试。", task.Value.Path);
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
            string? command = key?.GetValue(ValueName) as string;
            if (string.IsNullOrWhiteSpace(command)) return new Status(false, "尚未登记登录自启。");
            string path = ParseExecutable(command);
            if (!File.Exists(path)) return new Status(false, "旧自启路径已失效，请重新启用以修复。", path);
            if (ReadRunApproval() == false) return new Status(false, "已被 Windows 启动设置禁用，请重新启用。", path);
            return new Status(true, "已启用：Windows 启动项（备用方式，系统可能延迟启动）。", path);
        }
        catch (Exception ex) { return new Status(null, "无法读取系统自启状态：" + ex.Message); }
    }

    public static bool? IsEnabled() => ReadStatus().Enabled;
    internal static string BuildCommand(string path) => $"\"{path}\" --startup";
    internal static string ParseExecutable(string command)
    {
        command = command.Trim();
        if (command.StartsWith('"'))
        {
            int end = command.IndexOf('"', 1);
            return end > 1 ? command[1..end] : string.Empty;
        }
        int split = command.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        return split >= 0 ? command[..(split + 4)] : string.Empty;
    }
    private static bool? ReadRunApproval()
    {
        using var key = Registry.CurrentUser.OpenSubKey(ApprovedPath);
        return key?.GetValue(ValueName) is byte[] { Length: >= 4 } state ? state[0] is 2 or 6 : null;
    }

    internal static string InstallPayload(string executable, string? appRoot = null)
    {
        executable = Path.GetFullPath(executable);
        if (!File.Exists(executable)) throw new FileNotFoundException("截图程序不存在，无法登记自启。", executable);
        if (File.Exists(Path.Combine(Path.GetDirectoryName(executable)!, "ShotTool.dll")))
            throw new InvalidOperationException("开机自启请使用发布版的单文件 ShotTool.exe，不能直接登记开发构建目录。");
        using var input = File.OpenRead(executable);
        string id = Convert.ToHexString(SHA256.HashData(input))[..16].ToLowerInvariant();
        string root = appRoot ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ShotTool", "App");
        string directory = Path.Combine(root, id);
        string target = Path.Combine(directory, "ShotTool.exe");
        Directory.CreateDirectory(directory);
        if (!File.Exists(target))
        {
            string temporary = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".tmp");
            try { File.Copy(executable, temporary); File.Move(temporary, target, overwrite: true); }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        string notices = Path.Combine(Path.GetDirectoryName(executable)!, "licenses");
        if (Directory.Exists(notices))
            foreach (string notice in Directory.EnumerateFiles(notices, "*", SearchOption.AllDirectories))
            {
                string destination = Path.Combine(directory, "licenses", Path.GetRelativePath(notices, notice));
                if (string.Equals(notice, destination, StringComparison.OrdinalIgnoreCase)) continue;
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(notice, destination, overwrite: true);
            }
        return target;
    }

    internal static void RegisterTask(string name, string executable, bool enabled = true)
    {
        WithScheduler((service, folder) =>
        {
            dynamic definition = service.NewTask(0);
            try
            {
                string sid = WindowsIdentity.GetCurrent().User!.Value;
                definition.RegistrationInfo.Description = "ShotTool：当前用户登录后启动截图托盘。可在程序设置或任务计划程序中禁用。";
                definition.Principal.UserId = sid;
                definition.Principal.LogonType = 3; // InteractiveToken; no stored password or elevation
                definition.Principal.RunLevel = 0;
                definition.Settings.Enabled = enabled;
                definition.Settings.DisallowStartIfOnBatteries = false;
                definition.Settings.StopIfGoingOnBatteries = false;
                definition.Settings.StartWhenAvailable = true;
                definition.Settings.ExecutionTimeLimit = "PT0S";
                definition.Settings.RestartCount = 3;
                definition.Settings.RestartInterval = "PT1M";
                definition.Settings.MultipleInstances = 2;
                dynamic trigger = definition.Triggers.Create(9);
                trigger.UserId = sid;
                trigger.Delay = "PT5S";
                dynamic action = definition.Actions.Create(0);
                action.Path = executable;
                action.Arguments = "--startup";
                action.WorkingDirectory = Path.GetDirectoryName(executable);
                dynamic registered = folder.RegisterTaskDefinition(name, definition, 6, sid, null, 3, null);
                Marshal.FinalReleaseComObject(registered);
                Marshal.FinalReleaseComObject(trigger);
                Marshal.FinalReleaseComObject(action);
            }
            finally { Marshal.FinalReleaseComObject(definition); }
        });
    }

    internal static (bool Enabled, string Path)? ReadTask(string name)
    {
        (bool, string)? result = null;
        WithScheduler((_, folder) =>
        {
            dynamic task;
            try { task = folder.GetTask(name); }
            catch (COMException ex) when ((uint)ex.HResult is 0x80070002 or 0x8004130F) { return; }
            try { result = ((bool)task.Enabled, (string)task.Definition.Actions.Item(1).Path); }
            finally { Marshal.FinalReleaseComObject(task); }
        });
        return result;
    }
    internal static void DeleteTask(string name)
    {
        WithScheduler((_, folder) =>
        {
            try { folder.DeleteTask(name, 0); }
            catch (COMException ex) when ((uint)ex.HResult is 0x80070002 or 0x8004130F) { }
        });
    }
    private static void WithScheduler(Action<dynamic, dynamic> action)
    {
        dynamic service = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service")!)!;
        dynamic? folder = null;
        try { service.Connect(); folder = service.GetFolder("\\"); action(service, folder); }
        finally { if (folder is not null) Marshal.FinalReleaseComObject(folder); Marshal.FinalReleaseComObject(service); }
    }
    public static string ResolveExecutablePath() => Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "ShotTool.exe");
}
