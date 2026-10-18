using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using ShotTool.Core;

namespace ShotTool.Ai.Ocr;

// ============================================================================
//  PaddleOcrRecognizer.cs —— PaddleOCR PP-OCRv4 后端（模型内嵌，可选后端）
//
//  定位（captain 口径）：**深色 / 代码 / 数字场景的精度兜底**。
//  t13 实测：白底 Windows 92.6% vs Paddle 100%（+7.4pt）；
//           深色场景 Windows 79.8% vs Paddle 100%（**+20.2pt**，其中中文 75%→100%）。
//
//  ★ t14 实测的硬限制（决定了本类的"质量门控"，务必不要删）：
//    简化 det 后处理（8-邻域连通域 + 轴对齐矩形 + 近似 unclip）在**倾斜文本**上
//    会**硬出错结果** —— 旋转 15°/30° 时漏检 29%/35%，准确率掉到 12%/8%，
//    甚至低于 Windows OCR 的 32%。
//    根因是"轴对齐 bbox 把斜排文字的邻行内容也裁进来"，不是 unclip 缺失。
//    因此本类**不做 minAreaRect 改造**（captain 裁定路线 C），而是：
//    识别完**评估质量**，可疑就给 UI 明确提示"换系统 OCR"，绝不假装识别成功。
//
//  ★ 与 WindowsMediaOcrRecognizer 的两处差异，改代码时别照抄错：
//    1) 来源是**内嵌资源**（PaddleEmbeddedResources），不是外部目录；
//    2) det 后处理是简化版，所以必须有质量门控（Windows 那边由引擎自己保证）。
//
//  依赖：Microsoft.ML.OnnxRuntime（主工程已引用 1.31.0）。
// ============================================================================

/// <summary>识别结果的可信度分级。用于让 UI 决定"直接展示"还是"提示换后端"。</summary>
public enum OcrQuality
{
    /// <summary>正常，可直接展示。</summary>
    Good = 0,

    /// <summary>可疑（可能漏行/串行），建议提示用户复核或换后端。</summary>
    Suspect = 1,

    /// <summary>明显不可用（倾斜/复杂排版），应提示切换系统 OCR，不要展示为成功。</summary>
    Insufficient = 2,
}

/// <summary>
/// PaddleOCR 一次识别的质量诊断。由 <see cref="PaddleOcrRecognizer.LastDiagnostics"/> 暴露，
/// UI 在拿到 <see cref="OcrResult"/> 后读取它来决定是否提示用户。
///
/// 为什么不用异常表达"质量不足"：文字可能**部分可用**，直接丢弃比给带警告的结果更糟。
/// </summary>
public sealed class PaddleOcrDiagnostics
{
    public OcrQuality Quality { get; init; } = OcrQuality.Good;

    /// <summary>判定原因的简短说明（中文，可直接展示）。</summary>
    public string? Reason { get; init; }

    /// <summary>给用户的行动建议（中文，可直接展示）。</summary>
    public string? Suggestion { get; init; }

    /// <summary>det 检出的文本框数。</summary>
    public int BoxCount { get; init; }

    /// <summary>按水平投影估算的文本行数（用于判断漏检/过分割）。</summary>
    public int EstimatedLines { get; init; }

    /// <summary>各框 CTC 置信度均值（0..1）。</summary>
    public double AverageScore { get; init; }

    /// <summary>框宽/高比的中位数（正常横排文本应显著 &gt; 1）。</summary>
    public double MedianAspectRatio { get; init; }

    public double DetMs { get; init; }

    public double RecMs { get; init; }

    public override string ToString()
        => $"{Quality}（框 {BoxCount}/估行 {EstimatedLines}，置信 {AverageScore:F2}，" +
           $"宽高比 {MedianAspectRatio:F1}）{(Reason is null ? "" : "：" + Reason)}";
}

/// <summary>
/// PaddleOCR 后端。模型内嵌在 exe 中，无需用户下载。
///
/// 线程模型：<see cref="RecognizeAsync"/> 可并发调用，内部用信号量串行化
/// （ONNX session 非线程安全）。
///
/// 懒加载：首次识别才建 session（约 330 ms）；<see cref="Unload"/> 可释放。
/// 与 NLLB 不同，这里**不做空闲自动卸载** —— 15 MB 模型 + 数百 MB 峰值远低于
/// 翻译的 1.6 GB，卸载收益不足以抵消重建成本。
/// </summary>
public sealed class PaddleOcrRecognizer : ITextRecognizer, IDisposable
{
    public const string RecognizerId = "paddle.onnx";

    // ---- det 参数（RapidOCR / PaddleOCR 默认值）----
    private const int DetLimitSideLen = 960;
    private const float DetThresh = 0.3f;
    private const float DetBoxThresh = 0.6f;
    private const float DetUnclipRatio = 1.5f;
    private const int DetMinSize = 3;

    // ---- rec 参数 ----
    private const int RecHeight = 48;
    private const int RecMinWidth = 320;
    private const int RecMaxWidth = 4000;

    private static readonly float[] DetMean = { 0.485f, 0.456f, 0.406f };
    private static readonly float[] DetStd = { 0.229f, 0.224f, 0.225f };

    private readonly IAppLog? _log;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private InferenceSession? _det;
    private InferenceSession? _rec;
    private string[]? _dict;
    private string _detInput = "x";
    private string _detOutput = "sigmoid_0.tmp_0";
    private string _recInput = "x";
    private string _recOutput = "softmax_11.tmp_0";
    private bool _disposed;

    public PaddleOcrRecognizer(IAppLog? log = null)
    {
        _log = log;
    }

    /// <inheritdoc />
    public string Id => RecognizerId;

    /// <inheritdoc />
    public string DisplayName => "PaddleOCR（内嵌 PP-OCRv4，深色/代码更准）";

    /// <summary>内嵌资源是否齐备。纯探测，不加载 ONNX。</summary>
    public bool IsAvailable => !_disposed && PaddleEmbeddedResources.IsAvailable;

    /// <summary>session 是否已加载（诊断用）。</summary>
    public bool IsLoaded => _det is not null && _rec is not null;

    /// <summary>最近一次识别的质量诊断。UI 应在每次识别后读取。</summary>
    public PaddleOcrDiagnostics? LastDiagnostics { get; private set; }

    /// <inheritdoc />
    public async Task<OcrResult> RecognizeAsync(Bitmap image, OcrOptions options,
                                               CancellationToken cancellationToken = default)
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(PaddleOcrRecognizer));
        }

        ArgumentNullException.ThrowIfNull(image);
        options ??= new OcrOptions();

        if (!IsAvailable)
        {
            throw new OcrUnavailableException(
                "PaddleOCR 内嵌模型不可用（随包资源缺失或损坏）。",
                PaddleEmbeddedResources.DescribeMissing());
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(true);
        try
        {
            EnsureLoaded();

            // det/rec 是纯 CPU 同步计算，放线程池避免卡 UI
            var work = await Task.Run(
                () => RecognizeCore(image, options, cancellationToken),
                cancellationToken).ConfigureAwait(true);

            LastDiagnostics = work.Diagnostics;

            if (work.Diagnostics.Quality == OcrQuality.Insufficient)
            {
                _log?.Warn($"[OCR-Paddle] 质量不足：{work.Diagnostics}");
            }

            return work.Result;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>核心识别：det → 框 → rec → CTC → 质量评估。</summary>
    private (OcrResult Result, PaddleOcrDiagnostics Diagnostics) RecognizeCore(
        Bitmap source, OcrOptions options, CancellationToken cancellationToken)
    {
        var totalSw = Stopwatch.StartNew();

        // ---- det ----
        var detSw = Stopwatch.StartNew();
        var boxes = RunDet(source, cancellationToken);
        detSw.Stop();
        cancellationToken.ThrowIfCancellationRequested();

        // ---- rec ----
        var recSw = Stopwatch.StartNew();
        var raw = new List<(string Text, Rectangle Box, double Score)>(boxes.Count);
        foreach (var box in boxes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (text, score) = RunRec(source, box);
            if (!string.IsNullOrWhiteSpace(text))
            {
                raw.Add((text, box, score));
            }
        }
        recSw.Stop();

        // 阅读顺序：先按行带（y 量化）再按 x
        var ordered = raw
            .OrderBy(r => r.Box.Top / Math.Max(1, r.Box.Height))
            .ThenBy(r => r.Box.Left)
            .ToList();

        // ---- 组装 Core 契约类型 ----
        var lines = new List<OcrLine>(ordered.Count);
        foreach (var (text, box, _) in ordered)
        {
            var bounds = new PhysRect(box.X, box.Y, box.Width, box.Height);
            var words = new[] { new OcrWord(OcrTextPostProcessor.CollapseCjkSpaces(text), bounds) };
            lines.Add(new OcrLine(OcrTextPostProcessor.CollapseCjkSpaces(text), bounds, words));
        }

        var plainText = OcrTextPostProcessor.BuildPlainText(
            lines.Select(l => l.Text), options.PreserveLayout, options.SingleLine);

        totalSw.Stop();
        var result = new OcrResult(RecognizerId, lines, plainText, totalSw.Elapsed);

        // ---- 质量门控（t14 的核心教训）----
        var diagnostics = Evaluate(raw, source, detSw.Elapsed.TotalMilliseconds,
                                   recSw.Elapsed.TotalMilliseconds);

        _log?.Info($"[OCR-Paddle] {lines.Count} 行 / {plainText.Length} 字，" +
                   $"{totalSw.ElapsedMilliseconds} ms，质量={diagnostics.Quality}");

        return (result, diagnostics);
    }

    // ==================== 质量门控 ====================

    /// <summary>
    /// 评估一次识别是否可信。三条信号：
    ///   1) 漏检：框数明显少于水平投影估算的行数（倾斜时轴对齐框会合并/丢弃邻行）；
    ///   2) 过分割：框数远多于估算行数；
    ///   3) 形态异常：框宽/高比中位数接近或小于 1（正常横排文本行应显著更宽）。
    /// 任一条严重 → Insufficient；两条轻微 → Suspect。
    ///
    /// 这是**启发式**，不是精确判定；目的是"可疑时别假装成功"，不是做学术分类。
    /// </summary>
    private static PaddleOcrDiagnostics Evaluate(
        List<(string Text, Rectangle Box, double Score)> items, Bitmap source, double detMs, double recMs)
    {
        var estimated = EstimateLineCount(source);
        var boxCount = items.Count;
        var avgScore = items.Count > 0 ? items.Average(i => i.Score) : 0;

        var ratios = items
            .Where(i => i.Box.Height > 0)
            .Select(i => (double)i.Box.Width / i.Box.Height)
            .OrderBy(x => x)
            .ToList();
        var medianRatio = ratios.Count > 0 ? ratios[ratios.Count / 2] : 0;

        // 无框：区分"图里真没字"与"有字没检出"
        if (boxCount == 0)
        {
            var quality = estimated <= 1 ? OcrQuality.Good : OcrQuality.Insufficient;
            return new PaddleOcrDiagnostics
            {
                Quality = quality,
                Reason = estimated <= 1 ? "图像中未检测到文字" : $"估算有约 {estimated} 行文字，但一个框都没检出",
                Suggestion = estimated <= 1 ? null : "文字可能过小、倾斜或对比度低。建议改用系统 OCR 后端。",
                BoxCount = 0,
                EstimatedLines = estimated,
                AverageScore = 0,
                MedianAspectRatio = 0,
                DetMs = detMs,
                RecMs = recMs,
            };
        }

        var recall = estimated > 0 ? (double)boxCount / estimated : 1.0;

        var severeMiss = estimated >= 3 && recall < 0.6;          // 漏检 >40%
        var severeSplit = estimated >= 3 && recall > 2.2;          // 过分割 >120%

        // 形态信号：正常横排文本框应显著"扁"，实测横排为 9.6–26.5，倾斜为 1.4–2.2。
        // 阈值取 3.0 且要求 boxCount>=2 —— 单框（短词如 "OK"）不判形态，避免误报。
        var suspiciousShape = boxCount >= 2 && medianRatio > 0 && medianRatio < 3.0;

        // 置信度信号：实测横排 0.98–1.00，倾斜 0.34–0.75。阈值 0.85 有足够分离度。
        var lowScore = avgScore > 0 && avgScore < 0.85;

        var severeCount = (severeMiss ? 1 : 0) + (severeSplit ? 1 : 0) +
                          (suspiciousShape ? 1 : 0) + (lowScore ? 1 : 0);

        if (severeMiss || (severeSplit && suspiciousShape))
        {
            return new PaddleOcrDiagnostics
            {
                Quality = OcrQuality.Insufficient,
                Reason = severeMiss
                    ? $"明显漏检（框 {boxCount} 个 / 估算 {estimated} 行）"
                    : "文本框形态异常且数量异常（疑似倾斜或竖排）",
                Suggestion = "倾斜/竖排/照片文字请改用「系统 OCR」后端（Windows OCR 在这类场景更稳）。",
                BoxCount = boxCount,
                EstimatedLines = estimated,
                AverageScore = avgScore,
                MedianAspectRatio = medianRatio,
                DetMs = detMs,
                RecMs = recMs,
            };
        }

        if (severeCount >= 2)
        {
            return new PaddleOcrDiagnostics
            {
                Quality = OcrQuality.Suspect,
                Reason = $"识别结果可能有串行或漏字（框 {boxCount} / 估行 {estimated}，" +
                         $"置信 {avgScore:F2}，宽高比 {medianRatio:F1}）",
                Suggestion = "建议核对文本；若原文为倾斜或复杂排版，改用「系统 OCR」后端。",
                BoxCount = boxCount,
                EstimatedLines = estimated,
                AverageScore = avgScore,
                MedianAspectRatio = medianRatio,
                DetMs = detMs,
                RecMs = recMs,
            };
        }

        return new PaddleOcrDiagnostics
        {
            Quality = OcrQuality.Good,
            BoxCount = boxCount,
            EstimatedLines = estimated,
            AverageScore = avgScore,
            MedianAspectRatio = medianRatio,
            DetMs = detMs,
            RecMs = recMs,
        };
    }

    /// <summary>
    /// 用水平投影估算文本行数：缩到小图 → 灰度 → 判定底色 → 统计"文字色"像素的行分布 →
    /// 数连续超阈值区间。暗底/亮底都能处理（先看平均亮度决定文字是暗还是亮）。
    /// </summary>
    private static int EstimateLineCount(Bitmap source)
    {
        const int TargetWidth = 600;
        var scale = Math.Min(1.0, (double)TargetWidth / Math.Max(1, source.Width));
        var w = Math.Max(1, (int)(source.Width * scale));
        var h = Math.Max(1, (int)(source.Height * scale));

        using var small = new Bitmap(w, h, PixelFormat.Format32bppPArgb);
        using (var g = Graphics.FromImage(small))
        {
            g.InterpolationMode = InterpolationMode.HighQualityBilinear;
            g.DrawImage(source, new Rectangle(0, 0, w, h));
        }

        // 取灰度 + 平均亮度
        var gray = new byte[w * h];
        double sum = 0;
        var data = small.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
        try
        {
            var stride = data.Stride;
            var buf = new byte[stride * h];
            Marshal.Copy(data.Scan0, buf, 0, buf.Length);
            for (var y = 0; y < h; y++)
            {
                for (var x = 0; x < w; x++)
                {
                    var off = y * stride + x * 4;
                    var lum = (buf[off] * 114 + buf[off + 1] * 587 + buf[off + 2] * 299) / 1000;
                    gray[y * w + x] = (byte)lum;
                    sum += lum;
                }
            }
        }
        finally
        {
            small.UnlockBits(data);
        }

        var mean = sum / (w * h);
        var darkBackground = mean < 128;
        // 亮底 → 文字是暗像素；暗底 → 文字是亮像素
        var threshold = darkBackground ? mean + 40 : mean - 40;

        var hasInk = new bool[h];
        for (var y = 0; y < h; y++)
        {
            var count = 0;
            for (var x = 0; x < w; x++)
            {
                var lum = gray[y * w + x];
                if (darkBackground ? lum > threshold : lum < threshold)
                {
                    count++;
                }
            }

            // 该行至少有 0.5% 的像素是文字色，才算"有内容"
            hasInk[y] = count > Math.Max(2, w * 0.005);
        }

        // 数连续有内容的行带
        var lines = 0;
        var inBand = false;
        for (var y = 0; y < h; y++)
        {
            if (hasInk[y] && !inBand) { lines++; inBand = true; }
            else if (!hasInk[y]) { inBand = false; }
        }

        return lines;
    }

    // ==================== det ====================

    private List<Rectangle> RunDet(Bitmap source, CancellationToken ct)
    {
        var (rw, rh) = FitDetSize(source.Width, source.Height);
        var scaleX = (double)rw / source.Width;
        var scaleY = (double)rh / source.Height;

        using var resized = new Bitmap(source, new Size(rw, rh));
        var tensor = new DenseTensor<float>(new[] { 1, 3, rh, rw });
        FillNormalized(resized, tensor, DetMean, DetStd);

        using var outputs = _det!.Run(new[] { NamedOnnxValue.CreateFromTensor(_detInput, tensor) });
        ct.ThrowIfCancellationRequested();
        var prob = outputs.First(o => o.Name == _detOutput).AsTensor<float>();

        var pw = prob.Dimensions[3];
        var ph = prob.Dimensions[2];

        var mask = new bool[ph * pw];
        for (var y = 0; y < ph; y++)
        {
            for (var x = 0; x < pw; x++)
            {
                mask[y * pw + x] = prob[0, 0, y, x] > DetThresh;
            }
        }

        var boxes = new List<Rectangle>();
        var visited = new bool[ph * pw];
        var stack = new Stack<(int X, int Y)>();

        for (var y = 0; y < ph; y++)
        {
            for (var x = 0; x < pw; x++)
            {
                var idx = y * pw + x;
                if (!mask[idx] || visited[idx]) continue;

                int minX = x, maxX = x, minY = y, maxY = y;
                double sumProb = 0;
                var count = 0;

                stack.Clear();
                stack.Push((x, y));
                visited[idx] = true;

                while (stack.Count > 0)
                {
                    var (cx, cy) = stack.Pop();
                    if (cx < minX) minX = cx;
                    if (cx > maxX) maxX = cx;
                    if (cy < minY) minY = cy;
                    if (cy > maxY) maxY = cy;
                    sumProb += prob[0, 0, cy, cx];
                    count++;

                    for (var dy = -1; dy <= 1; dy++)
                    {
                        for (var dx = -1; dx <= 1; dx++)
                        {
                            if (dx == 0 && dy == 0) continue;
                            var nx = cx + dx;
                            var ny = cy + dy;
                            if (nx < 0 || ny < 0 || nx >= pw || ny >= ph) continue;
                            var ni = ny * pw + nx;
                            if (!mask[ni] || visited[ni]) continue;
                            visited[ni] = true;
                            stack.Push((nx, ny));
                        }
                    }
                }

                var bw = maxX - minX + 1;
                var bh = maxY - minY + 1;
                if (bw < DetMinSize || bh < DetMinSize) continue;
                if (sumProb / count < DetBoxThresh) continue;

                var area = (double)bw * bh;
                var perimeter = 2.0 * (bw + bh);
                var distance = perimeter > 0 ? area * DetUnclipRatio / perimeter : 0;

                var rx = (int)Math.Max(0, (minX - distance) / scaleX);
                var ry = (int)Math.Max(0, (minY - distance) / scaleY);
                var rw2 = (int)Math.Min(source.Width - rx, (bw + 2 * distance) / scaleX);
                var rh2 = (int)Math.Min(source.Height - ry, (bh + 2 * distance) / scaleY);
                if (rw2 < 4 || rh2 < 4) continue;

                boxes.Add(new Rectangle(rx, ry, rw2, rh2));
            }
        }

        return boxes;
    }

    /// <summary>det 输入尺寸：长边压到 960 且对齐 32（limit_type=max，小图不放大）。</summary>
    private static (int W, int H) FitDetSize(int w, int h)
    {
        var ratio = (double)DetLimitSideLen / Math.Max(w, h);
        if (ratio > 1.0) ratio = 1.0;
        var rw = Math.Max(32, (int)(Math.Round(w * ratio / 32) * 32));
        var rh = Math.Max(32, (int)(Math.Round(h * ratio / 32) * 32));
        return (rw, rh);
    }

    // ==================== rec ====================

    private (string Text, double Score) RunRec(Bitmap source, Rectangle box)
    {
        box.Intersect(new Rectangle(0, 0, source.Width, source.Height));
        if (box.Width < 2 || box.Height < 2) return (string.Empty, 0);

        var targetW = Math.Clamp(
            (int)Math.Round(RecHeight * (double)box.Width / box.Height),
            RecMinWidth, RecMaxWidth);

        using var crop = new Bitmap(box.Width, box.Height, PixelFormat.Format32bppPArgb);
        using (var g = Graphics.FromImage(crop))
        {
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.DrawImage(source, new Rectangle(0, 0, box.Width, box.Height), box, GraphicsUnit.Pixel);
        }

        using var resized = new Bitmap(crop, new Size(targetW, RecHeight));
        var tensor = new DenseTensor<float>(new[] { 1, 3, RecHeight, targetW });
        FillNormalized(resized, tensor, new[] { 0.5f, 0.5f, 0.5f }, new[] { 0.5f, 0.5f, 0.5f });

        using var outputs = _rec!.Run(new[] { NamedOnnxValue.CreateFromTensor(_recInput, tensor) });
        var logits = outputs.First(o => o.Name == _recOutput).AsTensor<float>();

        var steps = logits.Dimensions[1];
        var classes = logits.Dimensions[2];
        var dict = _dict!;

        // CTC 贪心解码：0=blank，1..dict.Length=字典字，dict.Length+1=空格
        var sb = new System.Text.StringBuilder();
        var prev = -1;
        double scoreSum = 0;
        var scoreCount = 0;

        for (var t = 0; t < steps; t++)
        {
            var best = 0;
            var bestVal = float.NegativeInfinity;
            for (var c = 0; c < classes; c++)
            {
                var v = logits[0, t, c];
                if (v > bestVal) { bestVal = v; best = c; }
            }

            if (best != 0 && best != prev)
            {
                if (best <= dict.Length) sb.Append(dict[best - 1]);
                else sb.Append(' ');
                scoreSum += bestVal;
                scoreCount++;
            }

            prev = best;
        }

        return (sb.ToString(), scoreCount > 0 ? scoreSum / scoreCount : 0);
    }

    // ==================== 加载 / 释放 ====================

    /// <summary>首次使用时从内嵌资源建 session（约 330 ms）。</summary>
    private void EnsureLoaded()
    {
        if (_det is not null && _rec is not null && _dict is not null)
        {
            return;
        }

        var detBytes = PaddleEmbeddedResources.ReadAllBytes(PaddleEmbeddedResources.DetFileName);
        var recBytes = PaddleEmbeddedResources.ReadAllBytes(PaddleEmbeddedResources.RecFileName);
        if (detBytes is null || recBytes is null)
        {
            throw new OcrUnavailableException(
                "PaddleOCR 内嵌模型读取失败。", PaddleEmbeddedResources.DescribeMissing());
        }

        var keysBytes = PaddleEmbeddedResources.ReadAllBytes(PaddleEmbeddedResources.KeysFileName)
                        ?? throw new OcrUnavailableException(
                            "PaddleOCR 字典（keys.txt）读取失败。", PaddleEmbeddedResources.DescribeMissing());

        var sw = Stopwatch.StartNew();
        var opts = new SessionOptions { GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL };

        // InferenceSession(byte[]) 直接吃内存，不需要落临时文件
        _det = new InferenceSession(detBytes, opts);
        _rec = new InferenceSession(recBytes, opts);

        _detInput = _det.InputMetadata.Keys.First();
        _detOutput = _det.OutputMetadata.Keys.First();
        _recInput = _rec.InputMetadata.Keys.First();
        _recOutput = _rec.OutputMetadata.Keys.First();

        // 字典按行读（保留行内空格，只去 \r）
        _dict = System.Text.Encoding.UTF8.GetString(keysBytes)
            .Replace("\r\n", "\n")
            .Split('\n')
            .Select(l => l.TrimEnd('\r'))
            .ToArray();

        sw.Stop();
        _log?.Info($"[OCR-Paddle] 内嵌模型已加载 {sw.ElapsedMilliseconds} ms，" +
                   $"det 输入={_detInput} rec 输入={_recInput} 字典={_dict.Length} 字");
    }

    /// <summary>手动释放 session（设置页「卸载」/ 应用退出）。下次识别会自动重建。</summary>
    public void Unload()
    {
        _gate.Wait();
        try
        {
            ReleaseSessions();
        }
        finally
        {
            _gate.Release();
        }
    }

    private void ReleaseSessions()
    {
        _det?.Dispose();
        _rec?.Dispose();
        _det = null;
        _rec = null;
        _dict = null;
    }

    // ==================== 预处理 ====================

    /// <summary>
    /// Bitmap → NCHW float 张量。(pixel/255 - mean)/std。
    /// Bitmap 像素是 BGRA，与 PaddleOCR 的 BGR 输入天然一致，不做通道交换。
    /// </summary>
    private static void FillNormalized(Bitmap bmp, DenseTensor<float> tensor, float[] mean, float[] std)
    {
        var w = bmp.Width;
        var h = bmp.Height;
        var data = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
        try
        {
            var stride = data.Stride;
            var buffer = new byte[stride * h];
            Marshal.Copy(data.Scan0, buffer, 0, buffer.Length);

            for (var y = 0; y < h; y++)
            {
                for (var x = 0; x < w; x++)
                {
                    var off = y * stride + x * 4;
                    tensor[0, 0, y, x] = (buffer[off] / 255f - mean[0]) / std[0];         // B
                    tensor[0, 1, y, x] = (buffer[off + 1] / 255f - mean[1]) / std[1];     // G
                    tensor[0, 2, y, x] = (buffer[off + 2] / 255f - mean[2]) / std[2];     // R
                }
            }
        }
        finally
        {
            bmp.UnlockBits(data);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _gate.Wait();
        try
        {
            ReleaseSessions();
        }
        finally
        {
            _gate.Release();
            _gate.Dispose();
        }
    }
}
