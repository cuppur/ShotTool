using System.Drawing;
using System.Text;
using System.Windows.Forms;
using ShotTool.Ai.Ocr;
using ShotTool.Ai.Translation;
using ShotTool.Core;
using ShotTool.Output;

namespace ShotTool.Ai;

// ============================================================================
//  OcrToolbarItems.cs —— 「提取文字」/「翻译」工具条按钮 + 结果面板
//
//  按 Core 契约实现（**不另造接口**）：
//    <see cref="IToolbarItemProvider"/> → 被 dev-editor 的 IToolbarRegistry 消费；
//    <see cref="IToolbarItem"/>         → Editor 只按接口渲染，不认识 OCR/翻译；
//    <see cref="ToolbarContext"/>        → 提供选区图 / ShowPanel / ShowToast。
//
//  ★ 面板落位：`PanelPlacement.Right`（右侧竖直长条）。
//    理由：OCR 结果是多行文本、可选中、可能十几行，横向条放不下；翻译的原文/译文切换也纵向更自然。
//
//  ★ 线程：OnClick 在 UI 线程同步返回；OCR/翻译在后台跑，完成后回 UI 线程再 ShowPanel。
//    Editor 会兜住 OnClick 抛出的异常，但后台任务的异常我们必须自己 catch（否则静默丢失）。
//
//  ★ 内存提示：NLLB 首次加载约 2.3 s、峰值 1.6 GB，所以翻译的 loading 态画在**面板内容**里，
//    不依赖 ShowToast（toast 约 2 s 自动消失，撑不住 2.3 s 的加载）。
// ============================================================================

/// <summary>「提取文字」+「翻译」两个按钮与结果面板。</summary>
public sealed class OcrToolbarItems : IToolbarItemProvider, IDisposable
{
    private readonly AiServiceRegistry _registry;
    private readonly IAppLog? _log;
    private readonly List<IToolbarItem> _items;
    private bool _disposed;

    public OcrToolbarItems(AiServiceRegistry registry, IAppLog? log = null)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _log = log;

        _items = new List<IToolbarItem>(2)
        {
            new OcrButton(registry, log),
            new TranslateButton(registry, log),
        };
    }

    /// <inheritdoc />
    public IEnumerable<IToolbarItem> GetItems() => _items;

    /// <inheritdoc />
    public event EventHandler? ItemsChanged;

    /// <summary>触发按钮可用态刷新（例如设置里切换了后端）。</summary>
    public void NotifyChanged() => ItemsChanged?.Invoke(this, EventArgs.Empty);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        foreach (var item in _items)
        {
            (item as IDisposable)?.Dispose();
        }
    }
}

/// <summary>「提取文字」按钮。</summary>
internal sealed class OcrButton : IToolbarItem
{
    private readonly AiServiceRegistry _registry;
    private readonly IAppLog? _log;

    public OcrButton(AiServiceRegistry registry, IAppLog? log)
    {
        _registry = registry;
        _log = log;
    }

    public string Id => "ai.ocr";

    /// <summary>紧贴「撤销」右侧（微信实测第 16 个按钮位置附近）。</summary>
    public ToolbarSlot Slot => ToolbarSlot.Right;

    public int Order => 101;

    public string Tooltip => "提取文字";

    public ToolbarGlyph Glyph => ToolbarGlyph.Ocr;

    public bool IsToggle => false;

    public bool IsChecked => false;

    /// <summary>没有可用引擎时隐藏（不是灰显）—— 用户看不到多余按钮，比点了报错好。</summary>
    public bool IsVisible(ToolbarContext context) => _registry.ResolveDefaultRecognizer() is not null;

    public bool IsEnabled(ToolbarContext context) => _registry.ResolveDefaultRecognizer() is { IsAvailable: true };

    public void OnClick(ToolbarContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var bitmap = context.SelectionBitmapProvider();
        if (bitmap is null)
        {
            context.ShowToast("还没有选区");
            return;
        }

        var recognizer = _registry.ResolveDefaultRecognizer();
        if (recognizer is null)
        {
            // 依赖 Core 的 OcrUnavailableException（WindowsMediaOcrRecognizer.cs 里定义）
            context.ShowToast("没有可用的 OCR 引擎");
            bitmap.Dispose();
            return;
        }

        // 先挂面板（loading 态），让用户知道在跑，而不是看起来卡住
        var panel = new OcrResultPanel(recognizer.DisplayName, DpiHelper.ScaleFactor(context.Toolbar.Dpi));
        context.Toolbar.ShowPanel(new ToolbarPanelRequest(panel, PanelPlacement.Right, "提取文字")
        {
            PreferredSizeDip = new PhysSize(360, 420),
        });

        // 后台识别，完成后回 UI 线程
        _ = RunAsync(context, bitmap, recognizer, panel);
    }

    private async Task RunAsync(ToolbarContext context, Bitmap bitmap, ITextRecognizer recognizer, OcrResultPanel panel)
    {
        try
        {
            var options = new OcrOptions
            {
                LanguageTag = "zh-Hans-CN",
                PreserveLayout = true,
                SingleLine = false,
            };

            var result = await recognizer.RecognizeAsync(bitmap, options).ConfigureAwait(true);

            // auto 后端的判定依据，显示给用户（可解释性）
            string? autoNote = null;
            if (_registry.Auto is { } auto && string.Equals(recognizer.Id, auto.Id, StringComparison.OrdinalIgnoreCase))
            {
                var sel = auto.LastSelection;
                if (sel is not null)
                {
                    autoNote = sel.Reason;
                    if (sel.QualityWarning is not null)
                    {
                        autoNote += "｜" + sel.QualityWarning;
                    }
                }
            }

            panel.SetResult(result, autoNote);
            _log?.Info($"[OCR] 完成：{result.Lines.Count} 行 / {result.PlainText.Length} 字，" +
                       $"{result.Elapsed.TotalMilliseconds:F0} ms（引擎 {result.EngineId}）");
        }
        catch (Exception ex)
        {
            _log?.Error("[OCR] 识别失败", ex);
            panel.SetError(ex is OcrUnavailableException oue ? oue.InstallGuide : ex.Message);
        }
        finally
        {
            bitmap.Dispose();
        }
    }
}

/// <summary>「翻译」按钮。</summary>
internal sealed class TranslateButton : IToolbarItem
{
    private readonly AiServiceRegistry _registry;
    private readonly IAppLog? _log;

    public TranslateButton(AiServiceRegistry registry, IAppLog? log)
    {
        _registry = registry;
        _log = log;
    }

    public string Id => "ai.translate";

    public ToolbarSlot Slot => ToolbarSlot.Right;

    public int Order => 100;

    public string Tooltip => "翻译";

    public ToolbarGlyph Glyph => ToolbarGlyph.Translate;

    public bool IsToggle => false;

    public bool IsChecked => false;

    /// <summary>翻译后端未配置时**隐藏**（不是灰显）—— 避免用户点了才发现没装模型。</summary>
    public bool IsVisible(ToolbarContext context) => true;

    public bool IsEnabled(ToolbarContext context) => true;

    public void OnClick(ToolbarContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var factory = _registry.Translations2;
        var provider = factory?.ResolveActiveProvider();

        var panel = new TranslateResultPanel(DpiHelper.ScaleFactor(context.Toolbar.Dpi));

        if (factory is null || provider is null)
        {
            // 未配置：显示引导而不是报错
            var status = factory?.DescribeStatus();
            panel.SetNotConfigured(
                status?.Summary ?? "翻译后端未启用",
                status?.ActionHint ?? "在设置中启用本地 NLLB 模型或本机 Ollama。",
                status?.InstallGuide);
            context.Toolbar.ShowPanel(new ToolbarPanelRequest(panel, PanelPlacement.Right, "翻译") { PreferredSizeDip = new PhysSize(380, 460) });
            return;
        }

        var bitmap = context.SelectionBitmapProvider();
        if (bitmap is null)
        {
            context.ShowToast("还没有选区");
            bitmap?.Dispose();
            return;
        }

        context.Toolbar.ShowPanel(new ToolbarPanelRequest(panel, PanelPlacement.Right, "翻译") { PreferredSizeDip = new PhysSize(380, 460) });
        _ = RunAsync(context, bitmap, provider, panel);
    }

    private async Task RunAsync(ToolbarContext context, Bitmap bitmap, ITranslationProvider provider, TranslateResultPanel panel)
    {
        OcrResult? ocr = null;
        try
        {
            // 先 OCR 取原文，再翻译
            panel.SetBusy("正在提取文字…");
            var recognizer = _registry.ResolveDefaultRecognizer()
                             ?? throw new InvalidOperationException("没有可用的 OCR 引擎");

            ocr = await recognizer.RecognizeAsync(bitmap, new OcrOptions
            {
                LanguageTag = "zh-Hans-CN",
                PreserveLayout = false,
                SingleLine = true,
            }).ConfigureAwait(true);

            if (string.IsNullOrWhiteSpace(ocr.PlainText))
            {
                panel.SetError("选区内没有识别到文字。");
                return;
            }

            var sourceCode = NllbLanguageMap.DetectSourceCode(ocr.PlainText);
            var targetCode = NllbLanguageMap.OppositeOf(sourceCode);

            // NLLB 首次加载 2.3 s + 1.6 GB，必须给明确的 loading 文案
            panel.SetBusy("正在翻译，首次使用可能需要几秒…");

            var translated = await provider.TranslateAsync(
                ocr.PlainText,
                NllbLanguageMap.ToBcp47(sourceCode),
                NllbLanguageMap.ToBcp47(targetCode)).ConfigureAwait(true);

            panel.SetResult(ocr.PlainText, translated,
                NllbLanguageMap.DisplayName(sourceCode), NllbLanguageMap.DisplayName(targetCode),
                provider.DisplayName);
        }
        catch (Exception ex)
        {
            _log?.Error("[翻译] 失败", ex);
            var hint = ex.Message;
            if (ex is NllbNotInstalledException nie)
            {
                hint = nie.InstallGuide;
            }

            panel.SetError(hint);
        }
        finally
        {
            bitmap.Dispose();
            ocr = null;
        }
    }
}

/// <summary>OCR 结果面板：可选中多行文本 + 复制 / 追加到剪贴板。</summary>
internal sealed class OcrResultPanel : UserControl
{
    private readonly TextBox _text;
    private readonly Label _status;
    private readonly Button _copy;
    private readonly Button _append;
    private readonly Button _selectAll;

    public OcrResultPanel(string engineName, double scale = 1)
    {
        AutoScaleMode = AutoScaleMode.None;
        Width = (int)(360 * scale);
        Height = (int)(420 * scale);
        Font = new Font("Microsoft YaHei UI", (float)(14 * scale), FontStyle.Regular, GraphicsUnit.Pixel);

        // ★ 先建 TextBox 再建按钮：按钮的 Click lambda 捕获 _text，
        //   若 _text 在按钮之后赋值，编译器会报 CS8602（虽然运行时安全）。
        _text = new TextBox
        {
            Multiline = true,
            ScrollBars = ScrollBars.Vertical,
            Dock = DockStyle.Fill,
            ReadOnly = false,          // 允许用户改错字（OCR 有形近字误识）
            Font = Font,
            BorderStyle = BorderStyle.None,
        };

        _status = new Label
        {
            Text = $"识别中…（{engineName}）",
            Dock = DockStyle.Top,
            Height = (int)(48 * scale),
            AutoEllipsis = true,
            Padding = new Padding(8, 6, 8, 6),
            ForeColor = Color.FromArgb(76, 76, 76),
        };

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = (int)(44 * scale),
            Padding = new Padding(6, 4, 6, 4),
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
        };

        _copy = MakeButton("复制", () =>
        {
            if (!string.IsNullOrEmpty(_text.Text))
            {
                TextClipboard.Write(_text.Text);
            }
        });

        _append = MakeButton("追加到剪贴板", () =>
        {
            if (string.IsNullOrEmpty(_text.Text))
            {
                return;
            }

            var existing = TextClipboard.Read();
            TextClipboard.Write(string.IsNullOrEmpty(existing) ? _text.Text : existing + Environment.NewLine + _text.Text);
        });

        _selectAll = MakeButton("全选", () => { _text.Focus(); _text.SelectAll(); });

        buttons.Controls.Add(_copy);
        buttons.Controls.Add(_append);
        buttons.Controls.Add(_selectAll);

        Controls.Add(_text);
        Controls.Add(_status);
        Controls.Add(buttons);
    }

    private Button MakeButton(string text, Action onClick)
    {
        var b = new Button { Text = text, AutoSize = true, Height = 28, Margin = new Padding(4, 0, 0, 0) };
        b.Click += (_, _) =>
        {
            try { onClick(); }
            catch (Exception ex) { _status.Text = "操作失败：" + ex.Message; }
        };
        return b;
    }

    public void SetResult(OcrResult result, string? autoNote)
    {
        if (IsDisposed) return;
        _text.Text = result.PlainText.Replace("\r\n", "\n").Replace("\n", Environment.NewLine);
        _status.Text = $"{result.Lines.Count} 行 / {result.PlainText.Length} 字，" +
                       $"{result.Elapsed.TotalMilliseconds:F0} ms";
        _status.ForeColor = Color.FromArgb(76, 76, 76);
    }

    public void SetError(string message)
    {
        if (IsDisposed) return;
        _text.Text = string.Empty;
        _status.Text = "识别失败";
        _status.ForeColor = Color.FromArgb(220, 38, 38);
        // 错误详情放进文本框，用户可以复制去搜
        _text.Text = message.Replace("\n", Environment.NewLine);
    }
}

/// <summary>翻译面板：原文/译文双栏 + 未配置时的引导。</summary>
internal sealed class TranslateResultPanel : UserControl
{
    private readonly TextBox _source;
    private readonly TextBox _target;
    private readonly Label _status;
    private readonly SplitContainer _split;

    public TranslateResultPanel(double scale = 1)
    {
        AutoScaleMode = AutoScaleMode.None;
        Width = (int)(380 * scale);
        Height = (int)(460 * scale);
        Font = new Font("Microsoft YaHei UI", (float)(14 * scale), FontStyle.Regular, GraphicsUnit.Pixel);

        _status = new Label
        {
            Dock = DockStyle.Top,
            Height = (int)(56 * scale),
            AutoEllipsis = true,
            Padding = new Padding(8, 6, 8, 6),
            ForeColor = Color.FromArgb(76, 76, 76),
        };

        _source = MakeBox();
        _target = MakeBox();
        _source.Font = Font;
        _target.Font = Font;

        _split = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Horizontal,
        };
        _split.Panel1.Controls.Add(Wrap("原文", _source, scale));
        _split.Panel2.Controls.Add(Wrap("译文", _target, scale));

        var copy = new Button { Text = "复制译文", Dock = DockStyle.Bottom, Height = (int)(40 * scale) };
        copy.Click += (_, _) =>
        {
            try { if (!string.IsNullOrWhiteSpace(_target.Text)) TextClipboard.Write(_target.Text); }
            catch (Exception ex) { _status.Text = ex.Message; }
        };

        Controls.Add(_split);
        Controls.Add(_status);
        Controls.Add(copy);
    }

    private static TextBox MakeBox() => new()
    {
        Multiline = true,
        ScrollBars = ScrollBars.Vertical,
        Dock = DockStyle.Fill,
        Font = new Font("Microsoft YaHei UI", 10f),
        BorderStyle = BorderStyle.None,
        ReadOnly = false,
    };

    private static Control Wrap(string title, TextBox box, double scale)
    {
        var host = new Panel { Dock = DockStyle.Fill };
        var label = new Label
        {
            Text = title,
            Dock = DockStyle.Top,
            Height = (int)(26 * scale),
            Padding = new Padding(6, 2, 6, 0),
            ForeColor = Color.FromArgb(120, 120, 120),
        };
        host.Controls.Add(box);
        host.Controls.Add(label);
        return host;
    }

    public void SetBusy(string message)
    {
        if (IsDisposed) return;
        _status.Text = message;
        _status.ForeColor = Color.FromArgb(76, 76, 76);
    }

    public void SetResult(string source, string target, string fromLang, string toLang, string providerName)
    {
        if (IsDisposed) return;
        _source.Text = source.Replace("\n", Environment.NewLine);
        _target.Text = target.Replace("\n", Environment.NewLine);
        _status.Text = $"{fromLang} → {toLang}｜{providerName}";
        _status.ForeColor = Color.FromArgb(0, 150, 90);
    }

    public void SetError(string message)
    {
        if (IsDisposed) return;
        _status.Text = "翻译失败";
        _status.ForeColor = Color.FromArgb(220, 38, 38);
        _target.Text = message.Replace("\n", Environment.NewLine);
    }

    /// <summary>未配置翻译后端：显示"未启用"+ 怎么配，而不是报错。</summary>
    public void SetNotConfigured(string summary, string actionHint, string? installGuide)
    {
        if (IsDisposed) return;
        _status.Text = summary + "｜" + actionHint;
        _status.ForeColor = Color.FromArgb(180, 120, 0);
        _target.Text = (installGuide ?? actionHint).Replace("\n", Environment.NewLine);
    }
}
