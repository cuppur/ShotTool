using ShotTool.Core;

namespace ShotTool.Ai.Translation;

// ============================================================================
//  TranslationBackendFactory.cs —— 翻译后端的选择、降级与状态上报
//
//  规格要求（captain 定稿）：
//    「如果 ONNX 路径在时间盒内跑不通，先交付『Ollama 后端可用 + NLLB 后端显示未安装
//      并给出安装引导』，不要为了硬啃 NLLB 拖垮整个 t9。」
//
//  因此本工厂的核心不是「找第一个能用的后端」，而是**如实上报三种状态**：
//    * NllbReady      —— NLLB 权重齐备，首选它（完全离线、无第三方进程依赖）
//    * OllamaReady    —— NLLB 未安装但本机 Ollama 可用，兜底
//    * NotConfigured  —— 两者都没有，UI 应隐藏翻译按钮或弹安装引导，而不是报错
//
//  设计意图：让 UI 能区分「功能不可用」与「功能未安装」，前者是 bug，后者是常态。
// ============================================================================

/// <summary>翻译后端种类。</summary>
public enum TranslationBackendKind
{
    /// <summary>没有任何可用后端。</summary>
    None = 0,

    /// <summary>本地 NLLB ONNX（首选，完全离线）。</summary>
    Nllb = 1,

    /// <summary>本机 Ollama（兜底）。</summary>
    Ollama = 2,
}

/// <summary>
/// 用户在设置里选择的翻译后端（对应 settings.json 的 <c>Sections.ai.TranslationBackend</c>）。
/// 取值与 dev-integration 约定的写盘枚举一致，读取忽略大小写。
/// </summary>
public enum TranslationBackendSelection
{
    /// <summary>关闭翻译功能（默认值，避免未安装模型的用户看到不可用按钮）。</summary>
    Off = 0,

    /// <summary>强制使用本地 NLLB；模型缺失时 UI 应展示安装引导。</summary>
    Nllb = 1,

    /// <summary>强制使用本机 Ollama。</summary>
    Ollama = 2,

    /// <summary>自动：模型齐备用 NLLB，否则回落到已配置的 Ollama。</summary>
    Auto = 3,
}

/// <summary>
/// 后端状态快照。UI 直接拿它渲染「已就绪 / 未安装 / 未配置」与对应操作提示。
/// </summary>
public sealed class TranslationBackendStatus
{
    public TranslationBackendStatus(
        TranslationBackendKind kind,
        bool available,
        string summary,
        string? actionHint = null,
        string? installGuide = null,
        NllbModelCheck? modelCheck = null)
    {
        Kind = kind;
        Available = available;
        Summary = summary ?? string.Empty;
        ActionHint = actionHint;
        InstallGuide = installGuide;
        ModelCheck = modelCheck;
    }

    public TranslationBackendKind Kind { get; }

    /// <summary>当前是否真的能翻译。</summary>
    public bool Available { get; }

    /// <summary>一行状态摘要，例如「NLLB 已就绪（int8，1.08 GiB）」。</summary>
    public string Summary { get; }

    /// <summary>建议用户做的下一步（可为 null）。</summary>
    public string? ActionHint { get; }

    /// <summary>多行安装引导（NLLB 未安装时由 locator 生成，含文件清单与许可）。</summary>
    public string? InstallGuide { get; }

    /// <summary>NLLB 模型检查详情（仅在 kind=Nllb 时有值）。</summary>
    public NllbModelCheck? ModelCheck { get; }

    public override string ToString() => $"{Kind} available={Available} {Summary}";
}

/// <summary>
/// 翻译后端工厂：持有 NLLB 与 Ollama 两个实现，按优先级选择当前可用后端。
///
/// 线程模型：构造轻量（不加载任何模型）；<see cref="Nllb"/> 的 session 由其自身延迟加载。
/// </summary>
public sealed class TranslationBackendFactory : IDisposable
{
    private readonly INllbModelLocator _locator;
    private readonly IAppLog? _log;
    private readonly OllamaOptions _ollamaOptions;
    private readonly NllbTranslatorOptions _nllbOptions;

    private NllbOnnxTranslator? _nllb;
    private OllamaTranslationProvider? _ollama;
    private bool _disposed;

    /// <summary>
    /// 用户选择的翻译后端。默认 <see cref="TranslationBackendSelection.Off"/>，
    /// 与 dev-integration 的 settings.json 默认值保持一致（未安装模型的用户不该看到可用按钮）。
    /// </summary>
    public TranslationBackendSelection Selection { get; private set; } = TranslationBackendSelection.Off;

    /// <summary>
    /// 应用设置里的 <c>TranslationBackend</c> 字符串。
    /// 取值 <c>"off"</c> / <c>"nllb"</c> / <c>"ollama"</c>（大小写不敏感），
    /// 其它任何值（含 null / 空 / 坏数据）一律回退 <see cref="TranslationBackendSelection.Off"/>。
    /// </summary>
    public void ApplyBackendSelection(string? backend)
    {
        Selection = ParseSelection(backend);
    }

    /// <summary>把设置字符串解析为枚举；无法识别回退 Off（安全默认，绝不让坏配置导致不可预期行为）。</summary>
    public static TranslationBackendSelection ParseSelection(string? backend)
    {
        if (string.IsNullOrWhiteSpace(backend))
        {
            return TranslationBackendSelection.Off;
        }

        return backend.Trim().ToLowerInvariant() switch
        {
            "nllb" => TranslationBackendSelection.Nllb,
            "ollama" => TranslationBackendSelection.Ollama,
            "auto" => TranslationBackendSelection.Auto,
            "off" => TranslationBackendSelection.Off,
            _ => TranslationBackendSelection.Off,
        };
    }

    public TranslationBackendFactory(
        INllbModelLocator locator,
        OllamaOptions? ollamaOptions = null,
        NllbTranslatorOptions? nllbOptions = null,
        IAppLog? log = null)
    {
        _locator = locator ?? throw new ArgumentNullException(nameof(locator));
        _ollamaOptions = ollamaOptions ?? new OllamaOptions();
        _nllbOptions = nllbOptions ?? NllbTranslatorOptions.Default;
        _log = log;
    }

    /// <summary>模型定位器（设置页用来校验目录）。</summary>
    public INllbModelLocator Locator => _locator;

    /// <summary>NLLB 后端实例（惰性创建；构造它不加载模型）。</summary>
    public NllbOnnxTranslator Nllb => _nllb ??= new NllbOnnxTranslator(_locator, _log, _nllbOptions);

    /// <summary>Ollama 后端实例（惰性创建）。</summary>
    public OllamaTranslationProvider Ollama => _ollama ??= new OllamaTranslationProvider(_ollamaOptions, _log);

    /// <summary>更新 Ollama 配置（设置页保存后调用）。</summary>
    public void ApplyOllamaOptions(OllamaOptions options)
    {
        _ollamaOptions.Enabled = options.Enabled;
        _ollamaOptions.BaseUrl = options.BaseUrl;
        _ollamaOptions.Model = options.Model;
        _ollamaOptions.TimeoutSeconds = options.TimeoutSeconds;
        _ollama?.ApplyOptions(_ollamaOptions);
    }

    /// <summary>
    /// 同步状态快照（不发网络请求）。用于 UI 绘制按钮可用态。
    /// NLLB 只做文件系统探测；Ollama 只判断「用户是否启用并填了模型名」。
    /// </summary>
    public TranslationBackendStatus DescribeStatus()
    {
        var check = _locator.Inspect();

        return Selection switch
        {
            // 用户在设置里关掉了翻译 → 不暴露任何后端，UI 隐藏翻译按钮
            TranslationBackendSelection.Off => new TranslationBackendStatus(
                TranslationBackendKind.None,
                available: false,
                summary: "翻译功能已关闭",
                actionHint: "在设置中启用翻译后端（本地 NLLB 模型，或本机 Ollama）。",
                modelCheck: check),

            // 强制 NLLB：模型缺失时如实报「未安装 + 引导」，不回落到 Ollama
            TranslationBackendSelection.Nllb => check.IsReady
                ? NllbReadyStatus(check)
                : new TranslationBackendStatus(
                    TranslationBackendKind.Nllb,
                    available: false,
                    summary: $"NLLB 未安装：{check.Problems.FirstOrDefault()?.Summary ?? "模型文件缺失"}",
                    actionHint: "按引导把 3 个文件放到模型目录后即可使用（完全离线）。",
                    installGuide: _locator.BuildInstallGuide(check),
                    modelCheck: check),

            // 强制 Ollama
            TranslationBackendSelection.Ollama => DescribeOllama(check),

            // 自动：NLLB 优先，其次 Ollama
            _ => check.IsReady
                ? NllbReadyStatus(check)
                : (_ollamaOptions.Enabled && !string.IsNullOrWhiteSpace(_ollamaOptions.Model)
                    ? DescribeOllama(check)
                    : new TranslationBackendStatus(
                        TranslationBackendKind.None,
                        available: false,
                        summary: "未安装翻译后端",
                        actionHint: "可安装本地 NLLB 模型（完全离线），或在设置中启用本机 Ollama。",
                        installGuide: _locator.BuildInstallGuide(check),
                        modelCheck: check)),
        };
    }

    private static TranslationBackendStatus NllbReadyStatus(NllbModelCheck check)
        => new(
            TranslationBackendKind.Nllb,
            available: true,
            summary: $"NLLB 已就绪（{check.PrecisionLabel}，{NllbModelCheck.FormatBytes(check.TotalBytes)}）",
            actionHint: null,
            installGuide: null,
            modelCheck: check);

    private TranslationBackendStatus DescribeOllama(NllbModelCheck check)
    {
        if (_ollamaOptions.Enabled && !string.IsNullOrWhiteSpace(_ollamaOptions.Model))
        {
            return new TranslationBackendStatus(
                TranslationBackendKind.Ollama,
                available: true,
                summary: $"Ollama 已配置（{_ollamaOptions.Model}）",
                actionHint: "首次翻译会连接本机 Ollama；若失败请检查 `ollama serve` 是否在运行。",
                modelCheck: check);
        }

        return new TranslationBackendStatus(
            TranslationBackendKind.Ollama,
            available: false,
            summary: "Ollama 未配置",
            actionHint: "在设置中启用 Ollama 并填写模型名（例如 qwen2.5:7b-instruct）。",
            modelCheck: check);
    }

    /// <summary>
    /// 带探活的状态（会尝试连接 Ollama）。设置页「测试连接」按钮用。
    /// </summary>
    public async Task<TranslationBackendStatus> ProbeAsync(CancellationToken cancellationToken = default)
    {
        var snapshot = DescribeStatus();
        if (snapshot.Kind == TranslationBackendKind.Nllb)
        {
            return snapshot;
        }

        if (_ollamaOptions.Enabled && !string.IsNullOrWhiteSpace(_ollamaOptions.Model))
        {
            var (ok, detail) = await Ollama.ProbeAsync(cancellationToken).ConfigureAwait(false);
            if (ok)
            {
                return new TranslationBackendStatus(
                    TranslationBackendKind.Ollama,
                    available: true,
                    summary: $"Ollama 可用（{_ollamaOptions.Model}）",
                    modelCheck: _locator.Inspect());
            }

            return new TranslationBackendStatus(
                TranslationBackendKind.Ollama,
                available: false,
                summary: $"Ollama 不可用：{detail}",
                actionHint: "确认 Ollama 正在运行并已 pull 对应模型；或改安装本地 NLLB。",
                installGuide: _locator.BuildInstallGuide(_locator.Inspect()),
                modelCheck: _locator.Inspect());
        }

        return snapshot;
    }

    /// <summary>
    /// 取当前应使用的翻译后端。没有任何可用后端时返回 null（调用方据此隐藏按钮/提示安装）。
    /// 优先级：NLLB（完全离线，无外部进程） &gt; Ollama。
    /// </summary>
    public ITranslationProvider? ResolveActiveProvider()
    {
        var nllbReady = _locator.Inspect().IsReady;
        var ollamaReady = _ollamaOptions.Enabled && !string.IsNullOrWhiteSpace(_ollamaOptions.Model);

        return Selection switch
        {
            TranslationBackendSelection.Off => null,
            TranslationBackendSelection.Nllb => nllbReady ? Nllb : null,
            TranslationBackendSelection.Ollama => ollamaReady ? Ollama : null,
            _ => nllbReady ? Nllb : (ollamaReady ? Ollama : null),
        };
    }

    /// <summary>取全部后端（供设置页列表展示「已安装 / 未安装」）。</summary>
    public IReadOnlyList<ITranslationProvider> ResolveAllProviders()
        => new ITranslationProvider[] { Nllb, Ollama };

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _nllb?.Dispose();
        _ollama?.Dispose();
        _nllb = null;
        _ollama = null;
    }
}
