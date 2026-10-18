// ============================================================================
//  Capture/GdiCaptureSource.cs —— 屏幕采集（ADR-002：GDI BitBlt + CAPTUREBLT）
//
//  单帧抓取整虚拟桌面。设计要点（每条都对应 ADR-002 §2.4 的正确性要点）：
//    1) **逐屏 BitBlt**：`GetDC(NULL)` 的坐标系是「主屏左上为原点、可为负」的物理像素，
//       但跨显示器（尤其不同 DPI / 负原点）不能指望一次 BitBlt 全部对齐 —— 按
//       EnumDisplayMonitors 的 rcMonitor 逐屏拷到对应偏移，覆盖率为 100% 时再返回。
//    2) **不缩放**：PMv2 下 BitBlt 的结果就是物理像素，本文件里没有任何 DPI 乘除。
//    3) **不抓光标**：BitBlt 本身不含光标；光标位置单独读给放大镜用（避免残影/拖尾）。
//    4) **DC 复用**：屏幕 DC（GetDC(NULL)）在实例生命周期内只取一次；每帧只新建位图
//       （因为位图所有权转移给 CapturedFrame，不能复用）。
//    5) **失败必须抛 CaptureException**（带 Win32 错误码），不做静默降级 ——
//       降级会掩盖真实故障（锁屏 / UAC 安全桌面 / RDP 断开），且用户看到的是"全黑截图"。
//
//  性能（3840×2160 单帧，工程预期 ~10–25 ms）：
//    BitBlt × N 屏（每屏一次 GDI 拷贝，驱动侧通常有硬件加速）；
//    实测数据在 RuntimeStats 里自报，托盘「状态」菜单可见（便于回填 docs/05-perf.md）。
// ============================================================================

using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using ShotTool.Core;
using ShotTool.Interop;

namespace ShotTool.Capture;

/// <summary>
/// GDI BitBlt 采集后端（P0 默认）。线程安全：内部串行化，可被任意线程调用。
/// </summary>
public sealed class GdiCaptureSource : ICaptureSource, IDisposable
{
    private readonly IAppLog? _log;
    private readonly object _gate = new();

    /// <summary>屏幕 DC（GetDC(NULL)），整个实例生命周期复用。</summary>
    private IntPtr _screenDc = IntPtr.Zero;

    private bool _disposed;

    // ---- 运行期统计（供托盘诊断 / 性能回填）----
    private int _captureCount;
    private long _lastElapsedMs;
    private long _maxElapsedMs;
    private long _totalElapsedMs;

    public GdiCaptureSource(IAppLog? log = null)
    {
        _log = log;
    }

    public CaptureBackend Backend => CaptureBackend.GdiBitBlt;

    /// <summary>抓屏次数。</summary>
    public int CaptureCount => Volatile.Read(ref _captureCount);

    /// <summary>上一次抓屏耗时（毫秒）。</summary>
    public long LastElapsedMs => Interlocked.Read(ref _lastElapsedMs);

    /// <summary>平均耗时（毫秒）。</summary>
    public long AverageElapsedMs
    {
        get
        {
            int count = CaptureCount;
            return count == 0 ? 0 : Interlocked.Read(ref _totalElapsedMs) / count;
        }
    }

    /// <summary>最大耗时（毫秒）。</summary>
    public long MaxElapsedMs => Interlocked.Read(ref _maxElapsedMs);

    /// <summary>GDI BitBlt 在 Windows 桌面上始终可用；无桌面会话时由 Capture 抛错。</summary>
    public bool IsAvailable() => !_disposed;

    /// <summary>
    /// 预热：启动时先取一次屏幕 DC，避免第一次 Alt+A 里包含首次 GDI 初始化开销
    /// （托盘装配完成后调用，失败只记日志，不影响后续）。
    /// </summary>
    public void Warm()
    {
        lock (_gate)
        {
            try
            {
                EnsureScreenDc();
            }
            catch (CaptureException ex)
            {
                _log?.Warn($"采集预热失败（不影响启动）：{ex.Message}");
            }
        }
    }

    public CapturedFrame CaptureVirtualDesktop(CaptureOptions options, CancellationToken cancellationToken = default)
    {
        options ??= CaptureOptions.Default;
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            long startedTicks = Stopwatch.GetTimestamp();

            // ---- 1) 几何：虚拟桌面 + 显示器清单（全部物理像素）----
            PhysRect bounds = MonitorService.GetVirtualDesktopBounds();
            if (bounds.IsEmpty)
            {
                throw new CaptureException(
                    "读取虚拟桌面尺寸失败（可能处于锁屏 / 无桌面会话 / 远程会话已断开）。",
                    Backend,
                    null);
            }

            IReadOnlyList<MonitorInfo> monitors = MonitorService.EnumerateMonitors(_log);
            if (monitors.Count == 0)
            {
                // 退化：把虚拟桌面当一个显示器（DPI 取主屏值），保证仍有位图可用
                uint dpi = MonitorService.GetPrimaryDpi();
                monitors = new[] { new MonitorInfo("\\.\\DISPLAY1", bounds, bounds, dpi, isPrimary: true) };
                _log?.Warn($"未枚举到显示器，按单屏 {bounds.Width}x{bounds.Height}@{dpi} 处理。");
            }

            cancellationToken.ThrowIfCancellationRequested();

            // ---- 2) 目标位图（32bppPArgb，与剪贴板/合成/输出路径一致）----
            Bitmap bitmap;
            try
            {
                bitmap = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppPArgb);
            }
            catch (Exception ex)
            {
                throw new CaptureException(
                    $"分配抓屏位图失败（{bounds.Width}x{bounds.Height}）。",
                    Backend,
                    null,
                    ex);
            }

            uint rop = NativeConstants.SRCCOPY
                | (options.IncludeLayeredWindows ? NativeConstants.CAPTUREBLT : 0u);

            bool anySuccess = false;
            bool anyFailure = false;
            int lastError = 0;

            try
            {
                IntPtr screenDc = EnsureScreenDc();

                using (Graphics graphics = Graphics.FromImage(bitmap))
                {
                    // 未覆盖区域先置黑：避免某些驱动上未初始化显存导致花屏
                    graphics.Clear(Color.Black);

                    IntPtr destDc = graphics.GetHdc();
                    try
                    {
                        foreach (MonitorInfo monitor in monitors)
                        {
                            cancellationToken.ThrowIfCancellationRequested();

                            // 逐屏区域夹进虚拟桌面（多屏可部分重叠/越界）
                            PhysRect part = monitor.Bounds.Intersect(bounds);
                            if (part.IsEmpty)
                            {
                                continue;
                            }

                            int destX = part.X - bounds.X;
                            int destY = part.Y - bounds.Y;

                            bool ok = NativeMethods.BitBlt(
                                destDc,
                                destX,
                                destY,
                                part.Width,
                                part.Height,
                                screenDc,
                                part.X,
                                part.Y,
                                rop);

                            if (ok)
                            {
                                anySuccess = true;
                            }
                            else
                            {
                                anyFailure = true;
                                lastError = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
                                _log?.Warn(
                                    $"BitBlt 失败：屏 {monitor.DeviceName} 区域 {part}，Win32={lastError}。");
                            }
                        }
                    }
                    finally
                    {
                        graphics.ReleaseHdc(destDc);
                    }
                }

                if (!anySuccess)
                {
                    throw new CaptureException(
                        $"BitBlt 全部失败（Win32={lastError}）。常见原因：锁屏 / UAC 安全桌面 / 远程会话断开。",
                        Backend,
                        lastError);
                }

                if (anyFailure)
                {
                    _log?.Warn("部分显示器抓屏失败，位图对应区域为黑色（其余区域有效）。");
                }

                // ---- 3) 光标位置（物理像素；给放大镜与取色用）----
                PhysPoint cursor = NativeMethods.GetCursorPos(out NativePoint nativeCursor)
                    ? new PhysPoint(nativeCursor.X, nativeCursor.Y)
                    : PhysPoint.Zero;

                var frame = new CapturedFrame(
                    bitmap,
                    bounds,
                    monitors,
                    cursor,
                    Backend,
                    DateTime.UtcNow);

                RecordElapsed(startedTicks, bounds, monitors.Count);
                return frame;
            }
            catch
            {
                // 失败路径必须释放位图（否则每失败一次泄漏 33 MB）
                bitmap.Dispose();
                throw;
            }
        }
    }

    /// <summary>取屏幕 DC（只取一次）。失败抛 CaptureException。</summary>
    private IntPtr EnsureScreenDc()
    {
        if (_screenDc != IntPtr.Zero)
        {
            return _screenDc;
        }

        _screenDc = NativeMethods.GetDC(IntPtr.Zero);
        if (_screenDc == IntPtr.Zero)
        {
            int error = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
            throw new CaptureException(
                $"GetDC(NULL) 失败（Win32={error}）。",
                Backend,
                error);
        }

        _log?.Info($"屏幕 DC 已建立（句柄 0x{_screenDc.ToInt64():X}），本实例复用。");
        return _screenDc;
    }

    private void RecordElapsed(long startedTicks, PhysRect bounds, int monitorCount)
    {
        long elapsedMs = (long)Stopwatch.GetElapsedTime(startedTicks).TotalMilliseconds;

        Interlocked.Increment(ref _captureCount);
        Interlocked.Exchange(ref _lastElapsedMs, elapsedMs);
        Interlocked.Add(ref _totalElapsedMs, elapsedMs);

        long max = Interlocked.Read(ref _maxElapsedMs);
        while (elapsedMs > max)
        {
            long previous = Interlocked.CompareExchange(ref _maxElapsedMs, elapsedMs, max);
            if (previous == max)
            {
                break;
            }

            max = previous;
        }

        int count = CaptureCount;
        // 抓屏是用户驱动的低频动作，但也不要每次都刷日志：前 3 次 + 之后每 10 次
        if (count <= 3 || count % 10 == 0)
        {
            _log?.Info(
                $"抓屏 #{count}：{bounds.Width}x{bounds.Height} / {monitorCount} 屏，耗时 {elapsedMs} ms" +
                $"（平均 {AverageElapsedMs} ms，峰值 {Interlocked.Read(ref _maxElapsedMs)} ms）。");
        }
    }

    /// <summary>诊断串（托盘「状态」菜单）。</summary>
    public string DescribeStats()
        => CaptureCount == 0
            ? "尚未抓屏"
            : $"{CaptureCount} 次，上次 {LastElapsedMs} ms，平均 {AverageElapsedMs} ms，峰值 {MaxElapsedMs} ms";

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            if (_screenDc != IntPtr.Zero)
            {
                NativeMethods.ReleaseDC(IntPtr.Zero, _screenDc);
                _screenDc = IntPtr.Zero;
            }
        }
    }
}
