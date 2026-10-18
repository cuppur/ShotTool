using System.Text.Json;
using ShotTool.Core;

namespace ShotTool.Settings;

/// <summary>
/// 翻译后端选择。
/// </summary>
public enum TranslationBackend
{
    /// <summary>关闭（默认）：不加载任何权重、不显示翻译入口。</summary>
    Off = 0,

    /// <summary>本地 NLLB-200-distilled-600M（int8 ONNX）。权重由用户自行获取，见 README「翻译」一节。</summary>
    Nllb = 1,

    /// <summary>本机 Ollama（HTTP 127.0.0.1:11434）。</summary>
    Ollama = 2,
}

/// <summary>
/// OCR 后端选择。
/// </summary>
public enum OcrBackend
{
    /// <summary>
    /// 自动（默认）：按选区平均亮度**只选一次**引擎 —— 暗色 UI（微信暗色 / IDE / 终端）
    /// 用 PaddleOCR，亮色用系统 OCR。不做"先 Paddle 再回落 Windows"（那会让暗色场景
    /// 跑两次引擎、延迟翻倍，且倾斜场景 Paddle 返回 Suspect 而非失败，根本不会触发回落）。
    /// </summary>
    Auto = 0,

    /// <summary>强制 Windows.Media.Ocr（系统自带、零体积；白底准、倾斜/照片文字表现更好）。</summary>
    Windows = 1,

    /// <summary>强制 PaddleOCR ONNX（横排深色最准、代码/十六进制友好；倾斜/照片建议改回 windows）。</summary>
    Paddle = 2,
}

/// <summary>
/// AI 能力的持久化设置（settings.json 的 <c>sections.ai</c> 段）。
///
/// 键名与 captain 约定一致：<c>TranslationBackend</c> / <c>NllbModelDirectory</c> / <c>OcrBackend</c>。
/// <para>
/// <b>容错契约</b>：任何未知取值（未知枚举名、数字越界、类型不符、坏 JSON、null）
/// 一律回退到默认值，<b>绝不抛异常</b>。配置文件是用户可手改的，不能因为手滑把程序打挂。
/// </para>
/// <para>
/// 读写都通过 <see cref="ISettingsStore"/> 的 section 机制，不改 <see cref="ShotToolOptions"/>
/// （Core 由 architect 独占）；dev-ai 只需要 <see cref="ReadFrom"/> / <see cref="WriteTo"/>。
/// </para>
/// </summary>
public sealed class AiOptions
{
    /// <summary>settings.json 内 sections 的键名。</summary>
    public const string SectionName = "ai";

    /// <summary>
    /// NLLB 模型目录默认值（原样保存，读取时再展开环境变量）。
    ///
    /// <para>
    /// 口径（captain 定死）：唯一根 = <c>%LOCALAPPDATA%\ShotTool\</c>。
    /// 不用 Roaming：1.07 GiB 的权重会被域漫游同步拖垮登录。
    /// </para>
    /// 权重不进 dist、不打包、不入库；用户自行放置后此目录存在即可用。
    /// </summary>
    public const string DefaultNllbModelDirectory = @"%LOCALAPPDATA%\ShotTool\models\nllb-200-distilled-600M-int8";

    /// <summary>翻译后端。默认 <see cref="TranslationBackend.Off"/>：不点翻译就不加载任何模型。</summary>
    public TranslationBackend TranslationBackend { get; set; } = TranslationBackend.Off;

    /// <summary>NLLB 模型目录（可含 <c>%LOCALAPPDATA%</c> 这类环境变量）。</summary>
    public string NllbModelDirectory { get; set; } = DefaultNllbModelDirectory;

    /// <summary>OCR 后端。默认 <see cref="OcrBackend.Auto"/>：按选区明暗自动选（见枚举注释）。</summary>
    public OcrBackend OcrBackend { get; set; } = OcrBackend.Auto;

    /// <summary>
    /// <see cref="OcrBackend.Auto"/> 的亮度阈值（0–255 的选区平均亮度）。
    /// 平均亮度低于该值判为「暗色」→ PaddleOCR；否则 → 系统 OCR。
    ///
    /// <para>
    /// 占位值，<b>待 dev-ai 实测确定</b>（t9 会给出微信暗色/IDE/终端/白底四类样本的实测阈值）。
    /// 确定后只改这一个常量；<see cref="OcrBackend.Auto"/> 的语义与持久化格式不变。
    /// </para>
    /// </summary>
    public const int AutoBrightnessThreshold = 120; // TODO(dev-ai): 用实测值替换

    /// <summary>展开环境变量后的模型目录（用于 <c>Directory.Exists</c> 判断）；无法展开时原样返回。</summary>
    public string ResolveNllbModelDirectory()
    {
        string raw = NllbModelDirectory;
        if (string.IsNullOrWhiteSpace(raw))
        {
            raw = DefaultNllbModelDirectory;
        }

        try
        {
            return Environment.ExpandEnvironmentVariables(raw);
        }
        catch
        {
            return raw;
        }
    }

    /// <summary>序列化为 JSON（用于 <see cref="ISettingsStore.SetSection"/>）。</summary>
    public string ToJson()
    {
        return JsonSerializer.Serialize(this, JsonSettingsStore.SharedJsonOptions);
    }

    /// <summary>
    /// 从 JSON 宽容解析：任何异常、类型不符、未知枚举值都回退默认，不抛异常。
    /// </summary>
    public static AiOptions Parse(string? json)
    {
        var options = new AiOptions();
        if (string.IsNullOrWhiteSpace(json))
        {
            return options;
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip,
            });
        }
        catch (JsonException)
        {
            return options;
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return options;
            }

            JsonElement root = document.RootElement;

            if (root.TryGetProperty(nameof(TranslationBackend), out JsonElement backendElement))
            {
                options.TranslationBackend = ParseTranslationBackend(backendElement);
            }

            if (root.TryGetProperty(nameof(NllbModelDirectory), out JsonElement dirElement))
            {
                options.NllbModelDirectory = ParseDirectory(dirElement);
            }

            if (root.TryGetProperty(nameof(OcrBackend), out JsonElement ocrElement))
            {
                options.OcrBackend = ParseOcrBackend(ocrElement);
            }
        }

        return options;
    }

    /// <summary>从设置仓库读取 <c>sections.ai</c>；缺失或损坏时返回默认值。</summary>
    public static AiOptions ReadFrom(ISettingsStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        return Parse(store.GetSection(SectionName));
    }

    /// <summary>写入 <c>sections.ai</c> 并立即落盘。</summary>
    public static void WriteTo(ISettingsStore store, AiOptions options)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(options);
        store.SetSection(SectionName, options.ToJson());
    }

    /// <summary>
    /// 确保配置里已有 ai 段（首次运行或旧配置文件升级用）。
    /// 已存在时不做任何事，避免覆盖用户手改的值。
    /// </summary>
    public static AiOptions EnsureSection(ISettingsStore store)
    {
        ArgumentNullException.ThrowIfNull(store);

        string? existing = store.GetSection(SectionName);
        if (existing is null)
        {
            AiOptions defaults = new();
            WriteTo(store, defaults);
            return defaults;
        }

        return Parse(existing);
    }

    // ---- 宽容解析辅助 ----

    private static TranslationBackend ParseTranslationBackend(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.String)
        {
            string? raw = element.GetString();
            if (!string.IsNullOrWhiteSpace(raw))
            {
                string text = raw.Trim();
                foreach (TranslationBackend candidate in Enum.GetValues<TranslationBackend>())
                {
                    if (string.Equals(candidate.ToString(), text, StringComparison.OrdinalIgnoreCase))
                    {
                        return candidate;
                    }
                }
            }

            return TranslationBackend.Off;
        }

        if (element.ValueKind == JsonValueKind.Number
            && element.TryGetInt32(out int number)
            && Enum.IsDefined(typeof(TranslationBackend), number))
        {
            return (TranslationBackend)number;
        }

        return TranslationBackend.Off;
    }

    private static OcrBackend ParseOcrBackend(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.String)
        {
            string? raw = element.GetString();
            if (!string.IsNullOrWhiteSpace(raw))
            {
                string text = raw.Trim();
                foreach (OcrBackend candidate in Enum.GetValues<OcrBackend>())
                {
                    if (string.Equals(candidate.ToString(), text, StringComparison.OrdinalIgnoreCase))
                    {
                        return candidate;
                    }
                }
            }

            return OcrBackend.Auto;
        }

        if (element.ValueKind == JsonValueKind.Number
            && element.TryGetInt32(out int number)
            && Enum.IsDefined(typeof(OcrBackend), number))
        {
            return (OcrBackend)number;
        }

        return OcrBackend.Auto;
    }

    private static string ParseDirectory(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.String)
        {
            return DefaultNllbModelDirectory;
        }

        string? raw = element.GetString();
        return string.IsNullOrWhiteSpace(raw) ? DefaultNllbModelDirectory : raw.Trim();
    }
}
