using System.Diagnostics;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using ShotTool.Core;
using OrtF16 = Microsoft.ML.OnnxRuntime.Float16;

namespace ShotTool.Ai.Translation;

// ============================================================================
//  NllbOnnxTranslator.cs —— NLLB-200 distilled 600M 的 ONNX 本地推理实现
//
//  设计约束（来自 captain 定稿的规格）：
//    * 权重不进主包：只加载用户目录下的模型，IsConfigured=false 时 UI 显示安装引导。
//    * 延迟加载：首次点「翻译」才建 session（预计 1–2 秒、内存 0.7–1 GB）。
//    * 空闲 5 分钟自动卸载并释放 session，把内存还给系统。
//    * 解码先用贪心（短句够用），不做 beam search。
//    * 语言控制靠 src_lang / tgt_lang forced token，默认源语言按文本自动判定。
//
//  实现要点（每一条都有实测依据，改之前请先读 MODEL_CARD 与 NllbTokenizer 注释）：
//    1) 张量名从 session 的 InputMetadata/OutputMetadata 读，不硬编码 —— 兼容
//       encoder.onnx / encoder_model.onnx 等不同导出的命名差异。
//    2) dtype 从 **encoder 输出** 元数据判定（int8 动态量化档为 float32，
//       官方 fp16 导出为 float16）。绝不能读输入元数据 —— 输入恒为 int64。
//    3) 该产物 use_cache=false，decode 每步全量重跑 decoder，无 past。
//       因此复杂度 O(n²)，靠 max_new_tokens 与短句场景兜住。
//    4) tokenizer 模板会把 src_lang 硬编码成 eng_Latn，必须先剥离再重拼。
// ============================================================================

/// <summary>一次翻译的耗时与规模统计。用于冒烟证据、日志与「诊断」面板。</summary>
public sealed class NllbTranslationStats
{
    /// <summary>译文（同语言直通时为原文）。</summary>
    public string Text { get; init; } = string.Empty;

    public double EncoderMs { get; init; }

    /// <summary>每个解码步的耗时（毫秒）。长度 = 实际解码步数。</summary>
    public IReadOnlyList<double> DecodeStepMs { get; init; } = Array.Empty<double>();

    public double TotalMs { get; init; }

    public int SourceTokenCount { get; init; }

    public int GeneratedTokenCount { get; init; }

    /// <summary>本次加载模型（含首帧）耗时；已在加载态时为 0。</summary>
    public double LoadMs { get; init; }

    /// <summary>进程峰值工作集（MB）。</summary>
    public double PeakWorkingSetMb { get; init; }

    public double PeakPrivateMemoryMb { get; init; }

    public string PrecisionLabel { get; init; } = string.Empty;

    public override string ToString()
    {
        var avgStep = DecodeStepMs.Count > 0 ? DecodeStepMs.Average() : 0;
        return $"encoder={EncoderMs:F0}ms decode={DecodeStepMs.Count}步(均{avgStep:F0}ms) " +
               $"total={TotalMs:F0}ms tokens={SourceTokenCount}→{GeneratedTokenCount} " +
               $"峰值RSS={PeakWorkingSetMb:F0}MB {PrecisionLabel}";
    }
}

/// <summary>可调参数。默认值即产品默认行为。</summary>
public sealed class NllbTranslatorOptions
{
    /// <summary>生成上限（NLLB config 的 max_length=200；短句一般 20 以内）。</summary>
    public int MaxNewTokens { get; init; } = 200;

    /// <summary>空闲多久后卸载 session。规格要求 5 分钟。</summary>
    public TimeSpan IdleUnloadAfter { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>空闲检查间隔。</summary>
    public TimeSpan IdleCheckInterval { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>ONNX 线程数。0 = 交给 ORT 自行决定（默认用物理核数）。</summary>
    public int IntraOpThreads { get; init; }

    /// <summary>是否启用 ORT 图优化（首次 Run 会多花几百毫秒，之后更快）。</summary>
    public bool EnableGraphOptimization { get; init; } = true;

    public static NllbTranslatorOptions Default { get; } = new();
}

/// <summary>
/// NLLB 本地翻译器。实现 <see cref="ITranslationProvider"/>。
///
/// 线程模型：<see cref="TranslateAsync"/> 可并发调用，内部用信号量串行化推理
/// （ONNX session 与 tokenizer 均非线程安全）。加载/卸载与推理互斥。
/// </summary>
public sealed class NllbOnnxTranslator : ITranslationProvider, IDisposable
{
    public const string ProviderId = "nllb.onnx.local";

    private readonly INllbModelLocator _locator;
    private readonly IAppLog? _log;
    private readonly NllbTranslatorOptions _options;

    /// <summary>串行化「加载 / 卸载 / 推理」三类临界区。等待者按 FIFO 排队，保证先来先算。</summary>
    private readonly SemaphoreSlim _gate = new(1, 1);

    private NllbTokenizer? _tokenizer;
    private InferenceSession? _encoder;
    private InferenceSession? _decoder;
    private TensorNames? _names;
    private bool _isFp16;
    private string _precisionLabel = "unknown";
    private DateTime _lastUsedUtc = DateTime.UtcNow;

    // ★ 必须全限定：本项目 ImplicitUsings 含 System.Windows.Forms，
    //   裸写 Timer 会在 Forms.Timer 与 Threading.Timer 之间二义（CS0104）。
    //   这里要的是线程池计时器（可后台触发），不能用 UI 计时器。
    private System.Threading.Timer? _idleTimer;
    private bool _disposed;

    public NllbOnnxTranslator(INllbModelLocator locator, IAppLog? log = null, NllbTranslatorOptions? options = null)
    {
        _locator = locator ?? throw new ArgumentNullException(nameof(locator));
        _log = log;
        _options = options ?? NllbTranslatorOptions.Default;
    }

    /// <inheritdoc />
    public string Id => ProviderId;

    /// <inheritdoc />
    public string DisplayName => "NLLB-200（本地离线 / 600M）";

    /// <summary>模型文件齐备即可配置。注意这里只做文件系统探测，不加载 ONNX。</summary>
    public bool IsConfigured => !_disposed && _locator.Inspect().IsReady;

    /// <summary>session 当前是否在内存中（UI 可据此显示「已加载 / 已卸载」）。</summary>
    public bool IsLoaded => _encoder is not null;

    /// <summary>最近一次翻译的统计（冒烟与诊断用）。</summary>
    public NllbTranslationStats? LastStats { get; private set; }

    /// <summary>当前精度档标签（未加载时为模型文件名推断值）。</summary>
    public string PrecisionLabel => _precisionLabel;

    /// <summary>
    /// 已加载 encoder 的输出张量元素类型名（诊断用）。未加载时为 null。
    /// 这是 dtype 分支的权威依据：int8 动态量化档为 Single(float32)，官方 fp16 导出为 Float16。
    /// </summary>
    public string? LoadedEncoderOutputDtype { get; private set; }

    /// <summary>模型检查结果（UI 引导用）。</summary>
    public NllbModelCheck InspectModel() => _locator.Inspect();

    /// <summary>安装引导文本（UI 直接展示）。</summary>
    public string BuildInstallGuide() => _locator.BuildInstallGuide(_locator.Inspect());

    /// <inheritdoc />
    public async Task<string> TranslateAsync(
        string text,
        string fromLanguageTag,
        string toLanguageTag,
        CancellationToken cancellationToken = default)
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(NllbOnnxTranslator));
        }

        var normalized = NllbLanguageMap.NormalizeText(text);
        if (normalized.Length == 0)
        {
            return string.Empty;
        }

        // 语言标签 → FLORES 码。源语言标签为空时自动判定（中文优先）。
        var srcCode = NllbLanguageMap.TryToFlores(fromLanguageTag, out var resolvedSrc)
            ? resolvedSrc
            : NllbLanguageMap.DetectSourceCode(normalized);

        var tgtCode = NllbLanguageMap.TryToFlores(toLanguageTag, out var resolvedTgt)
            ? resolvedTgt
            : NllbLanguageMap.OppositeOf(srcCode);

        // 同语言直通：省一次完整推理，也避免模型把它「改写」成同义句
        if (NllbLanguageMap.IsSameLanguage(srcCode, tgtCode))
        {
            return normalized;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(true);
        try
        {
            var check = _locator.Inspect();
            if (!check.IsReady)
            {
                throw new NllbNotInstalledException(check, _locator.BuildInstallGuide(check));
            }

            var loadSw = Stopwatch.StartNew();
            EnsureLoaded(check);
            var loadMs = _tokenizer is null ? 0 : loadSw.Elapsed.TotalMilliseconds;

            _lastUsedUtc = DateTime.UtcNow;

            // 推理放到线程池：ORT 是纯 CPU 同步计算，占 UI 线程会卡住 overlay
            var stats = await Task.Run(() => TranslateCore(normalized, srcCode, tgtCode, loadMs, cancellationToken), cancellationToken)
                .ConfigureAwait(true);

            LastStats = stats;
            _lastUsedUtc = DateTime.UtcNow;
            return stats.Text;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>同步翻译（供冒烟 harness 与无 UI 场景使用，语义与 <see cref="TranslateAsync"/> 一致）。</summary>
    public NllbTranslationStats TranslateBlocking(string text, string? fromLanguageTag, string? toLanguageTag,
                                                  CancellationToken cancellationToken = default)
    {
        var normalized = NllbLanguageMap.NormalizeText(text);
        var srcCode = NllbLanguageMap.TryToFlores(fromLanguageTag, out var s)
            ? s
            : NllbLanguageMap.DetectSourceCode(normalized);
        var tgtCode = NllbLanguageMap.TryToFlores(toLanguageTag, out var t)
            ? t
            : NllbLanguageMap.OppositeOf(srcCode);

        if (NllbLanguageMap.IsSameLanguage(srcCode, tgtCode))
        {
            return new NllbTranslationStats
            {
                Text = normalized,
                SourceTokenCount = 0,
                GeneratedTokenCount = 0,
                PrecisionLabel = _precisionLabel,
            };
        }

        _gate.Wait(cancellationToken);
        try
        {
            var check = _locator.Inspect();
            if (!check.IsReady)
            {
                throw new NllbNotInstalledException(check, _locator.BuildInstallGuide(check));
            }

            var loadSw = Stopwatch.StartNew();
            EnsureLoaded(check);
            var loadMs = loadSw.Elapsed.TotalMilliseconds;
            _lastUsedUtc = DateTime.UtcNow;

            var stats = TranslateCore(normalized, srcCode, tgtCode, loadMs, cancellationToken);
            LastStats = stats;
            _lastUsedUtc = DateTime.UtcNow;
            return stats;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>核心推理：encoder 前向 + 贪心解码。调用方必须持有 <see cref="_gate"/>。</summary>
    private NllbTranslationStats TranslateCore(string text, string srcCode, string tgtCode, double loadMs,
                                               CancellationToken cancellationToken)
    {
        var tokenizer = _tokenizer ?? throw new InvalidOperationException("tokenizer 未加载");
        var encoder = _encoder ?? throw new InvalidOperationException("encoder session 未加载");
        var decoder = _decoder ?? throw new InvalidOperationException("decoder session 未加载");
        var names = _names ?? throw new InvalidOperationException("张量名未解析");

        var totalSw = Stopwatch.StartNew();

        // ---- 源侧：[src_lang] + content + [eos] ----
        var srcIds = tokenizer.BuildEncoderInput(text, srcCode);
        cancellationToken.ThrowIfCancellationRequested();

        // ---- encoder 前向 ----
        var encSw = Stopwatch.StartNew();
        var (hidden, hiddenShape) = RunEncoder(encoder, names, srcIds, cancellationToken);
        encSw.Stop();

        // ---- 贪心解码 ----
        var decIds = new List<long>(tokenizer.BuildDecoderStart(tgtCode));
        var stepTimes = new List<double>(32);
        var generated = new List<long>(32);

        for (var step = 0; step < _options.MaxNewTokens; step++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var stepSw = Stopwatch.StartNew();
            var next = RunDecoderStep(decoder, names, decIds, hidden, hiddenShape);
            stepSw.Stop();
            stepTimes.Add(stepSw.Elapsed.TotalMilliseconds);

            if (next == NllbTokenizer.EosId)
            {
                break;
            }

            generated.Add(next);
            decIds.Add(next);
        }

        totalSw.Stop();

        var decoded = generated.Count == 0 ? string.Empty : tokenizer.Decode(generated).Trim();
        var proc = Process.GetCurrentProcess();

        return new NllbTranslationStats
        {
            Text = decoded,
            EncoderMs = encSw.Elapsed.TotalMilliseconds,
            DecodeStepMs = stepTimes,
            TotalMs = totalSw.Elapsed.TotalMilliseconds,
            SourceTokenCount = srcIds.Length,
            GeneratedTokenCount = generated.Count,
            LoadMs = loadMs,
            PeakWorkingSetMb = proc.PeakWorkingSet64 / (1024d * 1024d),
            PeakPrivateMemoryMb = proc.PrivateMemorySize64 / (1024d * 1024d),
            PrecisionLabel = _precisionLabel,
        };
    }

    /// <summary>encoder 前向。输出类型依 dtype 分支返回 float[] 或 OrtF16[]。</summary>
    private (object data, int[] shape) RunEncoder(InferenceSession encoder, TensorNames names, long[] srcIds,
                                                  CancellationToken cancellationToken)
    {
        var seq = srcIds.Length;
        var mask = new long[seq];
        Array.Fill(mask, 1L);

        using var results = encoder.Run(new[]
        {
            NamedOnnxValue.CreateFromTensor(names.EncoderInputIds, new DenseTensor<long>(srcIds, new[] { 1, seq })),
            NamedOnnxValue.CreateFromTensor(names.EncoderAttentionMask, new DenseTensor<long>(mask, new[] { 1, seq })),
        });

        cancellationToken.ThrowIfCancellationRequested();
        var value = results.First(r => r.Name == names.EncoderOutput);

        if (_isFp16)
        {
            var t = value.AsTensor<OrtF16>();
            return (t.ToArray(), t.Dimensions.ToArray());
        }

        var tf = value.AsTensor<float>();
        return (tf.ToArray(), tf.Dimensions.ToArray());
    }

    /// <summary>一步 decoder 前向，返回该步 argmax token（可能是 EOS）。</summary>
    private long RunDecoderStep(InferenceSession decoder, TensorNames names, List<long> decIds,
                                object hidden, int[] hiddenShape)
    {
        var mask = new long[hiddenShape[1]];
        Array.Fill(mask, 1L);

        using var results = decoder.Run(new[]
        {
            NamedOnnxValue.CreateFromTensor(names.DecoderInputIds,
                new DenseTensor<long>(decIds.ToArray(), new[] { 1, decIds.Count })),
            NamedOnnxValue.CreateFromTensor(names.DecoderAttentionMask,
                new DenseTensor<long>(mask, new[] { 1, mask.Length })),
            MakeHiddenInput(names.DecoderHiddenStates, hidden, hiddenShape),
        });

        var logitsValue = results.First(r => r.Name == names.DecoderOutput);

        // 维度必须从 Tensor 取：DisposableNamedOnnxValue 本身没有 Dimensions
        if (_isFp16)
        {
            var lt = logitsValue.AsTensor<OrtF16>();
            var vocab16 = lt.Dimensions[^1];
            var last16 = lt.Dimensions[^2] - 1;
            return ArgMax(vocab16, v => (float)lt[0, last16, v]);
        }

        var lf = logitsValue.AsTensor<float>();
        var vocab = lf.Dimensions[^1];
        var last = lf.Dimensions[^2] - 1;
        return ArgMax(vocab, v => lf[0, last, v]);
    }

    private static long ArgMax(int vocab, Func<int, float> readAt)
    {
        var best = 0L;
        var bestVal = float.NegativeInfinity;
        for (var v = 0; v < vocab; v++)
        {
            var val = readAt(v);
            if (val > bestVal)
            {
                bestVal = val;
                best = v;
            }
        }

        return best;
    }

    /// <summary>
    /// 构造 decoder 的 encoder_hidden_states 输入。
    /// 必须按 <see cref="_isFp16"/> 走对应泛型重载 —— NamedOnnxValue.CreateFromTensor 是泛型的，
    /// 用 object 装箱会编译失败（探针阶段踩过）。
    /// </summary>
    private NamedOnnxValue MakeHiddenInput(string tensorName, object hidden, int[] shape)
    {
        if (_isFp16)
        {
            return NamedOnnxValue.CreateFromTensor(tensorName, new DenseTensor<OrtF16>((OrtF16[])hidden, shape));
        }

        return NamedOnnxValue.CreateFromTensor(tensorName, new DenseTensor<float>((float[])hidden, shape));
    }

    /// <summary>
    /// 确保 session 与 tokenizer 已加载。首次调用建 session（约 1–2 秒），
    /// 之后复用；同时启动空闲卸载计时器。
    /// </summary>
    private void EnsureLoaded(NllbModelCheck check)
    {
        if (_encoder is not null && _tokenizer is not null)
        {
            return;
        }

        if (string.IsNullOrEmpty(check.EncoderPath) || string.IsNullOrEmpty(check.DecoderPath) ||
            string.IsNullOrEmpty(check.TokenizerPath))
        {
            throw new NllbNotInstalledException(check, _locator.BuildInstallGuide(check));
        }

        var sw = Stopwatch.StartNew();

        // tokenizer 先建：它的异常信息最可读（语言码解析失败比 ORT 报错更好定位）
        _tokenizer = new NllbTokenizer(check.TokenizerPath);

        var sessionOptions = new SessionOptions
        {
            GraphOptimizationLevel = _options.EnableGraphOptimization
                ? GraphOptimizationLevel.ORT_ENABLE_ALL
                : GraphOptimizationLevel.ORT_DISABLE_ALL,
        };

        if (_options.IntraOpThreads > 0)
        {
            sessionOptions.IntraOpNumThreads = _options.IntraOpThreads;
        }

        _encoder = new InferenceSession(check.EncoderPath, sessionOptions);
        _decoder = new InferenceSession(check.DecoderPath, sessionOptions);

        _names = TensorNames.Resolve(_encoder, _decoder);

        // ★ dtype 必须看 encoder 的**输出**：输入恒为 int64，读输入会永远得到 Int64
        var outputElementType = _encoder.OutputMetadata[_names.EncoderOutput].ElementType;
        _isFp16 = outputElementType == typeof(OrtF16);
        LoadedEncoderOutputDtype = outputElementType.Name;
        _precisionLabel = check.PrecisionLabel;

        sw.Stop();
        _log?.Info($"[NLLB] 模型已加载 {sw.ElapsedMilliseconds}ms，精度={_precisionLabel}，" +
                   $"dtype={(_isFp16 ? "float16" : "float32")}，目录={check.ModelDirectory}");

        // 空闲卸载计时器（首次加载时创建一次）
        _idleTimer ??= new System.Threading.Timer(
            OnIdleTick, null, _options.IdleCheckInterval, _options.IdleCheckInterval);
    }

    /// <summary>空闲检查：超时且无人等待时释放 session，把 0.7–1 GB 内存还给系统。</summary>
    private void OnIdleTick(object? state)
    {
        if (_disposed || _encoder is null)
        {
            return;
        }

        if (DateTime.UtcNow - _lastUsedUtc < _options.IdleUnloadAfter)
        {
            return;
        }

        // 不阻塞等待：若正在推理（信号量被占），下一轮再试
        if (!_gate.Wait(0))
        {
            return;
        }

        try
        {
            if (_encoder is not null && DateTime.UtcNow - _lastUsedUtc >= _options.IdleUnloadAfter)
            {
                ReleaseSessions();
                _log?.Info($"[NLLB] 空闲超过 {_options.IdleUnloadAfter.TotalMinutes:F0} 分钟，已卸载模型并释放 session。");
            }
        }
        catch (Exception ex)
        {
            _log?.Error("[NLLB] 空闲卸载失败", ex);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>释放 session / tokenizer（不重建计时器，便于下次复用）。</summary>
    private void ReleaseSessions()
    {
        _encoder?.Dispose();
        _decoder?.Dispose();
        _tokenizer?.Dispose();
        _encoder = null;
        _decoder = null;
        _tokenizer = null;
        _names = null;
        _isFp16 = false;
        LoadedEncoderOutputDtype = null;
    }

    /// <summary>手动卸载（设置页「卸载模型」按钮 / 应用退出）。</summary>
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

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _idleTimer?.Dispose();
        _idleTimer = null;

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

    /// <summary>从 session 元数据解析出的张量名。只做一次，之后复用。</summary>
    private sealed class TensorNames
    {
        public required string EncoderInputIds { get; init; }

        public required string EncoderAttentionMask { get; init; }

        public required string EncoderOutput { get; init; }

        public required string DecoderInputIds { get; init; }

        public required string DecoderAttentionMask { get; init; }

        public required string DecoderHiddenStates { get; init; }

        public required string DecoderOutput { get; init; }

        /// <summary>
        /// 按 metadata 解析张量名。只做精确匹配 + 已知别名，
        /// **不做任意子串匹配** —— 否则 decoder 的 encoder_attention_mask 会被
        /// 误当成 encoder 的 attention_mask，把模型错配掩盖成静默错误（探针实测踩过）。
        /// </summary>
        public static TensorNames Resolve(InferenceSession encoder, InferenceSession decoder)
        {
            var encIn = encoder.InputMetadata;
            var decIn = decoder.InputMetadata;

            return new TensorNames
            {
                EncoderInputIds = Find(encIn, "encoder.input_ids", "input_ids"),
                EncoderAttentionMask = Find(encIn, "encoder.attention_mask", "attention_mask"),
                EncoderOutput = encoder.OutputMetadata.Keys.First(),
                DecoderInputIds = Find(decIn, "decoder.input_ids", "input_ids"),
                DecoderAttentionMask = Find(decIn, "decoder.encoder_attention_mask", "encoder_attention_mask"),
                DecoderHiddenStates = Find(decIn, "decoder.encoder_hidden_states", "encoder_hidden_states", "last_hidden_state"),
                DecoderOutput = decoder.OutputMetadata.Keys.First(),
            };
        }

        private static string Find(IReadOnlyDictionary<string, NodeMetadata> meta, string logical, params string[] aliases)
        {
            foreach (var candidate in aliases.Prepend(logical))
            {
                if (meta.ContainsKey(candidate))
                {
                    return candidate;
                }
            }

            throw new InvalidOperationException(
                $"模型缺少输入 '{logical}'；该模型实际输入为: {string.Join(", ", meta.Keys)}。" +
                "若这是 decoder_model_merged 变体，请改用纯 decoder_model.onnx（本实现不使用 KV cache）。");
        }
    }
}

/// <summary>模型未安装（或未就绪）。异常消息与 <see cref="InstallGuide"/> 可直接展示给用户。</summary>
public sealed class NllbNotInstalledException : Exception
{
    public NllbNotInstalledException(NllbModelCheck check, string installGuide)
        : base(check.Describe())
    {
        Check = check;
        InstallGuide = installGuide;
    }

    public NllbModelCheck Check { get; }

    /// <summary>多行安装引导（含目标目录、文件清单、获取地址、许可与署名）。</summary>
    public string InstallGuide { get; }
}
