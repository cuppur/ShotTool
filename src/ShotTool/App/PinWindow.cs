using System.Drawing.Drawing2D;
using ShotTool.Core;

namespace ShotTool.App;

/// <summary>独立贴图窗口，拥有成品图副本，生命周期不依赖截图编辑窗口。</summary>
internal sealed class PinWindow : Form
{
    private static readonly HashSet<PinWindow> OpenPins = new();
    private readonly Bitmap _image;
    private readonly IOutputSink _output;
    private readonly IAppLog _log;
    private readonly ContextMenuStrip _menu;
    private Point _dragStart;
    private Point _dragLocation;
    private bool _dragging;
    private double _zoom = 1;
    private readonly bool _diagnostic;

    private PinWindow(Bitmap image, PhysRect selection, IOutputSink output, IAppLog log, bool diagnostic)
    {
        _image = (Bitmap)image.Clone();
        _output = output;
        _log = log;
        _diagnostic = diagnostic;
        AutoScaleMode = AutoScaleMode.None;
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        TopMost = true;
        KeyPreview = true;
        Text = "ShotTool 固定截图";
        AccessibleName = "固定截图";
        Bounds = selection.ToRectangle();
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint
            | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        _menu = new ContextMenuStrip();
        _menu.Items.Add("复制图片（Ctrl+C）", null, async (_, _) => await CopyAsync());
        _menu.Items.Add("另存为…（Ctrl+S）", null, async (_, _) => await SaveAsync());
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add("恢复原始大小（双击）", null, (_, _) => ResetZoom());
        _menu.Items.Add("关闭（Esc）", null, (_, _) => Close());
        ContextMenuStrip = _menu;
    }

    protected override bool ShowWithoutActivation => true;
    protected override CreateParams CreateParams
    {
        get { var p = base.CreateParams; p.ExStyle |= 0x80; return p; }
    }

    internal static PinWindow ShowPinned(Bitmap image, PhysRect selection, IOutputSink output, IAppLog log, bool diagnostic = false)
    {
        var pin = new PinWindow(image, selection, output, log, diagnostic);
        OpenPins.Add(pin);
        pin.FormClosed += (_, _) => OpenPins.Remove(pin);
        if (diagnostic) pin.Location = new Point(-20000, -20000);
        try { pin.Show(); }
        catch { OpenPins.Remove(pin); pin.Dispose(); throw; }
        log.Info($"已固定截图：{image.Width}x{image.Height}，当前 {OpenPins.Count} 张。");
        return pin;
    }

    internal static void CloseAll()
    {
        foreach (var pin in OpenPins.ToArray()) pin.Close();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.InterpolationMode = _zoom >= 1 ? InterpolationMode.NearestNeighbor : InterpolationMode.HighQualityBicubic;
        e.Graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
        e.Graphics.DrawImage(_image, ClientRectangle);
        using var border = new Pen(Color.FromArgb(140, 80, 80, 80));
        e.Graphics.DrawRectangle(border, 0, 0, Math.Max(0, Width - 1), Math.Max(0, Height - 1));
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left) return;
        _dragStart = PointToScreen(e.Location);
        _dragLocation = Location;
        _dragging = true;
        Capture = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        Cursor = Cursors.SizeAll;
        if (!_dragging) return;
        var p = PointToScreen(e.Location);
        Location = new Point(_dragLocation.X + p.X - _dragStart.X, _dragLocation.Y + p.Y - _dragStart.Y);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        _dragging = false;
        Capture = false;
    }

    protected override void OnMouseDoubleClick(MouseEventArgs e)
    {
        base.OnMouseDoubleClick(e);
        if (e.Button == MouseButtons.Left) ResetZoom();
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        ZoomAt(e.Location, _zoom * Math.Pow(1.1, e.Delta / 120.0));
    }

    internal void ZoomAt(Point anchor, double zoom)
    {
        double previous = _zoom;
        _zoom = Math.Clamp(zoom, 0.1, Math.Min(4, 16384.0 / Math.Max(_image.Width, _image.Height)));
        var location = PointToScreen(anchor);
        var size = new Size(Math.Max(1, (int)Math.Round(_image.Width * _zoom)), Math.Max(1, (int)Math.Round(_image.Height * _zoom)));
        Bounds = new Rectangle(location.X - (int)Math.Round(anchor.X * _zoom / previous),
            location.Y - (int)Math.Round(anchor.Y * _zoom / previous), size.Width, size.Height);
    }

    internal void ResetZoom() => ZoomAt(new Point(Width / 2, Height / 2), 1);

    internal async Task CopyAsync()
    {
        try
        {
            using var bitmap = (Bitmap)_image.Clone();
            await _output.CopyToClipboardAsync(bitmap);
        }
        catch (Exception ex) { ReportError(ex); }
    }

    private async Task SaveAsync()
    {
        try
        {
            using var bitmap = (Bitmap)_image.Clone();
            await _output.SaveAsync(bitmap, new SaveRequest { PromptUser = true });
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { ReportError(ex); }
    }

    private void ReportError(Exception ex)
    {
        _log.Error("贴图输出失败", ex);
        if (!_diagnostic) MessageBox.Show(this, ex.Message, "贴图输出失败", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        switch (keyData)
        {
            case Keys.Escape:
            case Keys.Delete: Close(); return true;
            case Keys.Control | Keys.C: _ = CopyAsync(); return true;
            case Keys.Control | Keys.S: _ = SaveAsync(); return true;
            case Keys.Control | Keys.D0: ResetZoom(); return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { OpenPins.Remove(this); _menu.Dispose(); _image.Dispose(); }
        base.Dispose(disposing);
    }
}
