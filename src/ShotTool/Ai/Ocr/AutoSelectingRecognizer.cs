using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using ShotTool.Core;

namespace ShotTool.Ai.Ocr;

// ============================================================================
//  AutoSelectingRecognizer.cs —— `OcrBackend = auto` 的实现：按选区亮度选一个引擎
//
//  ★ 语义（captain 定案，**不是**"先 Paddle 后回落"）：
//      低于亮度阈值 → PaddleOCR（深色场景它更准）
//      否则         → Windows OCR（更快、零加载）
//    **只跑一次，不做自动回落。**
//
//  为什么不做回落（三条理由都被实测支撑）：
//    1) 暗色场景双跑 = 335 ms(Paddle) + 84 ms(Windows) ≈ 419 ms，体验明显变慢；
//    2) 倾斜场景 Paddle 返回的是 Quality=Suspect **而不是失败**，回落条件根本不会触发，
//       等于没有兜底；
//    3) 按亮度只跑一次，延迟可预测（要么 84 ms，要么 335 ms）。
//
//  质量不足时不静默重试，而是把 <see cref="PaddleOcrRecognizer.LastDiagnostics"/> 的
//  原因/建议交给 UI 展示（"识别质量不足，建议切换系统 OCR"）—— 行为可预测优先。
//
//  ★ 阈值依据（13 张真实截图实测，见 docs/evidence/ocr-probe.md）：
//      暗色（IDE/终端/暗色微信）：Luma 0.159–0.185
//      亮色（白底网页/浅色微信/任务管理器）：Luma 0.876–0.902
//      两者之间没有真实样本，取 0.45 留足两侧余量。
//    中间地带（照片/游戏画面）倾向 Windows —— 它更快且无 Suspect 风险。
// ============================================================================

/// <summary>按选区亮度在 Windows OCR 与 PaddleOCR 之间自动选择。</summary>
public sealed class AutoSelectingRecognizer : ITextRecognizer, IDisposable
{
    public const string RecognizerId = "auto";

    /// <summary>
    /// 亮度阈值（0–1，相对 Luma）。低于此值判为深色 → 用 PaddleOCR。
    ///
    /// ★ 单一来源：引用 <c>Settings.AiOptions.AutoBrightnessThreshold</c>（0–255 刻度），
    ///   这里只做刻度换算，**不另定义一份**，避免两个阈值漂移。
    ///   实测建议值 115（0.45 × 255）：暗色组实测最高 47、亮色组最低 223，两侧余量充足。
    /// </summary>
    public static double DarkThreshold => ShotTool.Settings.AiOptions.AutoBrightnessThreshold / 255.0;

    /// <summary>亮度采样边长（先降到 64×64 再算，避免遍历 4K 全图）。</summary>
    private const int SampleSize = 64;

    private readonly WindowsMediaOcrRecognizer _windows;
    private readonly PaddleOcrRecognizer _paddle;
    private readonly IAppLog? _log;

    public AutoSelectingRecognizer(WindowsMediaOcrRecognizer windows, PaddleOcrRecognizer paddle, IAppLog? log = null)
    {
        _windows = windows ?? throw new ArgumentNullException(nameof(windows));
        _paddle = paddle ?? throw new ArgumentNullException(nameof(paddle));
        _log = log;
    }

    /// <inheritdoc />
    public string Id => RecognizerId;

    /// <inheritdoc />
    public string DisplayName => "自动（按画面明暗选引擎）";

    /// <summary>至少有一个后端可用就算可用（Paddle 内嵌资源缺失时仍能退回 Windows）。</summary>
    public bool IsAvailable => _windows.IsAvailable || _paddle.IsAvailable;

    /// <summary>上一次 auto 选择的引擎与依据。设置页用它显示「检测到深色背景（Luma 0.16）→ 使用 PaddleOCR」。</summary>
    public AutoSelectionResult? LastSelection { get; private set; }

    /// <inheritdoc />
    public async Task<OcrResult> RecognizeAsync(Bitmap image, OcrOptions options,
                                               CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(image);
        options ??= new OcrOptions();

        var luma = ComputeAverageLuma(image);
        var preferPaddle = luma < DarkThreshold && _paddle.IsAvailable;

        // 若 Paddle 不可用（内嵌资源异常），无论明暗都走 Windows —— 这是能力缺失，不是"回落"
        var usePaddle = preferPaddle;
        var backend = usePaddle ? "paddle" : "windows";

        LastSelection = new AutoSelectionResult
        {
            AverageLuma = luma,
            Threshold = DarkThreshold,
            IsDark = luma < DarkThreshold,
            ChosenBackend = backend,
            Reason = !_paddle.IsAvailable
                ? $"PaddleOCR 内嵌资源不可用，改用 Windows OCR（Luma {luma:F2}）"
                : usePaddle
                    ? $"检测到深色背景（Luma {luma:F2} < {DarkThreshold:F2}）→ 使用 PaddleOCR"
                    : $"检测到浅色背景（Luma {luma:F2} ≥ {DarkThreshold:F2}）→ 使用 Windows OCR",
        };

        _log?.Info($"[OCR-auto] Luma={luma:F3} → {backend}");

        var result = usePaddle
            ? await _paddle.RecognizeAsync(image, options, cancellationToken).ConfigureAwait(true)
            : await _windows.RecognizeAsync(image, options, cancellationToken).ConfigureAwait(true);

        // 若用 Paddle 且质量不足，把诊断挂到结果上供 UI 提示（**不重跑 Windows**）
        if (usePaddle && _paddle.LastDiagnostics is { Quality: not OcrQuality.Good } diag)
        {
            LastSelection = new AutoSelectionResult
            {
                AverageLuma = luma,
                Threshold = DarkThreshold,
                IsDark = true,
                ChosenBackend = backend,
                Reason = LastSelection!.Reason,
                QualityWarning = diag.Quality == OcrQuality.Insufficient
                    ? "识别质量不足，建议切换系统 OCR"
                    : "识别结果可能有误，建议核对",
                Suggestion = diag.Suggestion,
            };
            _log?.Warn($"[OCR-auto] Paddle 质量={diag.Quality}：{diag.Reason}");
        }

        return result;
    }

    /// <summary>
    /// 计算平均相对亮度（0–1）。先降到 64×64 再逐像素按 Luma 公式加权，
    /// 避免 4K 全图遍历（4K 全图约 830 万像素，降采样后只有 4096 个）。
    /// </summary>
    public static double ComputeAverageLuma(Bitmap source)
    {
        ArgumentNullException.ThrowIfNull(source);

        var w = Math.Min(SampleSize, Math.Max(1, source.Width));
        var h = Math.Min(SampleSize, Math.Max(1, source.Height));

        using var small = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(small))
        {
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBilinear;
            g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
            g.DrawImage(source, new Rectangle(0, 0, w, h));
        }

        // LockBits + 托管缓冲，比逐次 GetPixel 快两个数量级
        var data = small.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var stride = data.Stride;
            var buffer = new byte[stride * h];
            Marshal.Copy(data.Scan0, buffer, 0, buffer.Length);

            double sum = 0;
            for (var y = 0; y < h; y++)
            {
                for (var x = 0; x < w; x++)
                {
                    var off = y * stride + x * 4;
                    double b = buffer[off];
                    double g2 = buffer[off + 1];
                    double r = buffer[off + 2];
                    // Luma 相对亮度（ITU-R BT.709）
                    sum += (0.2126 * r + 0.7152 * g2 + 0.0722 * b) / 255.0;
                }
            }

            return sum / (w * h);
        }
        finally
        {
            small.UnlockBits(data);
        }
    }

    public void Dispose()
    {
        // 两个后端由外部（AiServiceRegistry / 工厂）拥有，这里不 Dispose，避免双重释放
    }
}

/// <summary>一次 auto 选择的结果与依据。设置页/日志直接展示，满足可解释性要求。</summary>
public sealed class AutoSelectionResult
{
    public double AverageLuma { get; init; }

    public double Threshold { get; init; }

    public bool IsDark { get; init; }

    /// <summary><c>"paddle"</c> 或 <c>"windows"</c>。</summary>
    public string ChosenBackend { get; init; } = string.Empty;

    /// <summary>中文理由，可直接展示（含 Luma 数值）。</summary>
    public string Reason { get; init; } = string.Empty;

    /// <summary>Paddle 质量不足时的提示（此时后端仍是 Paddle，没有自动重跑）。</summary>
    public string? QualityWarning { get; init; }

    public string? Suggestion { get; init; }

    public override string ToString() => Reason;
}
