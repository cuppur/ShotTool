using System.Text;

namespace ShotTool.Ai.Translation;

// ============================================================================
//  NllbLanguageMap.cs —— BCP-47 语言标签 ↔ FLORES-200 语言码 的双向映射
//
//  为什么需要这一层：项目其余部分（Core/Contracts.cs 的 ShotToolOptions、
//  Windows.Media.Ocr、UI）统一用 BCP-47（"zh-Hans" / "en" / "ja" / "ko"），
//  而 NLLB 只认 FLORES-200 语言码（"zho_Hans" / "eng_Latn" / "jpn_Jpan" / "kor_Hang"），
//  并且这些码在 tokenizer 里是**独立的 added token**，必须原样拼进序列。
//
//  ★ 关键坑（实测，务必不要改回去）：
//    NLLB 的语言码 token id 绝不能硬编码。社区导出的 README 里给的 id 表是错的
//    （实测 jpn_Jpan=256079 而非 256121，zho_Hans=256200 而非 256167）。
//    真值必须运行时从 tokenizer.json 的 added_tokens 解析 —— 见 NllbTokenizer。
//    本文件只负责「码名」映射，不掺 id。
// ============================================================================

/// <summary>BCP-47 ↔ FLORES-200 语言码映射与源语言自动判定。</summary>
public static class NllbLanguageMap
{
    /// <summary>NLLB 的默认源语言码（模型训练时的主语言）。</summary>
    public const string DefaultSourceCode = "eng_Latn";

    /// <summary>中文简体码。CJK 文本自动判定时使用。</summary>
    public const string ChineseSimplifiedCode = "zho_Hans";

    // BCP-47（小写、用 '-' 分隔）→ FLORES-200。
    // 只覆盖实用子集：UI 主推中/英/日/韩，其余常见语言一并列上，避免用户改配置后失效。
    private static readonly Dictionary<string, string> ToFlores = new(StringComparer.OrdinalIgnoreCase)
    {
        // —— 中文 ——
        ["zh"] = "zho_Hans",
        ["zh-hans"] = "zho_Hans",
        ["zh-cn"] = "zho_Hans",
        ["zh-sg"] = "zho_Hans",
        ["zh-hans-cn"] = "zho_Hans",
        ["zh-hant"] = "zho_Hant",
        ["zh-tw"] = "zho_Hant",
        ["zh-hk"] = "zho_Hant",
        ["zh-mo"] = "zho_Hant",
        ["zh-hant-tw"] = "zho_Hant",
        ["yue"] = "yue_Hant",

        // —— 英语 ——
        ["en"] = "eng_Latn",
        ["en-us"] = "eng_Latn",
        ["en-gb"] = "eng_Latn",

        // —— 日韩 ——
        ["ja"] = "jpn_Jpan",
        ["ja-jp"] = "jpn_Jpan",
        ["ko"] = "kor_Hang",
        ["ko-kr"] = "kor_Hang",

        // —— 常见欧洲语言 ——
        ["fr"] = "fra_Latn",
        ["de"] = "deu_Latn",
        ["es"] = "spa_Latn",
        ["pt"] = "por_Latn",
        ["it"] = "ita_Latn",
        ["ru"] = "rus_Cyrl",
        ["uk"] = "ukr_Cyrl",
        ["pl"] = "pol_Latn",
        ["nl"] = "nld_Latn",
        ["sv"] = "swe_Latn",
        ["da"] = "dan_Latn",
        ["fi"] = "fin_Latn",
        ["no"] = "nob_Latn",
        ["cs"] = "ces_Latn",
        ["el"] = "ell_Grek",
        ["tr"] = "tur_Latn",

        // —— 其它常用 ——
        ["ar"] = "arb_Arab",
        ["he"] = "heb_Hebr",
        ["hi"] = "hin_Deva",
        ["th"] = "tha_Thai",
        ["vi"] = "vie_Latn",
        ["id"] = "ind_Latn",
        ["ms"] = "zsm_Latn",
        ["fa"] = "pes_Arab",
    };

    // FLORES-200 → 展示用 BCP-47（反向查表，构造时由 ToFlores 生成，保持单一事实源）
    private static readonly Dictionary<string, string> FromFlores = BuildReverse();

    /// <summary>UI 主推的四种目标语言（顺序即下拉框顺序）。</summary>
    public static readonly IReadOnlyList<(string Code, string DisplayName)> UiLanguages = new[]
    {
        (ChineseSimplifiedCode, "中文（简体）"),
        ("eng_Latn", "英语"),
        ("jpn_Jpan", "日语"),
        ("kor_Hang", "韩语"),
    };

    private static Dictionary<string, string> BuildReverse()
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        // 反向时以「最短且最通用」的 BCP-47 为准，避免 zh-hans-cn 覆盖 zh-Hans
        foreach (var (bcp, flores) in ToFlores)
        {
            if (!map.ContainsKey(flores) || bcp.Length < map[flores].Length)
            {
                map[flores] = bcp;
            }
        }

        return map;
    }

    /// <summary>
    /// BCP-47 语言标签 → FLORES-200 语言码。
    /// 已经是 FLORES 码（含下划线且首段为三字母）时原样返回。
    /// 无法识别返回 false（调用方应回落到 <see cref="DefaultSourceCode"/> 或提示用户）。
    /// </summary>
    public static bool TryToFlores(string? languageTag, out string floresCode)
    {
        floresCode = DefaultSourceCode;
        if (string.IsNullOrWhiteSpace(languageTag))
        {
            return false;
        }

        var tag = languageTag.Trim();

        // 已经是 FLORES 码，例如用户直接填了 zho_Hans
        if (IsFloresCode(tag))
        {
            floresCode = tag;
            return true;
        }

        // 去掉枚举/区域后缀的兜底：zh-Hans-CN → 先试全串，再逐段回退
        if (ToFlores.TryGetValue(tag, out var mapped))
        {
            floresCode = mapped;
            return true;
        }

        var primary = tag.Split('-', '_')[0];
        if (ToFlores.TryGetValue(primary, out var primaryMapped))
        {
            floresCode = primaryMapped;
            return true;
        }

        return false;
    }

    /// <summary>FLORES-200 码 → 展示用 BCP-47。</summary>
    public static string ToBcp47(string floresCode)
        => !string.IsNullOrWhiteSpace(floresCode) && FromFlores.TryGetValue(floresCode, out var bcp)
            ? bcp
            : "en";

    /// <summary>判断字符串是否形如 FLORES-200 语言码（<c>xxx_Yyyy</c>，3 字母 + 下划线 + 4 字符且首字母大写）。</summary>
    public static bool IsFloresCode(string? text)
        => text is { Length: 8 } && text[3] == '_' && char.IsUpper(text[4]);

    /// <summary>
    /// 源语言自动判定：文本含 CJK 字符 → <c>zho_Hans</c>，否则 <c>eng_Latn</c>。
    ///
    /// 为什么用 CJK 而不是更细的语种检测：截图场景的文本量通常很短（几个词到几行），
    /// 统计式语种识别的方差大于收益；而「中文 vs 拉丁字母」的区分几乎零误判。
    /// 用户在 UI 上可手动覆盖，自动判定只作为默认值。
    /// </summary>
    public static string DetectSourceCode(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return DefaultSourceCode;
        }

        foreach (var ch in text)
        {
            if (IsCjk(ch))
            {
                return ChineseSimplifiedCode;
            }
        }

        return DefaultSourceCode;
    }

    /// <summary>是否 CJK（含中文、日文假名、韩文，以及全角标点）。</summary>
    public static bool IsCjk(char ch)
    {
        // CJK 统一表意文字及其扩展 A / 兼容区
        if (ch is >= '\u4E00' and <= '\u9FFF') return true;   // Unified Ideographs
        if (ch is >= '\u3400' and <= '\u4DBF') return true;   // Extension A
        if (ch is >= '\uF900' and <= '\uFAFF') return true;   // Compatibility Ideographs

        // 日文假名
        if (ch is >= '\u3040' and <= '\u30FF') return true;   // Hiragana + Katakana

        // 韩文音节与字母
        if (ch is >= '\uAC00' and <= '\uD7AF') return true;   // Hangul Syllables
        if (ch is >= '\u1100' and <= '\u11FF') return true;   // Hangul Jamo

        // 全角标点/符号（中文截图中常见，不含它会导致纯标点行误判为英文）
        if (ch is >= '\u3000' and <= '\u303F') return true;
        if (ch is >= '\uFF00' and <= '\uFFEF') return true;

        return false;
    }

    /// <summary>
    /// 判断「源语言 == 目标语言」——此时应直接返回原文，不做推理。
    /// 注意 zho_Hans 与 zho_Hant 视为不同语言（NLLB 支持简繁互转）。
    /// </summary>
    public static bool IsSameLanguage(string srcFlores, string tgtFlores)
        => string.Equals(srcFlores, tgtFlores, StringComparison.Ordinal);

    /// <summary>与源语言「相反」的目标语言：中文→英语，其它→中文。供 UI 默认值使用。</summary>
    public static string OppositeOf(string floresCode)
        => IsChinese(floresCode) ? "eng_Latn" : ChineseSimplifiedCode;

    /// <summary>是否中文（简体或繁体）。</summary>
    public static bool IsChinese(string? floresCode)
        => floresCode is "zho_Hans" or "zho_Hant";

    /// <summary>把 FLORES 码转成中文显示名；未知码回落为码本身，便于排查。</summary>
    public static string DisplayName(string floresCode)
    {
        foreach (var (code, name) in UiLanguages)
        {
            if (string.Equals(code, floresCode, StringComparison.Ordinal))
            {
                return name;
            }
        }

        return string.IsNullOrWhiteSpace(floresCode) ? "(未指定)" : floresCode;
    }

    /// <summary>去除首尾空白的辅助（翻译前清理文本，避免把纯空白送进模型）。</summary>
    public static string NormalizeText(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        // 统一换行，并压缩连续空白为单个空格（NLLB 对多余空白敏感，会浪费 token）
        var sb = new StringBuilder(text.Length);
        var lastWasSpace = false;
        foreach (var ch in text)
        {
            if (ch is '\r' or '\n' or '\t')
            {
                if (!lastWasSpace)
                {
                    sb.Append(' ');
                    lastWasSpace = true;
                }

                continue;
            }

            if (char.IsWhiteSpace(ch))
            {
                if (!lastWasSpace)
                {
                    sb.Append(' ');
                    lastWasSpace = true;
                }

                continue;
            }

            sb.Append(ch);
            lastWasSpace = false;
        }

        return sb.ToString().Trim();
    }
}
