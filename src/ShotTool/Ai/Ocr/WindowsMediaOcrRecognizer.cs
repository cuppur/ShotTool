using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using ShotTool.Core;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;

// ★ 必须用别名：ShotTool.Core 与 Windows.Media.Ocr 都有 OcrResult / OcrLine / OcrWord，
//   不加别名会 CS0104 二义（Core 的是项目契约，WinRT 的是引擎返回类型）。
using CoreOcrResult = ShotTool.Core.OcrResult;
using CoreOcrLine = ShotTool.Core.OcrLine;
using CoreOcrWord = ShotTool.Core.OcrWord;
using WinOcrResult = Windows.Media.Ocr.OcrResult;

namespace ShotTool.Ai.Ocr;

// ============================================================================
//  WindowsMediaOcrRecognizer.cs —— 默认 OCR 后端（Windows.Media.Ocr，零体积）
//
//  为什么选它（ADR-006）：系统内置，不打包任何模型，本机已装 zh-Hans-CN 语言包，
//  用户零下载。实测 1920×1080 中位 84 ms、3840×2160 中位 126 ms，首次含引擎初始化
//  也只要 82–126 ms —— 性能充裕到不需要复杂的生命周期管理。
//
//  ★ 三个实测坑，改动前必读（证据见 docs/evidence/ocr-probe.md）：
//
//   坑 1：中文识别是**逐字分词**的。OcrResult.Text 实测为 "网 络 连 接 失 败 ，
//       请 稍 后 重 试"，每个汉字一个 word，标点单独成 word。
//       直接显示很难看，直接喂翻译会切碎语义 → 必须走 OcrTextPostProcessor 还原。
//       注意：**不得**把 Words 直接拼给翻译后端。
//
//   坑 2：SoftwareBitmap 只接受 Bgra8，**Gray8 会抛 ArgumentException**。
//       alpha 模式（Premultiplied / Straight / Ignore）三者识别结果完全一致，
//       这里选 Ignore：截图完全不透明，alpha 无信息，跳过 alpha 运算最快。
//
//   坑 3：.NET 5+ 已移除 WindowsRuntime 的 AsBuffer()，
//       Bitmap → SoftwareBitmap 必须走 DataWriter 写进 InMemoryRandomAccessStream。
//
//  能力边界（如实告知用户，不粉饰）：小字号下中文有形近字误识（实测 桌→卓）、
//  英文长词会被切断并插入假标点（实测 conver 、 sation）。这是引擎局限，
//  不是用法错误。结果面板必须可编辑；追求精度时用户可切到 PaddleOCR 后端。
// ============================================================================

/// <summary>Windows 系统内置 OCR 引擎后端。</summary>
public sealed class WindowsMediaOcrRecognizer : ITextRecognizer, IDisposable
{
    public const string RecognizerId = "windows.media.ocr";

    private readonly IAppLog? _log;
    private readonly object _gate = new();

    /// <summary>
    /// 引擎缓存。实测初始化只要几十毫秒，缓存不是必需，但能省掉每次的
    /// `TryCreateFromLanguage` 与内部字典构建，且避免频繁 GC。
    /// </summary>
    private OcrEngine? _cachedEngine;
    private string? _cachedLanguageTag;
    private bool _disposed;

    public WindowsMediaOcrRecognizer(IAppLog? log = null)
    {
        _log = log;
    }

    /// <inheritdoc />
    public string Id => RecognizerId;

    /// <inheritdoc />
    public string DisplayName => "Windows 系统 OCR（内置，零下载）";

    /// <summary>
    /// 是否可用：至少有一个 OCR 语言包。
    /// 这是 `IsAvailable` 的唯一判据 —— 没有语言包时 Editor 应隐藏按钮而不是报错。
    /// </summary>
    public bool IsAvailable
    {
        get
        {
            try
            {
                return OcrEngine.AvailableRecognizerLanguages.Count > 0;
            }
            catch (Exception ex)
            {
                _log?.Warn($"[OCR] 查询语言包失败：{ex.GetType().Name}: {ex.Message}");
                return false;
            }
        }
    }

    /// <summary>本机已安装的 OCR 语言标签（设置页展示 / 诊断用）。</summary>
    public IReadOnlyList<string> AvailableLanguageTags
    {
        get
        {
            try
            {
                return OcrEngine.AvailableRecognizerLanguages.Select(l => l.LanguageTag).ToArray();
            }
            catch
            {
                return Array.Empty<string>();
            }
        }
    }

    /// <summary>
    /// 引擎能处理的最大边长（实测 10000）。超过时必须先缩放，
    /// 否则 `RecognizeAsync` 会抛异常。多屏拼接的虚拟桌面可能触发。
    /// </summary>
    public static uint MaxImageDimension
    {
        get
        {
            try
            {
                return OcrEngine.MaxImageDimension;
            }
            catch
            {
                return 10000;
            }
        }
    }

    /// <inheritdoc />
    public async Task<CoreOcrResult> RecognizeAsync(Bitmap image, OcrOptions options,
                                                   CancellationToken cancellationToken = default)
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(WindowsMediaOcrRecognizer));
        }

        ArgumentNullException.ThrowIfNull(image);
        options ??= new OcrOptions();

        var totalSw = Stopwatch.StartNew();

        if (!IsAvailable)
        {
            throw new OcrUnavailableException(
                "系统未安装任何 OCR 语言包，无法识别文字。",
                "安装中文语言包（任一方式）：\n" +
                "  1) 设置 → 时间和语言 → 语言 → 添加语言 → 中文(简体，中国) → 勾选「光学字符识别」\n" +
                "  2) PowerShell（管理员）：\n" +
                "     Add-WindowsCapability -Online -Name Language.OCR~~~zh-CN~0.0.1.0\n" +
                "  3) 改用 PaddleOCR 后端（设置 → AI → OCR 后端 → paddle，需自行放置模型）");
        }

        var engine = GetOrCreateEngine(options.LanguageTag, out var effectiveTag);

        // 超限时按比例缩放（保持可用，识别后把坐标还原）
        using var prepared = PrepareBitmap(image, out var scale);

        var softwareBitmap = await ToSoftwareBitmapAsync(prepared, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        WinOcrResult raw;
        try
        {
            raw = await engine.RecognizeAsync(softwareBitmap).AsTask(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            totalSw.Stop();
            _log?.Error($"[OCR] 识别失败（语言={effectiveTag}，{prepared.Width}×{prepared.Height}）", ex);
            throw new InvalidOperationException(
                $"OCR 识别失败（{ex.GetType().Name}）：{ex.Message}。" +
                $"图像 {prepared.Width}×{prepared.Height}，引擎上限 {MaxImageDimension}。", ex);
        }

        var lines = ConvertLines(raw, scale);
        var plainText = OcrTextPostProcessor.BuildPlainText(
            lines.Select(l => l.Text), options.PreserveLayout, options.SingleLine);

        totalSw.Stop();
        _log?.Info($"[OCR] {effectiveTag} 识别完成：{lines.Count} 行 / {plainText.Length} 字，" +
                   $"{totalSw.ElapsedMilliseconds} ms，源图 {image.Width}×{image.Height}" +
                   (scale < 1.0 ? $"，已缩放 {scale:P0}" : string.Empty));

        return new CoreOcrResult(RecognizerId, lines, plainText, totalSw.Elapsed);
    }

    /// <summary>
    /// 取得（或缓存）引擎。语言标签匹配策略：
    /// 先精确匹配，再按主语言（zh-Hans → 找 zh-*）回落，最后用用户配置语言。
    /// </summary>
    private OcrEngine GetOrCreateEngine(string? requestedTag, out string effectiveTag)
    {
        var tag = string.IsNullOrWhiteSpace(requestedTag) ? "zh-Hans-CN" : requestedTag.Trim();

        lock (_gate)
        {
            if (_cachedEngine is not null && string.Equals(_cachedLanguageTag, tag, StringComparison.OrdinalIgnoreCase))
            {
                effectiveTag = tag;
                return _cachedEngine;
            }

            var resolved = ResolveLanguageTag(tag);
            var engine = OcrEngine.TryCreateFromLanguage(new Language(resolved));
            if (engine is null)
            {
                // 回落到用户配置语言的引擎
                engine = OcrEngine.TryCreateFromUserProfileLanguages();
                if (engine is null)
                {
                    throw new OcrUnavailableException(
                        $"无法创建 OCR 引擎（请求语言 {resolved}）。",
                        $"已安装的 OCR 语言：{string.Join(", ", AvailableLanguageTags)}。\n" +
                        "请在设置中改用已安装的语言标签，或安装对应语言包。");
                }

                resolved = engine.RecognizerLanguage.LanguageTag;
            }

            _cachedEngine = engine;
            _cachedLanguageTag = tag;
            effectiveTag = resolved;
            _log?.Info($"[OCR] 引擎就绪：请求 {tag} → 实际 {resolved}");
            return engine;
        }
    }

    /// <summary>把请求的语言标签对齐到本机真实可用的标签。</summary>
    private string ResolveLanguageTag(string requested)
    {
        var available = AvailableLanguageTags;
        if (available.Count == 0)
        {
            return requested;
        }

        // 精确命中
        var exact = available.FirstOrDefault(t => string.Equals(t, requested, StringComparison.OrdinalIgnoreCase));
        if (exact is not null)
        {
            return exact;
        }

        // 主语言匹配：zh-Hans → zh-Hans-CN 之类
        var primary = requested.Split('-')[0];
        var samePrimary = available.FirstOrDefault(t =>
            t.Split('-')[0].Equals(primary, StringComparison.OrdinalIgnoreCase));
        if (samePrimary is not null)
        {
            _log?.Warn($"[OCR] 语言 {requested} 未安装，改用同语系的 {samePrimary}");
            return samePrimary;
        }

        _log?.Warn($"[OCR] 语言 {requested} 不可用，改用 {available[0]}（识别精度可能下降）");
        return available[0];
    }

    /// <summary>
    /// 超过引擎上限时等比缩放（长边压到上限内）。返回的 Bitmap 所有权归调用方
    /// （未缩放时是原图的浅复制，调用方 using 即可安全释放）。
    /// </summary>
    private Bitmap PrepareBitmap(Bitmap source, out double scale)
    {
        var max = MaxImageDimension;
        var w = source.Width;
        var h = source.Height;

        if (w <= max && h <= max)
        {
            scale = 1.0;
            return new Bitmap(source);   // 浅复制，保证调用方 Dispose 不影响原图
        }

        scale = Math.Min((double)max / w, (double)max / h);
        var tw = Math.Max(1, (int)(w * scale));
        var th = Math.Max(1, (int)(h * scale));

        _log?.Warn($"[OCR] 图像 {w}×{h} 超引擎上限 {max}，缩放到 {tw}×{th}（坐标将按比例还原）");

        var resized = new Bitmap(tw, th, PixelFormat.Format32bppPArgb);
        using var g = Graphics.FromImage(resized);
        g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
        g.DrawImage(source, 0, 0, tw, th);
        return resized;
    }

    /// <summary>把 WinRT 结果转成 Core 契约类型，并把缩放坐标还原到源图坐标系。</summary>
    private static IReadOnlyList<CoreOcrLine> ConvertLines(WinOcrResult raw, double scale)
    {
        var lines = new List<CoreOcrLine>(raw.Lines.Count);
        var inv = scale > 0 ? 1.0 / scale : 1.0;

        foreach (var line in raw.Lines)
        {
            var words = new List<CoreOcrWord>(line.Words.Count);
            double minX = double.MaxValue, minY = double.MaxValue;
            double maxX = double.MinValue, maxY = double.MinValue;

            foreach (var word in line.Words)
            {
                var r = word.BoundingRect;
                var rect = new PhysRect(
                    (int)Math.Round(r.X * inv),
                    (int)Math.Round(r.Y * inv),
                    Math.Max(1, (int)Math.Round(r.Width * inv)),
                    Math.Max(1, (int)Math.Round(r.Height * inv)));

                words.Add(new CoreOcrWord(word.Text, rect));

                minX = Math.Min(minX, rect.X);
                minY = Math.Min(minY, rect.Y);
                maxX = Math.Max(maxX, rect.Right);
                maxY = Math.Max(maxY, rect.Bottom);
            }

            var bounds = words.Count == 0
                ? PhysRect.Empty
                : new PhysRect(
                    (int)Math.Round(minX),
                    (int)Math.Round(minY),
                    Math.Max(1, (int)Math.Round(maxX - minX)),
                    Math.Max(1, (int)Math.Round(maxY - minY)));

            lines.Add(new CoreOcrLine(line.Text, bounds, words));
        }

        return lines;
    }

    /// <summary>
    /// Bitmap → SoftwareBitmap。固定 Bgra8 + Ignore：
    /// 实测 Gray8 会抛 ArgumentException（不支持），三种 alpha 模式识别结果一致。
    /// </summary>
    private static async Task<SoftwareBitmap> ToSoftwareBitmapAsync(Bitmap bmp, CancellationToken cancellationToken)
    {
        // 注意：不能省掉 PNG 往返改用 LockBits 直接拷 —— 先跑通再优化，
        // 实测 4K 端到端仅 126 ms，当前没有优化必要。
        using var ms = new MemoryStream();
        bmp.Save(ms, ImageFormat.Png);
        var png = ms.ToArray();
        cancellationToken.ThrowIfCancellationRequested();

        using var ras = new InMemoryRandomAccessStream();
        using (var writer = new DataWriter(ras))
        {
            writer.WriteBytes(png);
            await writer.StoreAsync().AsTask(cancellationToken).ConfigureAwait(false);
            await writer.FlushAsync().AsTask(cancellationToken).ConfigureAwait(false);
            writer.DetachStream();   // 必须 detach，否则 ras 释放时 writer 仍持有
        }

        ras.Seek(0);
        var decoder = await BitmapDecoder.CreateAsync(ras).AsTask(cancellationToken).ConfigureAwait(false);
        return await decoder.GetSoftwareBitmapAsync(
                BitmapPixelFormat.Bgra8,
                BitmapAlphaMode.Ignore)
            .AsTask(cancellationToken)
            .ConfigureAwait(false);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        lock (_gate)
        {
            // OcrEngine 不是 IDisposable，置空即可让 GC 回收
            _cachedEngine = null;
            _cachedLanguageTag = null;
        }
    }
}

/// <summary>
/// OCR 引擎不可用（缺语言包 / 引擎创建失败）。
/// <see cref="InstallGuide"/> 可直接展示给用户，说明怎么修。
/// </summary>
public sealed class OcrUnavailableException : Exception
{
    public OcrUnavailableException(string message, string installGuide) : base(message)
    {
        InstallGuide = installGuide;
    }

    public string InstallGuide { get; }
}
