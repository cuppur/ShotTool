using System.Text;

namespace ShotTool.Ai.Ocr;

// ============================================================================
//  OcrTextPostProcessor.cs —— OCR 文本后处理（纯字符串逻辑，不依赖 WinRT）
//
//  为什么需要它：Windows.Media.Ocr 对中文是**逐字切分**的，OcrResult.Text 用空格把
//  每个汉字连起来，实测得到：
//      "网 络 连 接 失 败 ， 请 稍 后 重 试"
//  直接显示很难看；直接送进翻译模型会被切碎语义（tokenizer 把空格当分隔符）。
//
//  本类负责把引擎输出还原成自然文本。规则（captain 定稿，已 8/8 实测验证）：
//
//    规则 A：**仅当空格两侧都是 CJK 字符时删除该空格**（"网 络" → "网络"）。
//    规则 B：中英混排、数字与单位、拉丁词之间的空格**保留原样**
//            （"网络 failed 连接"、"文件 3 个"、"共 120 px" 都不能动）。
//    规则 C：**不自动合并**被 OCR 切开的英文词（"net work" 不合成 "network"）。
//            理由：误合并比不合并更糟，且无法可靠判断。这类问题留给用户在
//            可编辑的结果面板里手改。
//
//  为什么不在这里放"识别"逻辑：本文件不引用任何 WinRT 类型，因此在 csproj 的
//  TargetFramework 加上平台版本号之前也能编译 —— 这样它能先合入、先被测试覆盖，
//  不会挡住整仓构建。
// ============================================================================

/// <summary>OCR 文本后处理：CJK 去空格、按行重建纯文本。</summary>
public static class OcrTextPostProcessor
{
    /// <summary>
    /// 删除「两侧都是 CJK」的空格。中英混排与数字/单位之间的空格原样保留。
    /// 连续多个空格时逐个判定，只删除符合条件的那几个。
    /// </summary>
    public static string CollapseCjkSpaces(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        // 快速路径：没有任何空格就不需要处理（OCR 纯英文结果常见）
        if (text.IndexOf(' ') < 0)
        {
            return text;
        }

        var sb = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (ch != ' ')
            {
                sb.Append(ch);
                continue;
            }

            var prev = i > 0 ? text[i - 1] : '\0';
            var next = i + 1 < text.Length ? text[i + 1] : '\0';

            // 规则 A：仅当两侧都是 CJK 才删；其余（规则 B）保留
            if (IsCjk(prev) && IsCjk(next))
            {
                continue;
            }

            sb.Append(ch);
        }

        return sb.ToString();
    }

    /// <summary>
    /// 把 OCR 的逐行结果重建为整段文本。
    ///
    /// <paramref name="preserveLayout"/> = true：保留行间换行（行内仍做 CJK 去空格）；
    /// false：行间也压成单个空格（得到一段连续文本）。
    /// <paramref name="singleLine"/> = true：无论 preserveLayout 如何，全部压成一行。
    /// </summary>
    public static string BuildPlainText(IEnumerable<string> lines, bool preserveLayout, bool singleLine)
    {
        if (lines is null)
        {
            return string.Empty;
        }

        var cleaned = new List<string>(16);
        foreach (var raw in lines)
        {
            if (raw is null)
            {
                continue;
            }

            // 行内先按规则 A/B 处理，再去掉行首尾空白（引擎常给出行首缩进噪声）
            var line = CollapseCjkSpaces(raw).Trim();
            if (line.Length > 0)
            {
                cleaned.Add(line);
            }
        }

        if (cleaned.Count == 0)
        {
            return string.Empty;
        }

        if (singleLine)
        {
            return string.Join(' ', cleaned);
        }

        return preserveLayout
            ? string.Join('\n', cleaned)
            : string.Join(' ', cleaned);
    }

    /// <summary>
    /// 是否 CJK 字符。判定口径**必须与 <c>NllbLanguageMap.IsCjk</c> 保持一致** ——
    /// 否则会出现「OCR 认为这是中文并去掉空格、翻译器认为这不是中文」的割裂行为。
    ///
    /// 覆盖：CJK 统一表意文字及扩展 A / 兼容区、日文假名、韩文音节与字母、
    /// CJK 标点（U+3000-303F）、全角形式（U+FF00-FFEF）。
    /// </summary>
    public static bool IsCjk(char ch)
    {
        if (ch is >= '\u4E00' and <= '\u9FFF') return true;   // 统一表意文字
        if (ch is >= '\u3400' and <= '\u4DBF') return true;   // 扩展 A
        if (ch is >= '\uF900' and <= '\uFAFF') return true;   // 兼容表意文字
        if (ch is >= '\u3040' and <= '\u30FF') return true;   // 平假名 + 片假名
        if (ch is >= '\uAC00' and <= '\uD7AF') return true;   // 韩文音节
        if (ch is >= '\u1100' and <= '\u11FF') return true;   // 韩文字母
        if (ch is >= '\u3000' and <= '\u303F') return true;   // CJK 标点（。、「」等）
        if (ch is >= '\uFF00' and <= '\uFFEF') return true;   // 全角形式（，：；！？等）
        return false;
    }

    /// <summary>
    /// 判断文本是否含 CJK 字符。用于「该不该用中文引擎 / 该不该去空格」的启发式判断。
    /// </summary>
    public static bool ContainsCjk(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        foreach (var ch in text)
        {
            if (IsCjk(ch))
            {
                return true;
            }
        }

        return false;
    }
}
