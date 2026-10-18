using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ShotTool.Core;

namespace ShotTool.Ai.Translation;

// ============================================================================
//  OllamaTranslationProvider.cs —— Ollama 本地大模型翻译后端（NLLB 的兜底方案）
//
//  为什么需要它：NLLB 的 ONNX 路径依赖用户自行下载 1.08 GiB 权重并放对目录，
//  存在「用户暂时没装」的常态。Ollama 只要本机跑起来就能用，作为兜底后端保证
//  「翻译」按钮在任何一种本地模型存在时都可用，而不是整体失灵。
//
//  依赖：本机运行的 Ollama（默认 http://127.0.0.1:11434）。不内置任何模型、不含密钥。
//  隐私：只向 127.0.0.1 发请求；不向任何远端服务发送截图文本。
// ============================================================================

/// <summary>Ollama 后端配置。由设置页写入 "ai" section。</summary>
public sealed class OllamaOptions
{
    public bool Enabled { get; set; }

    /// <summary>服务地址。默认本机。</summary>
    public string BaseUrl { get; set; } = "http://127.0.0.1:11434";

    /// <summary>模型名，例如 qwen2.5:7b-instruct / llama3.1:8b。</summary>
    public string Model { get; set; } = string.Empty;

    /// <summary>请求超时（秒）。本地大模型首 token 可能较慢。</summary>
    public int TimeoutSeconds { get; set; } = 120;
}

/// <summary>Ollama 翻译后端。</summary>
public sealed class OllamaTranslationProvider : ITranslationProvider, IDisposable
{
    public const string ProviderId = "ollama.local";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly HttpClient _http;
    private readonly IAppLog? _log;
    private OllamaOptions _options;

    public OllamaTranslationProvider(OllamaOptions? options = null, IAppLog? log = null)
    {
        _options = options ?? new OllamaOptions();
        _log = log;

        _http = new HttpClient
        {
            // 单次请求的超时在 CancellationToken 上控制，这里给一个宽松上限
            Timeout = TimeSpan.FromSeconds(Math.Max(10, _options.TimeoutSeconds) + 30),
        };
    }

    /// <inheritdoc />
    public string Id => ProviderId;

    /// <inheritdoc />
    public string DisplayName => string.IsNullOrWhiteSpace(_options.Model)
        ? "Ollama（本地大模型）"
        : $"Ollama / {_options.Model}";

    /// <summary>
    /// 是否「已配置」。这里只判断用户是否启用并填了模型名 —— 不去发网络请求，
    /// 因为属性会在 UI 绘制时被频繁读取。真实可用性由 <see cref="ProbeAsync"/> 或首次翻译验证。
    /// </summary>
    public bool IsConfigured => _options.Enabled && !string.IsNullOrWhiteSpace(_options.Model);

    public OllamaOptions Options => _options;

    /// <summary>更新配置（设置页保存后调用）。</summary>
    public void ApplyOptions(OllamaOptions options)
    {
        _options = options ?? new OllamaOptions();
    }

    /// <summary>
    /// 探活：查询 <c>/api/tags</c> 看服务是否在跑、目标模型是否已下载。
    /// 返回可展示的诊断文本（空字符串 = 一切正常）。
    /// </summary>
    public async Task<(bool Ok, string Detail)> ProbeAsync(CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_options.BaseUrl))
        {
            return (false, "未填写 Ollama 服务地址");
        }

        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(5));

            var tags = await _http.GetFromJsonAsync<OllamaTags>(
                Combine(_options.BaseUrl, "/api/tags"), timeoutCts.Token).ConfigureAwait(false);

            if (tags?.Models is null || tags.Models.Count == 0)
            {
                return (false, "Ollama 在运行，但没有已下载的模型。请先执行 `ollama pull <模型名>`。");
            }

            if (!string.IsNullOrWhiteSpace(_options.Model) &&
                !tags.Models.Any(m => IsModelMatch(m.Name, _options.Model)))
            {
                var available = string.Join("、", tags.Models.Select(m => m.Name).Take(5));
                return (false, $"未找到模型 {_options.Model}。本机已有：{available}");
            }

            return (true, string.Empty);
        }
        catch (OperationCanceledException)
        {
            return (false, "连接 Ollama 超时。请确认 Ollama 正在运行（`ollama serve`）。");
        }
        catch (HttpRequestException ex)
        {
            return (false, $"无法连接 Ollama（{_options.BaseUrl}）：{ex.Message}。请确认服务已启动。");
        }
        catch (Exception ex)
        {
            return (false, $"探测 Ollama 失败：{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <inheritdoc />
    public async Task<string> TranslateAsync(
        string text,
        string fromLanguageTag,
        string toLanguageTag,
        CancellationToken cancellationToken = default)
    {
        var normalized = NllbLanguageMap.NormalizeText(text);
        if (normalized.Length == 0)
        {
            return string.Empty;
        }

        if (!IsConfigured)
        {
            throw new InvalidOperationException(
                "Ollama 后端未配置：请在设置中启用，并填写模型名（例如 qwen2.5:7b-instruct）。");
        }

        var prompt = BuildPrompt(normalized, fromLanguageTag, toLanguageTag);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(Math.Max(10, _options.TimeoutSeconds)));

        var request = new OllamaGenerateRequest
        {
            Model = _options.Model,
            Prompt = prompt,
            Stream = false,
            Options = new OllamaGenerateOptions { Temperature = 0.1 },
        };

        try
        {
            using var response = await _http.PostAsJsonAsync(
                Combine(_options.BaseUrl, "/api/generate"),
                request,
                JsonOptions,
                timeoutCts.Token).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(timeoutCts.Token).ConfigureAwait(false);
                throw new InvalidOperationException(
                    $"Ollama 返回 {(int)response.StatusCode}：{Truncate(body, 300)}");
            }

            var result = await response.Content
                .ReadFromJsonAsync<OllamaGenerateResponse>(timeoutCts.Token)
                .ConfigureAwait(false);

            var translated = CleanOutput(result?.Response);
            _log?.Info($"[Ollama] 翻译完成 {normalized.Length} 字 → {translated.Length} 字（{_options.Model}）");
            return translated;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"Ollama 翻译超时（>{_options.TimeoutSeconds}s）。首次加载模型较慢，可增大超时或换更小模型。");
        }
    }

    /// <summary>
    /// 构造翻译提示。用「只输出译文」+ 语言名的明确约束，
    /// 因为本地小模型容易附加解释或原样复述。
    /// </summary>
    private static string BuildPrompt(string text, string fromLanguageTag, string toLanguageTag)
    {
        var from = DescribeLanguage(fromLanguageTag);
        var to = DescribeLanguage(toLanguageTag);

        var sb = new StringBuilder(text.Length + 200);
        sb.AppendLine($"把下面的{from}文本翻译成{to}。");
        sb.AppendLine("要求：只输出译文本身，不要解释、不要加引号、不要保留原文。");
        sb.AppendLine("如果原文是界面文案或报错提示，保持简洁的口吻。");
        sb.AppendLine();
        sb.AppendLine("原文：");
        sb.Append(text);
        return sb.ToString();
    }

    private static string DescribeLanguage(string tag)
    {
        if (string.IsNullOrWhiteSpace(tag))
        {
            return "外语";
        }

        var lower = tag.Trim().ToLowerInvariant();
        if (lower.StartsWith("zh-hant") || lower is "zh-tw" or "zh-hk")
        {
            return "繁体中文";
        }

        if (lower.StartsWith("zh"))
        {
            return "简体中文";
        }

        return lower switch
        {
            "en" or "en-us" or "en-gb" => "英语",
            "ja" or "ja-jp" => "日语",
            "ko" or "ko-kr" => "韩语",
            "fr" => "法语",
            "de" => "德语",
            "es" => "西班牙语",
            "ru" => "俄语",
            _ => tag,
        };
    }

    /// <summary>清理模型输出：去引号、去常见的「译文：」前缀、压缩空行。</summary>
    private static string CleanOutput(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return string.Empty;
        }

        var text = raw.Trim();

        // 去掉整体包裹的引号（模型很爱加）
        if (text.Length >= 2 &&
            ((text[0] == '"' && text[^1] == '"') || (text[0] == '「' && text[^1] == '」') ||
             (text[0] == '“' && text[^1] == '”')))
        {
            text = text[1..^1].Trim();
        }

        // 去掉「译文：」「翻译：」这类前缀
        foreach (var prefix in new[] { "译文：", "译文:", "翻译：", "翻译:", "Translation:", "Translation：" })
        {
            if (text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                text = text[prefix.Length..].Trim();
                break;
            }
        }

        return text;
    }

    private static string Combine(string baseUrl, string path)
        => baseUrl.TrimEnd('/') + path;

    private static bool IsModelMatch(string candidate, string wanted)
        => string.Equals(candidate, wanted, StringComparison.OrdinalIgnoreCase)
           || string.Equals(candidate.Split(':')[0], wanted.Split(':')[0], StringComparison.OrdinalIgnoreCase);

    private static string Truncate(string text, int max)
        => text.Length <= max ? text : text[..max] + "…";

    public void Dispose()
    {
        _http.Dispose();
    }

    // ---- Ollama API 的线格式（只声明用到的字段，避免过度耦合）----

    private sealed class OllamaGenerateRequest
    {
        [JsonPropertyName("model")]
        public string Model { get; set; } = string.Empty;

        [JsonPropertyName("prompt")]
        public string Prompt { get; set; } = string.Empty;

        [JsonPropertyName("stream")]
        public bool Stream { get; set; }

        [JsonPropertyName("options")]
        public OllamaGenerateOptions? Options { get; set; }
    }

    private sealed class OllamaGenerateOptions
    {
        [JsonPropertyName("temperature")]
        public double Temperature { get; set; }
    }

    private sealed class OllamaGenerateResponse
    {
        [JsonPropertyName("response")]
        public string? Response { get; set; }

        [JsonPropertyName("done")]
        public bool Done { get; set; }
    }

    private sealed class OllamaTags
    {
        [JsonPropertyName("models")]
        public List<OllamaTagEntry>? Models { get; set; }
    }

    private sealed class OllamaTagEntry
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;
    }
}
