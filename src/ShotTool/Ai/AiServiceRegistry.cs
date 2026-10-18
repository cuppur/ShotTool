using System.Drawing;
using System.Windows.Forms;
using ShotTool.Ai.Ocr;
using ShotTool.Ai.Translation;
using ShotTool.Core;

namespace ShotTool.Ai;

// ============================================================================
//  AiServiceRegistry.cs —— IAiServiceRegistry 实现 + 组合根装配入口
//
//  职责：
//    1) 持有三个 OCR 后端（Windows / Paddle / Auto）与翻译后端工厂；
//    2) 实现 Core 的 IAiServiceRegistry，供 Editor/App 按接口消费；
//    3) 提供 <see cref="Initialize"/> 作为**组合根唯一一行**的接线入口。
//
//  ★ 关于 backend 选择（captain 定案）：
//    设置键 `OcrBackend` 三值：auto（默认）/ windows / paddle。
//    - auto  = <see cref="AutoSelectingRecognizer"/>，按选区亮度选一个引擎，只跑一次；
//    - 未知值一律回退 auto（不抛、不静默变 windows）。
//
//  ★ 为什么没有"先 Paddle 失败再回落 Windows"：
//    倾斜场景 Paddle 返回 Quality=Suspect 而非异常，回落条件不会触发；
//    且双跑会让暗色场景从 335 ms 变成 419 ms。按亮度只跑一次，延迟可预测。
// ============================================================================

public sealed class AiServiceRegistry : IAiServiceRegistry, IDisposable
{
    private readonly List<ITextRecognizer> _recognizers = new(3);
    private readonly List<ITranslationProvider> _translations = new(4);
    private readonly IAppLog? _log;
    private bool _disposed;

    public AiServiceRegistry(IAppLog? log = null)
    {
        _log = log;
    }

    /// <summary>Windows 系统 OCR（零下载，默认候选）。Initialize 后赋值。</summary>
    public WindowsMediaOcrRecognizer? Windows { get; private set; }

    /// <summary>PaddleOCR（内嵌模型，深色场景更准）。</summary>
    public PaddleOcrRecognizer? Paddle { get; private set; }

    /// <summary>auto：按亮度选一个引擎。</summary>
    public AutoSelectingRecognizer? Auto { get; private set; }

    /// <summary>翻译后端工厂（NLLB / Ollama）。</summary>
    public TranslationBackendFactory? Translations2 { get; private set; }

    /// <inheritdoc />
    public IReadOnlyList<ITextRecognizer> Recognizers => _recognizers;

    /// <inheritdoc />
    public IReadOnlyList<ITranslationProvider> Translations => _translations;

    /// <summary>当前用户选择的 OCR 后端（auto / windows / paddle）。</summary>
    public string SelectedBackend { get; private set; } = "auto";

    /// <inheritdoc />
    public event EventHandler? Changed;

    /// <summary>
    /// 组合根接线入口。**一行调用**：
    /// <code>var ai = AiServiceRegistry.Initialize(context, context.ToolbarExtensions);</code>
    ///
    /// 会做四件事：
    ///   ① 建三个 OCR 后端并注册到本 registry；
    ///   ② 读设置里的 `OcrBackend` / `TranslationBackend` / `NllbModelDirectory`；
    ///   ③ 建翻译后端工厂（NLLB + Ollama），按设置决定是否启用；
    ///   ④ 若传入 toolbarRegistry，则注册「提取文字 / 翻译」按钮提供者。
    /// </summary>
    public static AiServiceRegistry Initialize(
        ShotToolContext context,
        IToolbarRegistry? toolbarRegistry = null,
        ISettingsStore? settings = null)
    {
        ArgumentNullException.ThrowIfNull(context);

        var registry = new AiServiceRegistry(context.Log);

        // ① OCR 后端
        var windows = new WindowsMediaOcrRecognizer(context.Log);
        var paddle = new PaddleOcrRecognizer(context.Log);
        var auto = new AutoSelectingRecognizer(windows, paddle, context.Log);

        // ② 设置（缺失时按默认：OCR=auto，翻译=off）
        var store = settings ?? context.Settings;
        var aiOptions = store is null ? AiSettingsSnapshot.Default : AiSettingsSnapshot.Read(store);

        registry.SelectedBackend = aiOptions.OcrBackend;
        registry.Windows = windows;
        registry.Paddle = paddle;
        registry.Auto = auto;
        registry.Register(windows);
        registry.Register(paddle);
        registry.Register(auto);

        // ③ 翻译后端工厂
        var locator = new NllbModelLocator(aiOptions.NllbModelDirectory);
        var factory = new TranslationBackendFactory(
            locator,
            new OllamaOptions
            {
                Enabled = aiOptions.OllamaEnabled,
                BaseUrl = aiOptions.OllamaBaseUrl,
                Model = aiOptions.OllamaModel,
            },
            log: context.Log);
        factory.ApplyBackendSelection(aiOptions.TranslationBackend);
        registry.Translations2 = factory;

        foreach (var provider in factory.ResolveAllProviders())
        {
            registry.Register(provider);
        }

        // ④ 工具条按钮（若挂点已就绪）
        if (toolbarRegistry is not null)
        {
            toolbarRegistry.Register(new OcrToolbarItems(registry, context.Log));
            context.Log.Info("[AI] 已注册工具条扩展：提取文字 / 翻译");
        }
        else
        {
            context.Log.Warn("[AI] ToolbarExtensions 未装配，OCR 按钮未注册（组合根需先设 context.ToolbarExtensions）");
        }

        context.Ai = registry;
        context.Log.Info($"[AI] 装配完成：OCR 后端={registry.SelectedBackend}，" +
                         $"可用识别器={registry.Recognizers.Count(r => r.IsAvailable)}/" +
                         $"{registry.Recognizers.Count}，翻译={aiOptions.TranslationBackend}，" +
                         $"NLLB 就绪={locator.Inspect().IsReady}（{NllbModelCheck.DescribeSource(locator.ActiveSource)}）");

        return registry;
    }

    /// <summary>按设置切换 OCR 后端（设置页保存后调用）。未知值回退 auto。</summary>
    public void ApplyBackend(string? backend)
    {
        var normalized = (backend ?? string.Empty).Trim().ToLowerInvariant();
        SelectedBackend = normalized switch
        {
            "windows" => "windows",
            "paddle" => "paddle",
            _ => "auto",
        };

        _log?.Info($"[AI] OCR 后端切换为 {SelectedBackend}");
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <inheritdoc />
    public void Register(ITextRecognizer recognizer)
    {
        if (recognizer is null || _recognizers.Contains(recognizer))
        {
            return;
        }

        _recognizers.Add(recognizer);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <inheritdoc />
    public void Register(ITranslationProvider provider)
    {
        if (provider is null || _translations.Contains(provider))
        {
            return;
        }

        _translations.Add(provider);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// 优先返回设置里 <c>OcrBackend</c> 对应的引擎；不可用则按 auto → windows → paddle 顺序回落。
    /// </summary>
    public ITextRecognizer? ResolveDefaultRecognizer()
    {
        ITextRecognizer? ById(string id) =>
            _recognizers.FirstOrDefault(r => string.Equals(r.Id, id, StringComparison.OrdinalIgnoreCase));

        var preferred = ById(SelectedBackend switch
        {
            "windows" => WindowsMediaOcrRecognizer.RecognizerId,
            "paddle" => PaddleOcrRecognizer.RecognizerId,
            _ => AutoSelectingRecognizer.RecognizerId
        });
        if (preferred is { IsAvailable: true })
        {
            return preferred;
        }

        // 设置的后端不可用 → 按 可用性 顺序找第一个能用的（这是"能力缺失"的回落，不是 auto 的运行时回落）
        foreach (var id in new[] { AutoSelectingRecognizer.RecognizerId, WindowsMediaOcrRecognizer.RecognizerId, PaddleOcrRecognizer.RecognizerId })
        {
            var r = ById(id);
            if (r is { IsAvailable: true })
            {
                return r;
            }
        }

        return _recognizers.FirstOrDefault(r => r.IsAvailable);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Translations2?.Dispose();
        Windows?.Dispose();
        Paddle?.Dispose();
        Auto?.Dispose();
    }
}

/// <summary>
/// 设置快照（读 <c>Sections.ai</c>）。键名与 dev-integration 的 `AiOptions` 一致：
/// <c>OcrBackend</c>(auto/windows/paddle) / <c>TranslationBackend</c>(off/nllb/ollama) /
/// <c>NllbModelDirectory</c> / <c>Ollama*</c>。
/// 任何缺失/坏值一律回退默认，绝不抛。
/// </summary>
internal sealed class AiSettingsSnapshot
{
    public string OcrBackend { get; init; } = "auto";

    public string TranslationBackend { get; init; } = "off";

    public string? NllbModelDirectory { get; init; }

    public bool OllamaEnabled { get; init; }

    public string OllamaBaseUrl { get; init; } = "http://127.0.0.1:11434";

    public string OllamaModel { get; init; } = string.Empty;

    public static AiSettingsSnapshot Default { get; } = new();

    public static AiSettingsSnapshot Read(ISettingsStore store)
    {
        try
        {
            var json = store.GetSection("ai");
            if (string.IsNullOrWhiteSpace(json))
            {
                return Default;
            }

            using var doc = System.Text.Json.JsonDocument.Parse(json);
            var root = doc.RootElement;

            string? Str(string name)
            {
                if (!root.TryGetProperty(name, out var v)) return null;
                return v.ValueKind == System.Text.Json.JsonValueKind.String ? v.GetString() : null;
            }

            bool Bool(string name)
            {
                if (!root.TryGetProperty(name, out var v)) return false;
                return v.ValueKind == System.Text.Json.JsonValueKind.True;
            }

            return new AiSettingsSnapshot
            {
                OcrBackend = (Str("OcrBackend") ?? "auto").Trim().ToLowerInvariant() switch
                {
                    "windows" => "windows",
                    "paddle" => "paddle",
                    _ => "auto",
                },
                TranslationBackend = (Str("TranslationBackend") ?? "off").Trim().ToLowerInvariant(),
                NllbModelDirectory = Str("NllbModelDirectory"),
                OllamaEnabled = Bool("OllamaEnabled"),
                OllamaBaseUrl = Str("OllamaBaseUrl") ?? "http://127.0.0.1:11434",
                OllamaModel = Str("OllamaModel") ?? string.Empty,
            };
        }
        catch
        {
            return Default;
        }
    }
}
