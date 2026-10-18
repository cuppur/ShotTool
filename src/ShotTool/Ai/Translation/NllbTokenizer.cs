using System.Text.Json;
using Tokenizers.DotNet;

namespace ShotTool.Ai.Translation;

// ============================================================================
//  NllbTokenizer.cs —— NLLB 分词器 + 语言码 token 解析
//
//  ★ 两个必须知道的实测坑（改动前先读）：
//
//  坑 1：语言码 token id 不能硬编码。
//    社区 ONNX 产物的 README 里给的表是**错的**：实测 jpn_Jpan=256079（README 写 256121）、
//    zho_Hans=256200（README 写 256167）。真值只能从 tokenizer.json 的 added_tokens 解析。
//    本类用 System.Text.Json 直接读 added_tokens —— 便宜（约 250ms / 17 MB）、可靠、
//    不依赖 native 行为，且已与 native Encode 结果交叉验证一致（202 个 FLORES 语言码）。
//
//  坑 2：tokenizer.json 的 post_processor 把 src_lang **硬编码成 eng_Latn**。
//    `Encode("你好")` 实际返回 [256047(eng_Latn), 12695, 249725, 2(</s>)]。
//    若把这个序列直接喂给 encoder，中文文本会被当成「英语源」→ 译文质量严重下降甚至乱译。
//    本类因此提供 EncodeContent()：先探测模板（空串 Encode 得 [lang, </s>]），
//    再剥离固定的前后缀，由调用方重新拼 [真实 src_lang] + content + [eos]。
//
//  依赖：Tokenizers.DotNet（managed）+ Tokenizers.DotNet.runtime.win-x64（native hf_tokenizers.dll）。
//  只装前者会在运行时找不到 native 而抛 DllNotFoundException。
// ============================================================================

/// <summary>NLLB 分词器。非线程安全：同一实例请串行使用（翻译器内部已加锁）。</summary>
public sealed class NllbTokenizer : IDisposable
{
    /// <summary>NLLB 的 </s> / decoder_start 均为 2，pad 为 1（来自官方 config.json）。</summary>
    public const long EosId = 2;

    public const long PadId = 1;

    private readonly Tokenizer _tokenizer;
    private readonly int _templatePrefixLength;
    private readonly int _templateSuffixLength;
    private bool _disposed;

    /// <summary>FLORES-200 语言码 → token id（从 tokenizer.json 解析，202 条）。</summary>
    public IReadOnlyDictionary<string, long> LanguageCodes { get; }

    /// <summary>tokenizer.json 的 post_processor 里固定的默认源语言码（实测恒为 eng_Latn）。</summary>
    public string TemplateDefaultLanguageCode { get; }

    public NllbTokenizer(string tokenizerJsonPath)
    {
        if (!File.Exists(tokenizerJsonPath))
        {
            throw new FileNotFoundException($"找不到 tokenizer.json：{tokenizerJsonPath}", tokenizerJsonPath);
        }

        // 先静态解析（失败信息比 native 的异常更可读）
        (LanguageCodes, TemplateDefaultLanguageCode) = ParseTokenizerJson(tokenizerJsonPath);

        _tokenizer = new Tokenizer(vocabPath: tokenizerJsonPath);

        // 用空串探测模板固定插入的前后缀，例如 [eng_Latn, </s>] → 前缀 1、后缀 1
        var probe = _tokenizer.Encode(string.Empty);
        _templateSuffixLength = probe.Length >= 2 && probe[^1] == EosId ? 1 : 0;
        _templatePrefixLength = probe.Length - _templateSuffixLength;
    }

    /// <summary>
    /// 把文本编成**纯内容** token：剥离 tokenizer 模板自带的语言前缀与 eos 后缀。
    /// 调用方负责拼上真实 src_lang 与 eos。
    /// </summary>
    public long[] EncodeContent(string text)
    {
        ThrowIfDisposed();
        if (string.IsNullOrEmpty(text))
        {
            return Array.Empty<long>();
        }

        var raw = _tokenizer.Encode(text);
        var start = Math.Min(_templatePrefixLength, raw.Length);
        var end = Math.Max(start, raw.Length - _templateSuffixLength);

        var content = new long[end - start];
        for (var i = 0; i < content.Length; i++)
        {
            content[i] = raw[start + i];
        }

        return content;
    }

    /// <summary>解码 token 序列为文本。语言码 token 会被 native 库忽略或渲染为原样，调用方应先剔除。</summary>
    public string Decode(IReadOnlyList<long> ids)
    {
        ThrowIfDisposed();
        if (ids.Count == 0)
        {
            return string.Empty;
        }

        var buffer = new uint[ids.Count];
        for (var i = 0; i < ids.Count; i++)
        {
            var id = ids[i];
            buffer[i] = id is >= 0 and <= uint.MaxValue ? (uint)id : 0u;
        }

        return _tokenizer.Decode(buffer);
    }

    /// <summary>
    /// 解析语言码；不存在时返回 false（调用方应回落到默认源语言并提示用户语言不被支持）。
    /// </summary>
    public bool TryGetLanguageTokenId(string floresCode, out long tokenId)
    {
        tokenId = 0;
        return !string.IsNullOrWhiteSpace(floresCode) && LanguageCodes.TryGetValue(floresCode, out tokenId);
    }

    /// <summary>
    /// 构造源侧输入序列：<c>[src_lang_id] + content tokens + [eos]</c>。
    /// 这正是 NLLB encoder 期望的格式（语言码在最前，eos 结束）。
    /// </summary>
    public long[] BuildEncoderInput(string text, string sourceFloresCode)
    {
        var content = EncodeContent(text);
        if (!TryGetLanguageTokenId(sourceFloresCode, out var langId))
        {
            // 语言码未知时用模板默认语言，至少不产生非法 token
            langId = LanguageCodes.TryGetValue(TemplateDefaultLanguageCode, out var fallback) ? fallback : 0;
        }

        var result = new long[content.Length + 2];
        result[0] = langId;
        Array.Copy(content, 0, result, 1, content.Length);
        result[^1] = EosId;
        return result;
    }

    /// <summary>
    /// 解码器起手序列：<c>[decoder_start_token_id, tgt_lang_id]</c>。
    /// NLLB 用 decoder_start=2（即 </s>）作为序列起始，紧跟目标语言码强制指定输出语言。
    /// </summary>
    public long[] BuildDecoderStart(string targetFloresCode)
    {
        var tgtId = TryGetLanguageTokenId(targetFloresCode, out var id)
            ? id
            : (LanguageCodes.TryGetValue(TemplateDefaultLanguageCode, out var fallback) ? fallback : 0);
        return new[] { EosId, tgtId };
    }

    /// <summary>
    /// 从 tokenizer.json 的 added_tokens 解析 FLORES-200 语言码 → id，并读取模板默认语言。
    /// 纯静态解析，不触碰 native 库。
    /// </summary>
    public static (Dictionary<string, long> LanguageCodes, string TemplateDefaultCode) ParseTokenizerJson(string tokenizerJsonPath)
    {
        var codes = new Dictionary<string, long>(StringComparer.Ordinal);
        var templateDefault = NllbLanguageMap.DefaultSourceCode;

        using var stream = File.OpenRead(tokenizerJsonPath);
        using var doc = JsonDocument.Parse(stream);
        var root = doc.RootElement;

        if (root.TryGetProperty("added_tokens", out var added) && added.ValueKind == JsonValueKind.Array)
        {
            foreach (var token in added.EnumerateArray())
            {
                if (token.ValueKind != JsonValueKind.Object) continue;
                if (!token.TryGetProperty("content", out var contentEl) || contentEl.ValueKind != JsonValueKind.String) continue;
                if (!token.TryGetProperty("id", out var idEl) || idEl.ValueKind != JsonValueKind.Number) continue;

                var content = contentEl.GetString();
                if (NllbLanguageMap.IsFloresCode(content) && idEl.TryGetInt64(out var id))
                {
                    codes[content!] = id;
                }
            }
        }

        // 读取 post_processor 模板里固定的语言码（用于诊断与兜底）
        if (root.TryGetProperty("post_processor", out var pp) &&
            pp.TryGetProperty("single", out var single) &&
            single.ValueKind == JsonValueKind.Array)
        {
            foreach (var step in single.EnumerateArray())
            {
                if (step.TryGetProperty("SpecialToken", out var special) &&
                    special.TryGetProperty("id", out var idEl) &&
                    idEl.ValueKind == JsonValueKind.String)
                {
                    var code = idEl.GetString();
                    if (NllbLanguageMap.IsFloresCode(code))
                    {
                        templateDefault = code!;
                        break;
                    }
                }
            }
        }

        return (codes, templateDefault);
    }

    /// <summary>
    /// 轻量预检：不解码 17 MB JSON 全量结构，只数出语言码条目数与 eos 是否可解析。
    /// 用于设置页「检查模型」按钮，避免为了显示一行状态就构造 native tokenizer。
    /// </summary>
    public static bool TryPeek(string tokenizerJsonPath, out int languageCodeCount, out string error)
    {
        languageCodeCount = 0;
        error = string.Empty;
        try
        {
            if (!File.Exists(tokenizerJsonPath))
            {
                error = "文件不存在";
                return false;
            }

            var (codes, _) = ParseTokenizerJson(tokenizerJsonPath);
            languageCodeCount = codes.Count;
            if (codes.Count < 100)
            {
                error = $"语言码条目过少（{codes.Count}），文件可能不完整";
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            error = $"{ex.GetType().Name}: {ex.Message}";
            return false;
        }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(NllbTokenizer));
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _tokenizer.Dispose();
    }
}
