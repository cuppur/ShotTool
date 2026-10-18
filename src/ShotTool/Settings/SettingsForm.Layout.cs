using System.Drawing;
using ShotTool.Ai.Translation;

namespace ShotTool.Settings;

public sealed partial class SettingsForm
{
    private readonly TabControl _pages = new();
    private readonly Label _ocrHelp = new();
    private readonly Label _translationHelp = new();
    private readonly Label _modelStatus = new();
    private float _layoutScale = 1;
    private readonly uint? _layoutDpiOverride;
    internal bool DiagnosticMode { get; set; }
    protected override bool ShowWithoutActivation => DiagnosticMode;

    private int Px(float value) => (int)Math.Round(value * _layoutScale);
    private Padding Pad(int all) => new(Px(all));

    private void ConfigureWindow()
    {
        // 所有尺寸从 DIP 一次换算，禁止再与 WinForms 自动缩放叠加。
        _layoutScale = (_layoutDpiOverride ?? (uint)DeviceDpi) / 96f;
        AutoScaleMode = AutoScaleMode.None;
        Font = new Font("Microsoft YaHei UI", Px(14), FontStyle.Regular, GraphicsUnit.Pixel);
        Text = "ShotTool 设置";
        BackColor = Color.White;
        FormBorderStyle = FormBorderStyle.Sizable;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        var workArea = Screen.FromPoint(Cursor.Position).WorkingArea;
        var ideal = SizeFromClientSize(new Size(Px(720), Px(600)));
        Size = new Size(Math.Min(ideal.Width, workArea.Width - Px(24)), Math.Min(ideal.Height, workArea.Height - Px(24)));
        MinimumSize = new Size(Math.Min(Px(560), Width), Math.Min(Px(460), Height));
        Location = new Point(workArea.X + (workArea.Width - Width) / 2, workArea.Y + (workArea.Height - Height) / 2);
    }

    private void BuildLayout()
    {
        SuspendLayout();
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Margin = Padding.Empty };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, Px(64)));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, Px(64)));
        root.Controls.Add(new Label
        {
            Text = "设置", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(Px(20), 0, 0, 0), Margin = Padding.Empty,
            Font = new Font(Font.FontFamily, Px(22), FontStyle.Regular, GraphicsUnit.Pixel)
        }, 0, 0);
        _pages.Dock = DockStyle.Fill;
        _pages.Margin = new Padding(Px(16), 0, Px(16), Px(8));
        _pages.Font = Font;
        _pages.ItemSize = new Size(Px(126), Px(34));
        _pages.SizeMode = TabSizeMode.Fixed;
        root.Controls.Add(_pages, 0, 1);
        BuildCapturePage();
        BuildAiPage();
        BuildResidentPage();
        BuildAboutPage();
        root.Controls.Add(BuildButtons(), 0, 2);
        Controls.Add(root);
        ResumeLayout(true);
    }

    private TableLayoutPanel NewPage(string title)
    {
        var page = new TabPage(title) { BackColor = Color.White, AutoScroll = true, Padding = Pad(18) };
        _pages.TabPages.Add(page);
        var grid = new TableLayoutPanel
        {
            Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2, RowCount = 0,
            Margin = Padding.Empty, Padding = Padding.Empty, GrowStyle = TableLayoutPanelGrowStyle.AddRows
        };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, Px(104)));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        page.Controls.Add(grid);
        return grid;
    }

    private void AddField(TableLayoutPanel grid, string name, Control control, int height = 40)
    {
        int row = grid.RowCount++;
        grid.RowStyles.Add(new RowStyle(SizeType.Absolute, Px(height)));
        grid.Controls.Add(new Label
        {
            Text = name, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft,
            AutoSize = false, Margin = new Padding(0, 0, Px(12), 0)
        }, 0, row);
        control.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        control.Margin = new Padding(0, Px(4), 0, Px(4));
        control.AccessibleName = name;
        grid.Controls.Add(control, 1, row);
    }

    private void AddWide(TableLayoutPanel grid, Control control, int height)
    {
        int row = grid.RowCount++;
        grid.RowStyles.Add(new RowStyle(SizeType.Absolute, Px(height)));
        control.Dock = DockStyle.Fill;
        control.Margin = Padding.Empty;
        grid.Controls.Add(control, 0, row);
        grid.SetColumnSpan(control, 2);
    }

    private void AddHint(TableLayoutPanel grid, string text, int height = 38, Label? existing = null)
    {
        var label = existing ?? new Label();
        label.Text = text;
        label.ForeColor = Color.FromArgb(100, 100, 100);
        label.Padding = new Padding(0, Px(4), 0, Px(4));
        label.AutoSize = false;
        AddWide(grid, label, height);
    }

    private void AddCheck(TableLayoutPanel grid, CheckBox check, string text)
    {
        check.Text = text;
        check.AutoSize = true;
        check.Anchor = AnchorStyles.Left;
        check.AccessibleName = text;
        int row = grid.RowCount++;
        grid.RowStyles.Add(new RowStyle(SizeType.Absolute, Px(36)));
        check.Margin = Padding.Empty;
        grid.Controls.Add(check, 0, row);
        grid.SetColumnSpan(check, 2);
    }

    private void AddSection(TableLayoutPanel grid, string title)
        => AddWide(grid, new Label { Text = title, TextAlign = ContentAlignment.BottomLeft,
            Padding = new Padding(0, 0, 0, Px(6)), ForeColor = Color.FromArgb(45, 45, 45) }, 34);

    private Control DirectoryField(TextBox box, bool showOpen)
    {
        var panel = new TableLayoutPanel { ColumnCount = showOpen ? 3 : 2, RowCount = 1, Height = Px(36), Margin = Padding.Empty };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, Px(78)));
        if (showOpen) panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, Px(66)));
        box.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        box.Margin = new Padding(0, 0, Px(8), 0);
        panel.Controls.Add(box, 0, 0);
        var browse = new Button { Text = "浏览…", Dock = DockStyle.Fill, Margin = new Padding(0, 0, Px(6), 0) };
        browse.Click += (_, _) => BrowseFolder(box);
        panel.Controls.Add(browse, 1, 0);
        if (showOpen)
        {
            var open = new Button { Text = "打开", Dock = DockStyle.Fill, Margin = Padding.Empty };
            open.Click += OnOpenModelDirectoryClicked;
            panel.Controls.Add(open, 2, 0);
        }
        return panel;
    }

    private void BuildCapturePage()
    {
        var grid = NewPage("截图与保存");
        AddField(grid, "全局快捷键", _hotkeyText);
        AddHint(grid, "例如 Alt+A 或 Ctrl+Shift+S；用于开始和取消截图。", 32);
        AddCheck(grid, _showMagnifier, "框选时显示像素放大镜");
        AddCheck(grid, _closeAfterCopy, "复制后关闭截图界面");
        AddSection(grid, "保存");
        AddField(grid, "保存位置", DirectoryField(_saveDirText, false));
        AddHint(grid, "留空时保存到“图片”文件夹中的 ShotTool 目录。", 32);
        _formatCombo.DropDownStyle = ComboBoxStyle.DropDownList;
        _formatCombo.Items.AddRange(new object[] { "PNG（无损，推荐）", "JPEG（体积较小）", "BMP" });
        AddField(grid, "图片格式", _formatCombo);
        AddCheck(grid, _copyOnSave, "保存时同时复制到剪贴板");
    }

    private void BuildAiPage()
    {
        var grid = NewPage("文字与翻译");
        _ocrCombo.DropDownStyle = ComboBoxStyle.DropDownList;
        _ocrCombo.Items.AddRange(new object[] { "自动选择", "Windows 系统 OCR", "Paddle OCR（内置）" });
        AddField(grid, "文字识别", _ocrCombo);
        AddHint(grid, string.Empty, 50, _ocrHelp);
        _translationCombo.DropDownStyle = ComboBoxStyle.DropDownList;
        _translationCombo.Items.AddRange(new object[] { "关闭", "NLLB 本地离线翻译", "Ollama 本机服务" });
        AddField(grid, "翻译方式", _translationCombo);
        AddHint(grid, string.Empty, 50, _translationHelp);
        AddField(grid, "模型目录", DirectoryField(_nllbDirText, true));
        AddHint(grid, "随包模型会自动查找，通常不必修改目录。", 32);
        AddHint(grid, string.Empty, 40, _modelStatus);
        var license = new LinkLabel { Text = "查看模型许可与资源说明", TextAlign = ContentAlignment.MiddleLeft };
        license.LinkClicked += (_, _) => _pages.SelectedIndex = 3;
        AddWide(grid, license, 36);
        _ocrCombo.SelectedIndexChanged += (_, _) => RefreshAiHelp();
        _translationCombo.SelectedIndexChanged += (_, _) => RefreshAiHelp();
        _nllbDirText.TextChanged += (_, _) => RefreshAiHelp();
    }

    private void RefreshAiHelp()
    {
        _ocrHelp.Text = _ocrCombo.SelectedIndex switch
        {
            1 => "使用已安装的系统 OCR 语言包。识别效果因字体、背景和文字方向而异。",
            2 => "使用内置识别模型，适合横排文字、深色界面和代码截图。",
            _ => "根据截图明暗选择系统 OCR 或内置模型，所有识别均在本机完成。"
        };
        _translationHelp.Text = _translationCombo.SelectedIndex switch
        {
            1 => "使用本地模型翻译；首次使用时加载，空闲 5 分钟后自动卸载。",
            2 => "需要已配置的本机 Ollama 服务；模型与服务参数保存在配置文件中。",
            _ => "不加载翻译模型。截图和文字识别仍可正常使用。"
        };
        _nllbDirText.Enabled = _translationCombo.SelectedIndex == 1;
        bool ready = new NllbModelLocator(_nllbDirText.Text).Inspect().IsReady;
        _modelStatus.Text = _translationCombo.SelectedIndex == 1
            ? ready ? "本地翻译模型已就绪。" : "未找到完整模型，请选择模型所在目录。"
            : "NLLB 模型仅在选择本地离线翻译时使用。";
        _modelStatus.ForeColor = ready && _translationCombo.SelectedIndex == 1
            ? Color.FromArgb(0, 125, 75) : Color.FromArgb(100, 100, 100);
    }

    private void BuildResidentPage()
    {
        var grid = NewPage("常驻");
        AddCheck(grid, _startup, "登录 Windows 后自动启动");
        AddCheck(grid, _trayNotify, "显示托盘通知");
        AddHint(grid, "关闭设置窗口后，程序继续在系统托盘中运行。右键托盘图标可截图或退出。", 60);
        AddSection(grid, "固定截图");
        AddHint(grid, "框选后点击图钉或按 F3。贴图可拖动、滚轮缩放、双击恢复大小；Ctrl+C 复制，Esc 关闭。", 86);
    }

    private void BuildAboutPage()
    {
        var grid = NewPage("关于");
        AddSection(grid, "ShotTool 独立截图工具");
        AddHint(grid, "无需启动或登录微信。界面和交互参考微信截图，代码与图标由本项目实现；不加载或分发腾讯二进制组件、图标或模型。", 86);
        AddSection(grid, "翻译模型许可");
        AddHint(grid, "NLLB-200-distilled-600M 采用 CC-BY-NC-4.0，仅限非商业使用，并需保留署名。模型是否随包取决于发布版本；商用替代方案需另行核对许可与适配，本项目未内置。", 104);
        AddSection(grid, "资源与隐私");
        AddHint(grid, "文字识别和本地翻译在本机完成。翻译模型仅在使用时加载，空闲 5 分钟后卸载。第三方组件的来源和声明见程序旁的 licenses 文件夹。", 90);
    }

    private Control BuildButtons()
    {
        var footer = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 4, RowCount = 1,
            Padding = new Padding(Px(16), Px(12), Px(16), Px(12)),
            Margin = Padding.Empty, BackColor = Color.FromArgb(247, 247, 247)
        };
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, Px(136)));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, Px(96)));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, Px(88)));
        footer.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var config = new Button { Text = "打开配置目录", Dock = DockStyle.Fill, Margin = Padding.Empty };
        config.Click += OnOpenConfigClicked;
        footer.Controls.Add(config, 0, 0);
        var save = new Button { Text = "保存", Dock = DockStyle.Fill, Margin = Padding.Empty };
        save.Click += OnSaveClicked;
        var cancel = new Button { Text = "取消", Dock = DockStyle.Fill, DialogResult = DialogResult.Cancel,
            Margin = new Padding(0, 0, Px(8), 0) };
        footer.Controls.Add(cancel, 2, 0);
        footer.Controls.Add(save, 3, 0);
        AcceptButton = save;
        CancelButton = cancel;
        return footer;
    }
}
