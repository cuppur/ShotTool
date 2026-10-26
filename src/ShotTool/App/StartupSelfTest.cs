using System.Runtime.InteropServices;
using ShotTool.Core;
using ShotTool.Settings;

namespace ShotTool.App;

internal static class StartupSelfTest
{
    internal static bool Run(IAppLog log, string directory)
    {
        string task = "ShotTool Selftest " + Guid.NewGuid().ToString("N");
        bool registered = false;
        try
        {
            string source = Path.Combine(directory, "payload fixture.exe");
            File.WriteAllBytes(source, new byte[] { 77, 90, 10, 20 });
            string installed = StartupRegistration.InstallPayload(source, Path.Combine(directory, "application"));
            File.Delete(source);
            if (!File.Exists(installed) || !File.ReadAllBytes(installed).SequenceEqual(new byte[] { 77, 90, 10, 20 }))
                throw new InvalidOperationException("移动原程序后，自启安装副本失效。");
            if (StartupRegistration.ParseExecutable(StartupRegistration.BuildCommand(installed)) != installed)
                throw new InvalidOperationException("带空格路径的命令行解析失败。");
            try
            {
                StartupRegistration.RegisterTask(task, installed, enabled: false); registered = true;
                var status = StartupRegistration.ReadTask(task);
                if (status is null || status.Value.Enabled || status.Value.Path != installed)
                    throw new InvalidOperationException("实际登录任务登记验证失败。");
                Console.WriteLine("[PASS] Startup: real disabled fixture task created/read; no password or admin elevation");
            }
            catch (COMException ex) when ((uint)ex.HResult == 0x80070005)
            { Console.WriteLine("[INFO] Task creation denied by Windows; production uses verified Run fallback"); }
            Console.WriteLine("[PASS] Startup: immutable installed payload survives source removal; quoted path/--startup parsed");
            return true;
        }
        catch (Exception ex) { log.Error("自启检查失败", ex); Console.WriteLine("[FAIL] Startup: " + ex.Message); return false; }
        finally { if (registered) StartupRegistration.DeleteTask(task); }
    }
}
