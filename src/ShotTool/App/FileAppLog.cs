// ============================================================================
//  App/FileAppLog.cs —— 落盘日志（IAppLog 实现）
//
//  为什么不用 Trace 就够了：本程序是**托盘常驻、无控制台**的 WinExe，
//  `Debug.WriteLine` 只在挂了调试器时可见。截图链路出问题时（热键没反应、
//  抓屏全黑、完成路径没把位图交出去）必须能在事后查 —— 所以日志同时落盘。
//
//  路径：%LOCALAPPDATA%\ShotTool\logs\shottool-yyyyMMdd.log
//        （可用 SHOTTOOL_LOG_DIR 环境变量覆盖，便于自动化验证时隔离目录）
//  策略：单文件按天切分、追加写、写入失败静默降级（日志不能反过来影响主流程）。
// ============================================================================

using System.Diagnostics;
using System.IO;
using System.Text;
using ShotTool.Core;

namespace ShotTool.App;

/// <summary>追加式文件日志（线程安全）。</summary>
public sealed class FileAppLog : IAppLog, IDisposable
{
    private readonly object _gate = new();
    private readonly string _directory;
    private readonly string _filePath;
    private StreamWriter? _writer;
    private bool _disposed;

    public FileAppLog(string? directory = null)
    {
        _directory = ResolveDirectory(directory);

        // 文件名带 PID：单实例约束下本应只有一个写者，但"第二个实例刚启动就退出"与
        // 自测/多开调试都会并发写同一个文件 —— 实测会把行撕裂成半截（错位的乱码）。
        // 每个进程独立文件最简单，也最不容易误导排查。
        _filePath = Path.Combine(_directory, $"shottool-{DateTime.Now:yyyyMMdd}-{Environment.ProcessId}.log");
    }

    /// <summary>日志目录（日志文件所在目录）。</summary>
    public string Directory => _directory;

    /// <summary>当前日志文件完整路径。</summary>
    public string FilePath => _filePath;

    /// <summary>每条日志落盘后触发（诊断面板 / 自测可订阅，不影响主流程）。</summary>
    public event EventHandler<string>? MessageWritten;

    /// <summary>解析日志目录：显式参数 &gt; SHOTTOOL_LOG_DIR 环境变量 &gt; %LOCALAPPDATA%\ShotTool\logs。</summary>
    public static string ResolveDirectory(string? explicitDirectory = null)
    {
        if (!string.IsNullOrWhiteSpace(explicitDirectory))
        {
            return EnsureDirectory(explicitDirectory);
        }

        string? fromEnv = Environment.GetEnvironmentVariable("SHOTTOOL_LOG_DIR");
        if (!string.IsNullOrWhiteSpace(fromEnv))
        {
            return EnsureDirectory(fromEnv);
        }

        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localAppData))
        {
            localAppData = Path.GetTempPath();
        }

        return EnsureDirectory(Path.Combine(localAppData, "ShotTool", "logs"));
    }

    private static string EnsureDirectory(string path)
    {
        try
        {
            System.IO.Directory.CreateDirectory(path);
        }
        catch (Exception)
        {
            // 目录不可建（权限/只读盘）：退到临时目录，保证不抛
            path = Path.Combine(Path.GetTempPath(), "ShotTool-logs");
            try
            {
                System.IO.Directory.CreateDirectory(path);
            }
            catch (Exception)
            {
                // 仍然失败就只能在内存里记（Write 会静默跳过落盘）
            }
        }

        return path;
    }

    public void Info(string message) => Write("info", message);

    public void Warn(string message) => Write("warn", message);

    public void Error(string message, Exception? error = null)
        => Write("error", error is null ? message : $"{message} :: {error}");

    private void Write(string level, string message)
    {
        if (_disposed)
        {
            return;
        }

        string line = $"{DateTime.Now:HH:mm:ss.fff} [{level,-5}] {message}";

        lock (_gate)
        {
            Debug.WriteLine($"[ShotTool][{level}] {message}");

            try
            {
                _writer ??= CreateWriter();
                _writer?.WriteLine(line);
                _writer?.Flush();
            }
            catch (Exception)
            {
                // 落盘失败不影响主流程；关掉 writer 避免每行都重试
                _writer?.Dispose();
                _writer = null;
            }
        }

        try
        {
            MessageWritten?.Invoke(this, line);
        }
        catch (Exception)
        {
            // 订阅者异常同样不外溢
        }
    }

    private StreamWriter? CreateWriter()
    {
        try
        {
            var stream = new FileStream(
                _filePath,
                FileMode.Append,
                FileAccess.Write,
                FileShare.ReadWrite | FileShare.Delete);
            return new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
            {
                AutoFlush = true,
            };
        }
        catch (Exception)
        {
            return null;
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
            _writer?.Dispose();
            _writer = null;
        }
    }
}
