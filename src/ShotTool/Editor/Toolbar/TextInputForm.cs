using System.Runtime.InteropServices;
using ShotTool.Core;
using ShotTool.Editor.Algorithms;
using ShotTool.Editor.Model;

namespace ShotTool.Editor.Toolbar;

/// <summary>Native IME-aware transparent rich edit over the frozen screenshot, no white plate.</summary>
public sealed class TextInputForm : Form
{
    private readonly TransparentEditor _editor;
    private readonly Font _font;
    private readonly Bitmap? _background;
    private readonly Point _backgroundOffset;
    private readonly Size _limit;
    private readonly int _inset;
    private bool _committed;
    private bool _composing;
    internal bool DiagnosticMode { get; set; }
    protected override bool ShowWithoutActivation => DiagnosticMode;

    internal TextInputForm(string initial, double fontSizePhys, Color color, PhysRect available,
        double dpiScale, Bitmap? background, PhysPoint backgroundOrigin)
    {
        _font = TextMeasurer.CreateFont(TextAnnotation.DefaultFontFamily, fontSizePhys);
        _background = background; // borrowed for modal editor lifetime
        _backgroundOffset = new Point(backgroundOrigin.X - available.X, backgroundOrigin.Y - available.Y);
        _inset = Math.Max(1, (int)Math.Round(dpiScale));
        _limit = new Size(Math.Max(16, available.Width), Math.Max(16, available.Height));
        AutoScaleMode = AutoScaleMode.None;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        BackColor = Color.FromArgb(35, 39, 47);
        Location = new Point(available.X - _inset, available.Y - _inset);
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
        _editor = new TransparentEditor(this)
        {
            Multiline = true, AcceptsTab = false, BorderStyle = BorderStyle.None,
            ScrollBars = RichTextBoxScrollBars.None, WordWrap = false,
            DetectUrls = false, ShortcutsEnabled = true, Font = _font,
            ForeColor = color, BackColor = BackColor, Text = initial,
            ImeMode = ImeMode.On, HideSelection = false,
            Location = new Point(_inset, _inset)
        };
        _editor.KeyDown += OnEditorKeyDown;
        _editor.TextChanged += (_, _) => ResizeEditor();
        Controls.Add(_editor);
        ResizeEditor();
        Deactivate += (_, _) => { if (!DiagnosticMode) Commit(); };
        Shown += (_, _) => { ResizeEditor(); if (!DiagnosticMode) { _editor.Focus(); _editor.SelectionStart = _editor.TextLength; } };
    }

    public string? Result { get; private set; }
    public static string? Edit(IWin32Window? owner, PhysRect boundsPhys, double fontSizePhys,
        uint argb, string? initial, double dpiScale, Bitmap? background = null, PhysPoint backgroundOrigin = default)
    {
        using var form = new TextInputForm(initial ?? string.Empty, fontSizePhys,
            Color.FromArgb(unchecked((int)argb)), boundsPhys, dpiScale, background, backgroundOrigin);
        if (owner is not null) form.ShowDialog(owner); else form.ShowDialog();
        return form.Result;
    }

    private void ResizeEditor()
    {
        var size = TextMeasurer.Measure(string.IsNullOrEmpty(_editor.Text) ? "输入文字" : _editor.Text + " ",
            _font.Size, _font.FontFamily.Name);
        int width = Math.Min(_limit.Width, Math.Max((int)(120 * _font.Size / 18), (int)size.Width + 12));
        int height = Math.Min(_limit.Height, Math.Max((int)Math.Ceiling(_font.GetHeight()) + 6, (int)size.Height + 8));
        if (_editor.IsHandleCreated)
        {
            int span = _editor.GetPositionFromCharIndex(_editor.TextLength).Y - _editor.GetPositionFromCharIndex(0).Y;
            height = Math.Min(_limit.Height, Math.Max(height, span + (int)Math.Ceiling(_font.GetHeight()) + 14));
        }
        ClientSize = new Size(width + _inset * 2, height + _inset * 2);
        _editor.Size = new Size(width, height);
        Invalidate(true);
    }
    protected override void OnPaintBackground(PaintEventArgs e) => PaintBackdrop(e.Graphics, Point.Empty);
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        using var border = new Pen(Color.FromArgb(0, 195, 117), _inset);
        e.Graphics.DrawRectangle(border, 0, 0, ClientSize.Width - 1, ClientSize.Height - 1);
    }
    private void PaintBackdrop(Graphics graphics, Point childOffset)
    {
        graphics.Clear(BackColor);
        if (_background is not null)
            graphics.DrawImageUnscaled(_background, _backgroundOffset.X + _inset - childOffset.X,
                _backgroundOffset.Y + _inset - childOffset.Y);
    }
    private void OnEditorKeyDown(object? sender, KeyEventArgs e)
    {
        if (_composing || e.KeyCode is Keys.ProcessKey or Keys.Packet) return;
        if (e.KeyCode == Keys.Enter && !e.Shift)
        { e.SuppressKeyPress = true; Commit(); }
        else if (e.KeyCode == Keys.Escape)
        { e.SuppressKeyPress = true; _committed = true; Result = null; DialogResult = DialogResult.Cancel; Close(); }
    }
    private void Commit()
    {
        if (_committed) return;
        _committed = true;
        Result = _editor.Text.Replace("\r\n", "\n");
        DialogResult = DialogResult.OK; Close();
    }
    protected override void Dispose(bool disposing)
    { base.Dispose(disposing); if (disposing) _font.Dispose(); }

    private sealed class TransparentEditor : RichTextBox
    {
        private readonly TextInputForm _host;
        internal TransparentEditor(TextInputForm host) => _host = host;
        protected override CreateParams CreateParams
        { get { var p = base.CreateParams; p.ExStyle |= 0x20; return p; } }
        protected override void WndProc(ref Message m)
        {
            if (m.Msg == 0x10D) _host._composing = true; // WM_IME_STARTCOMPOSITION
            if (m.Msg == 0x10E) _host._composing = false;
            if (m.Msg == 0x14 && m.WParam != IntPtr.Zero)
            {
                using var graphics = Graphics.FromHdc(m.WParam);
                _host.PaintBackdrop(graphics, Location);
                m.Result = new IntPtr(1); return;
            }
            base.WndProc(ref m);
        }
    }

    internal void SetDiagnosticText(string text) => _editor.Text = text;
    internal bool DiagnosticTextFits => _editor.GetPositionFromCharIndex(_editor.TextLength).Y
        - _editor.GetPositionFromCharIndex(0).Y + _font.GetHeight() + 4 <= _editor.Height;
    internal void CommitForDiagnostics() => Commit();
}
