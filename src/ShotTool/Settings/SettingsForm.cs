using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;
using ShotTool.Core;
using ShotTool.Ai.Translation;

namespace ShotTool.Settings;

/// <summary>
/// 设置页（托盘菜单「设置…」打开）。
///
/// <para>
/// 只改本层拥有的持久化字段：<see cref="ShotToolOptions"/> 的 App 级字段 +
/// <c>sections.ai</c>（<see cref="AiOptions"/>）。视觉/标注相关的默认值归 dev-editor，
/// 不在此页暴露。
/// </para>
/// <para>
/// 页面里必须保留两段文案（产品要求，不要删）：
/// 翻译模型许可（CC-BY-NC-4.0，仅非商业 + 署名，商用替代方案）与
/// 合规声明（不包含/不提取/不分发腾讯任何资源）。
/// </para>
/// </summary>
public sealed partial class SettingsForm : Form
{
    private readonly ISettingsStore _store;
    private readonly AiOptions _ai;
    private readonly Action? _onApplied;

    private readonly TextBox _hotkeyText = new();
    private readonly TextBox _saveDirText = new();
    private readonly ComboBox _formatCombo = new();
    private readonly CheckBox _showMagnifier = new();
    private readonly CheckBox _closeAfterCopy = new();
    private readonly CheckBox _copyOnSave = new();
    private readonly CheckBox _startup = new();
    private readonly CheckBox _trayNotify = new();
    private readonly ComboBox _ocrCombo = new();
    private readonly ComboBox _translationCombo = new();
    private readonly TextBox _nllbDirText = new();

    /// <summary>
    /// 创建设置页。
    /// </summary>
    /// <param name="store">设置仓库。</param>
    /// <param name="onApplied">保存成功后的回调（dev-capture 用来立即重注册热键 / 自启项）。</param>
    public SettingsForm(ISettingsStore store, Action? onApplied = null)
        : this(store, onApplied, null)
    {
    }

    internal SettingsForm(ISettingsStore store, Action? onApplied, uint? dpiOverride)
    {
        _layoutDpiOverride = dpiOverride;
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _onApplied = onApplied;
        _ai = AiOptions.ReadFrom(store);

        ConfigureWindow();
        BuildLayout();
        LoadValues();
        RefreshAiHelp();
    }

    // ==================================================================
    // 布局
    // ==================================================================

    private void LoadValues()
    {
        ShotToolOptions app = _store.App;

        _hotkeyText.Text = app.CaptureHotkey.IsEmpty ? string.Empty : app.CaptureHotkey.ToString();
        _saveDirText.Text = app.SaveDirectory;
        _formatCombo.SelectedIndex = Clamp((int)app.SaveFormat, _formatCombo.Items.Count);
        _showMagnifier.Checked = app.ShowMagnifier;
        _closeAfterCopy.Checked = app.CloseAfterCopy;
        _copyOnSave.Checked = app.CopyOnSave;

        // 自启以注册表真实状态为准（用户可能在任务管理器里手动禁用过）。
        _startup.Checked = StartupRegistration.IsEnabled() ?? app.StartupWithWindows;
        _startupStatus.Text = StartupRegistration.ReadStatus().Description;
        _trayNotify.Checked = app.ShowTrayNotifications;

        _ocrCombo.SelectedIndex = Clamp((int)_ai.OcrBackend, _ocrCombo.Items.Count);
        _translationCombo.SelectedIndex = Clamp((int)_ai.TranslationBackend, _translationCombo.Items.Count);
        _nllbDirText.Text = _ai.NllbModelDirectory;
    }

    private void OnSaveClicked(object? sender, EventArgs e)
    {
        if (!HotkeyGesture.TryParse(_hotkeyText.Text, out HotkeyGesture gesture) || gesture.IsEmpty)
        {
            MessageBox.Show(
                this,
                "热键格式无效。\r\n示例：Alt+A、Ctrl+Shift+S、F3。",
                "热键",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            _hotkeyText.Focus();
            _hotkeyText.SelectAll();
            return;
        }

        ShotToolOptions app = _store.App;
        app.CaptureHotkey = gesture;
        app.SaveDirectory = _saveDirText.Text.Trim();
        app.SaveFormat = (ImageFileFormat)_formatCombo.SelectedIndex;
        app.ShowMagnifier = _showMagnifier.Checked;
        app.CloseAfterCopy = _closeAfterCopy.Checked;
        app.CopyOnSave = _copyOnSave.Checked;
        app.StartupWithWindows = _startup.Checked;
        app.ShowTrayNotifications = _trayNotify.Checked;

        _ai.OcrBackend = (OcrBackend)_ocrCombo.SelectedIndex;
        _ai.TranslationBackend = (TranslationBackend)_translationCombo.SelectedIndex;
        _ai.NllbModelDirectory = string.IsNullOrWhiteSpace(_nllbDirText.Text)
            ? AiOptions.DefaultNllbModelDirectory
            : _nllbDirText.Text.Trim();

        try
        {
            AiOptions.WriteTo(_store, _ai);
            _store.Save();
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                this,
                $"保存配置失败：{ex.Message}",
                "ShotTool",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
            return;
        }

        // 自启是副作用：失败时提示，但不回滚已保存的其它设置。
        string? startupError = null;
        try
        {
            StartupRegistration.Apply(_startup.Checked);
        }
        catch (Exception ex)
        {
            startupError = ex.Message;
        }

        if (startupError is not null)
        {
            MessageBox.Show(
                this,
                $"设置已保存，但开机自启登记失败：{startupError}\r\n请检查系统启动设置或任务计划程序中的 ShotTool 登录任务。",
                "ShotTool",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }

        _onApplied?.Invoke();
        DialogResult = DialogResult.OK;
        Close();
    }

    /// <summary>
    /// 打开 NLLB 模型目录；目录不存在时先创建空目录再打开（不弹错误）。
    /// 以输入框当前内容为准（即使还没点「保存」），留空则用默认口径。
    /// </summary>
    private void OnOpenModelDirectoryClicked(object? sender, EventArgs e)
    {
        try
        {
            string raw = _nllbDirText.Text.Trim();
            if (string.IsNullOrWhiteSpace(raw))
            {
                raw = AiOptions.DefaultNllbModelDirectory;
            }

            var locator = new NllbModelLocator(raw);
            string directory = locator.Inspect().IsReady
                ? locator.ActiveDirectory : Environment.ExpandEnvironmentVariables(raw);
            Directory.CreateDirectory(directory); // 不存在 → 建空目录（用户往里放两个 onnx）

            Process.Start(new ProcessStartInfo
            {
                FileName = directory,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                this,
                $"打开模型目录失败：{ex.Message}",
                "ShotTool",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
    }

    private void OnOpenConfigClicked(object? sender, EventArgs e)
    {        string? directory = Path.GetDirectoryName(_store.FilePath);
        if (string.IsNullOrEmpty(directory))
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(directory);
            Process.Start(new ProcessStartInfo
            {
                FileName = directory,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"打开配置目录失败：{ex.Message}", "ShotTool", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void BrowseFolder(TextBox target)
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "选择目录",
            UseDescriptionForTitle = true,
            SelectedPath = Directory.Exists(Environment.ExpandEnvironmentVariables(target.Text))
                ? Environment.ExpandEnvironmentVariables(target.Text)
                : Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
            ShowNewFolderButton = true,
        };

        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            target.Text = dialog.SelectedPath;
        }
    }

    // ==================================================================
    // 布局辅助
    // ==================================================================

    private static int Clamp(int value, int count)
    {
        if (count <= 0)
        {
            return 0;
        }

        return Math.Clamp(value, 0, count - 1);
    }
}
