namespace ShotTool.Ai.Translation;

// ============================================================================
//  INllbModelLocator.cs —— NLLB 模型定位与校验的契约
//
//  为什么需要这一层：NLLB-200-distilled-600M 的权重**不进主包**（600M 参数，
//  int8 档约 1.07 GiB；打进 exe 会让 15 MB 变成 1 GB+）。模型由用户按引导自行
//  放置到约定目录。因此「模型在不在、缺哪个文件、缺多少」必须能给出**可操作**的
//  诊断，而不是在推理时抛一个 NullReference 或 ONNX 的晦涩错误。
//
//  命名兼容：不同来源的导出文件名不一致（官方 optimum 导出 vs 社区量化产物），
//  所以定位器按**候选名列表**匹配，调用方不需要关心具体文件名。
//
//  许可提醒（重要，不得省略）：
//    NLLB-200-distilled-600M 为 CC-BY-NC-4.0（仅非商业）。UI 与 README 必须显式
//    告知用户，不得表述为「可商用」。署名要求见 NllbModelLocator.LicenseNotice。
// ============================================================================

/// <summary>
/// 模型缺失或不完整的一条具体问题。每条都自带可展示给用户的修复指引，
/// UI 直接列出即可，不需要再拼文案。
/// </summary>
public sealed class NllbModelProblem
{
    public NllbModelProblem(
        string kind,
        string summary,
        string? expectedPath,
        long expectedBytes,
        long actualBytes,
        bool isBlocking = true)
    {
        Kind = kind ?? string.Empty;
        Summary = summary ?? string.Empty;
        ExpectedPath = expectedPath;
        ExpectedBytes = expectedBytes;
        ActualBytes = actualBytes;
        IsBlocking = isBlocking;
    }

    /// <summary>问题种类：<c>missing-dir</c> / <c>missing-file</c> / <c>size-mismatch</c> / <c>unreadable</c>。</summary>
    public string Kind { get; }

    /// <summary>一句话摘要，例如「缺少 encoder_model_int8.onnx」。</summary>
    public string Summary { get; }

    /// <summary>期望的完整路径（缺失时即「应该放在这里」）。</summary>
    public string? ExpectedPath { get; }

    /// <summary>期望字节数（0 = 未知，不做校验）。</summary>
    public long ExpectedBytes { get; }

    /// <summary>实际字节数（0 = 文件不存在）。</summary>
    public long ActualBytes { get; }

    /// <summary>
    /// true = 阻断加载（文件缺失 / 空文件）；
    /// false = 仅提示（字节数与已知产物不同，可能是别的量化档或版本，仍允许尝试加载）。
    /// </summary>
    public bool IsBlocking { get; }

    public override string ToString()
    {
        if (ActualBytes > 0 && ExpectedBytes > 0)
        {
            return $"{Summary}（期望 {ExpectedBytes:N0} 字节，实际 {ActualBytes:N0} 字节）";
        }

        return Summary;
    }
}

/// <summary>
/// 模型来源。设置页**必须**显示当前用的是哪一份 ——
/// 否则用户删掉用户目录的模型却发现仍可用，会困惑「是不是没删干净」。
/// </summary>
public enum NllbModelSource
{
    /// <summary>没找到任何可用模型。</summary>
    None = 0,

    /// <summary>随包分发：exe 同目录 <c>models\nllb-200-distilled-600M-int8\</c>（优先级最高）。</summary>
    Bundled = 1,

    /// <summary>用户自下：<c>%LOCALAPPDATA%\ShotTool\models\...</c>。</summary>
    UserLocal = 2,

    /// <summary>历史遗留：<c>%APPDATA%\WeChatShot\models\...</c>（提示迁移）。</summary>
    Legacy = 3,

    /// <summary>用户在设置里指定的自定义目录。</summary>
    CustomOverride = 4,
}

/// <summary>
/// 一次模型检查的结果。只有在 <see cref="IsReady"/> 为 true 时才可加载推理。
/// </summary>
public sealed class NllbModelCheck
{
    public NllbModelCheck(
        bool isReady,
        string modelDirectory,
        string? encoderPath,
        string? decoderPath,
        string? tokenizerPath,
        string? configPath,
        string precisionLabel,
        long totalBytes,
        IReadOnlyList<NllbModelProblem> problems,
        NllbModelSource source = NllbModelSource.None)
    {
        IsReady = isReady;
        ModelDirectory = modelDirectory ?? string.Empty;
        EncoderPath = encoderPath;
        DecoderPath = decoderPath;
        TokenizerPath = tokenizerPath;
        ConfigPath = configPath;
        PrecisionLabel = precisionLabel ?? string.Empty;
        TotalBytes = totalBytes;
        Problems = problems ?? Array.Empty<NllbModelProblem>();
        Source = source;
    }

    /// <summary>这份模型从哪来（随包 / 用户目录 / 历史位置 / 自定义）。设置页必须展示。</summary>
    public NllbModelSource Source { get; }

    /// <summary>三个必需文件（encoder / decoder / tokenizer）是否齐备且字节数正确。</summary>
    public bool IsReady { get; }

    public string ModelDirectory { get; }

    public string? EncoderPath { get; }

    public string? DecoderPath { get; }

    public string? TokenizerPath { get; }

    /// <summary>config.json（可选，用于读取 eos / decoder_start / vocab 等真值）。</summary>
    public string? ConfigPath { get; }

    /// <summary>精度档标签：<c>int8</c> / <c>fp16</c> / <c>fp32</c> / <c>unknown</c>。仅用于 UI 显示。</summary>
    public string PrecisionLabel { get; }

    /// <summary>已识别权重文件的总字节数（引导页展示磁盘占用用）。</summary>
    public long TotalBytes { get; }

    public IReadOnlyList<NllbModelProblem> Problems { get; }

    public static NllbModelCheck NotReady(string directory, params NllbModelProblem[] problems)
        => new(false, directory, null, null, null, null, "unknown", 0, problems);

    /// <summary>来源的中文显示名，供设置页「当前使用：xxx」直接使用。</summary>
    public static string DescribeSource(NllbModelSource source) => source switch
    {
        NllbModelSource.Bundled => "随包自带",
        NllbModelSource.UserLocal => "用户目录（LOCALAPPDATA）",
        NllbModelSource.Legacy => "旧位置（APPDATA，建议迁移）",
        NllbModelSource.CustomOverride => "用户指定的自定义目录",
        _ => "未找到",
    };

    /// <summary>给用户看的紧凑摘要（一行）。</summary>
    public string Describe()
    {
        if (IsReady)
        {
            return $"NLLB 模型就绪（{DescribeSource(Source)}，{PrecisionLabel}，" +
                   $"{FormatBytes(TotalBytes)}）：{ModelDirectory}";
        }

        if (Problems.Count == 0)
        {
            return $"NLLB 模型未安装：{ModelDirectory}";
        }

        return $"NLLB 模型未就绪（{Problems.Count} 项问题）：{Problems[0].Summary}";
    }

    /// <summary>把字节数格式化成人类可读（Gitibi 语义，与 UI 文案 1.07 GiB 口径一致）。</summary>
    public static string FormatBytes(long bytes)
    {
        if (bytes <= 0)
        {
            return "0 B";
        }

        const double GiB = 1024d * 1024d * 1024d;
        const double MiB = 1024d * 1024d;
        if (bytes >= GiB)
        {
            return $"{bytes / GiB:N2} GiB";
        }

        if (bytes >= MiB)
        {
            return $"{bytes / MiB:N0} MiB";
        }

        return $"{bytes / 1024d:N0} KiB";
    }
}

/// <summary>
/// NLLB 模型定位器。实现见 <c>NllbModelLocator</c>。
///
/// 线程模型：<see cref="Inspect()"/> 只做文件系统探测（无网络、无 ONNX 加载），
/// 可在任意线程调用；UI 层若在热路径频繁调用，建议自行缓存结果。
/// </summary>
public interface INllbModelLocator
{
    /// <summary>
    /// **优先级 1（最高）**：随包自带目录 —— exe 同目录 <c>models\nllb-200-distilled-600M-int8\</c>。
    ///
    /// 发布形态：模型**不打进 exe 单文件**，而是随发布包放在 exe 旁边的 <c>models\</c> 下，
    /// 否则每次启动都要解压 1.08 GiB（启动会从 ~180 ms 掉到十几秒）。
    /// 用 <c>AppContext.BaseDirectory</c> 解析，兼容双击运行与从任意工作目录启动。
    /// </summary>
    string BundledModelDirectory { get; }

    /// <summary>
    /// **优先级 2**：用户自下目录 —— <c>%LOCALAPPDATA%\ShotTool\models\nllb-200-distilled-600M-int8</c>。
    ///
    /// ★ 为什么是 LocalAppData 而不是 Roaming：
    ///   模型 1.08 GiB，放在 Roaming 会被域环境（企业/学校账号）同步到漫游服务器，
    ///   每次登录都拖网速、还会撑爆漫游配额。大文件必须放 Local。
    /// </summary>
    string DefaultModelDirectory { get; }

    /// <summary>
    /// **优先级 3（兜底）**：历史路径 <c>%APPDATA%\WeChatShot\models\nllb-200-distilled-600M-int8</c>。
    /// 早期版本用过这个位置；保留探测以免用户白下 1.08 GiB。新安装不再写入这里。
    /// </summary>
    string LegacyModelDirectory { get; }

    /// <summary>
    /// 当前生效目录：按「自定义覆盖 &gt; 随包 &gt; 用户目录 &gt; 历史位置」解析，
    /// 返回**第一个就绪**的目录；都不就绪时返回随包目录（用于生成"缺哪个文件"的引导）。
    /// </summary>
    string ActiveDirectory { get; }

    /// <summary>当前生效目录的来源（随包 / 用户目录 / 历史 / 自定义 / 未找到）。设置页必须展示。</summary>
    NllbModelSource ActiveSource { get; }

    /// <summary>设置自定义目录（优先级最高）；传 null 或空串取消覆盖，回到三级自动解析。</summary>
    void SetDirectoryOverride(string? directory);

    /// <summary>检查当前生效目录（含来源）。</summary>
    NllbModelCheck Inspect();

    /// <summary>检查指定目录（不改变生效目录），来源标记为 <see cref="NllbModelSource.CustomOverride"/>。</summary>
    NllbModelCheck Inspect(string? directory);

    /// <summary>
    /// 检查历史路径是否有可用模型。没有返回 null。
    /// UI 据此提示「检测到旧位置的模型，是否迁移到新位置」。
    /// </summary>
    NllbModelCheck? InspectLegacy();

    /// <summary>
    /// 把历史路径的模型迁移到当前生效目录（同盘为瞬时元数据操作，不重写 1 GB）。
    /// 已迁移、新位置已有模型、历史路径不存在时返回 false 并给出原因。
    /// 这是显式操作：由设置页按钮或组合根调用，不在构造/探测时自动执行。
    /// </summary>
    bool TryMigrateLegacy(out string message);

    /// <summary>
    /// 给用户看的安装引导（多行纯文本，可直接塞进对话框或面板）。
    /// 内容包含：目标目录、需要的文件与精确字节数、获取地址、许可与署名。
    /// </summary>
    string BuildInstallGuide(NllbModelCheck check);
}
