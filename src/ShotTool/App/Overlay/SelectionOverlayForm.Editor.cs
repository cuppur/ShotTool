using System.Drawing;
using ShotTool.Core;
using ShotTool.Editor.Algorithms;
using ShotTool.Editor.Model;
using ShotTool.Editor.Toolbar;

namespace ShotTool.App.Overlay;

internal sealed partial class SelectionOverlayForm : IToolbarHostSurface
{
    private ToolbarHost? _editorHost;
    private ToolbarContext? _toolbarContext;
    private bool _ownedDialog;
    private bool _drawing;
    private PhysPointF _annotationStart;
    private readonly List<PhysPointF> _stroke = new();
    private Annotation? _previewAnnotation;
    private Bitmap? _annotationComposite;

    private void InitializeEditor()
    {
        _history.Changed += (_, _) =>
        {
            var undo = _toolbar.State.Items.FirstOrDefault(i => i.Id == "action.undo");
            if (undo is not null) undo.Enabled = _history.CanUndo;
            ResetAnnotationComposite();
            RaiseSessionChanged(ShotChangeKind.Annotations);
            InvalidateOverlay(_lastFootprint);
        };
        if (_ctx.ToolbarExtensions is null || _ctx.Output is null) return;
        _editorHost = new ToolbarHost(this, _toolbar.State, _ctx.ToolbarExtensions, () => _toolbarContext);
        _toolbarContext = new ToolbarContext(this, _ctx.Output, this, _editorHost,
            () => ComposeSelectionBitmap(true), ShowToast, Cancel);
        _editorHost.Rebuild();
    }

    private void InvokeExtension(string id)
    {
        var item = _toolbar.State.Items.FirstOrDefault(i => i.Id == id)?.Extension;
        if (item is null || _toolbarContext is null) return;
        try { if (item.IsEnabled(_toolbarContext)) item.OnClick(_toolbarContext); }
        catch (Exception ex) { _ctx.Log.Error("截图扩展执行失败。", ex); ShowToast(ex.Message); }
    }

    internal ToolbarPanelHost? InvokeExtensionForDiagnostics(string id)
    {
        InvokeExtension(id);
        return _editorHost?.CurrentPanel;
    }

    internal void SaveToolbarForDiagnostics(string path)
    {
        _toolbar.Layout(_selection, _virtualBounds, _dpi);
        using var canvas = new Bitmap(_virtualBounds.Width, _virtualBounds.Height);
        using (var graphics = Graphics.FromImage(canvas)) _toolbar.Paint(graphics, _dpi);
        using var cropped = canvas.Clone(_toolbar.Bounds.ToRectangle(), canvas.PixelFormat);
        cropped.Save(path, System.Drawing.Imaging.ImageFormat.Png);
    }

    private PhysPointF AnnotationPoint()
    {
        PhysRect sel = _selection!.Value;
        return new PhysPointF(Math.Clamp(_cursorVirtual.X - sel.X, 0, sel.Width - 1),
            Math.Clamp(_cursorVirtual.Y - sel.Y, 0, sel.Height - 1));
    }

    private bool BeginAnnotation()
    {
        if (_selection is not { } sel || !sel.Contains(_cursorVirtual) || _toolbar.ActiveTool is not { } kind)
            return false;
        // 控制柄始终可用；选择工具后，选区内部交给画笔。
        var hit = SelectionGeometry.HitTest(sel, _cursorVirtual, _metrics.HandleHitPx);
        if (hit is not (SelectionHandle.Body or SelectionHandle.None)) return false;
        _annotationStart = AnnotationPoint();
        var style = new AnnotationStyle(_toolbar.State.ActiveColorArgb, _toolbar.State.Brush);
        if (kind == AnnotationKind.Text)
        {
            double size = EditorDefaults.TextSizeToPhys(_toolbar.State.TextSize, _metrics.DpiScale);
            string? text;
            _ownedDialog = true;
            try
            {
                text = TextInputForm.Edit(this,
                    new PhysRect(_cursorVirtual.X, _cursorVirtual.Y,
                        Math.Min(sel.Right - _cursorVirtual.X, (int)(280 * _metrics.DpiScale)), (int)(size * 2)),
                    size, style.Argb, null, _metrics.DpiScale);
            }
            finally { _ownedDialog = false; Activate(); }
            if (!string.IsNullOrWhiteSpace(text)) AddAnnotation(new TextAnnotation(_annotationStart, text, size, style));
            return true;
        }
        if (kind == AnnotationKind.Number)
        {
            AddAnnotation(new NumberAnnotation(_annotationStart, _history.NextNumber,
                EditorDefaults.ResolveNumberRadius(EditorDefaults.TextSizeToPhys(_toolbar.State.TextSize, _metrics.DpiScale)), style));
            return true;
        }
        _drawing = true;
        _stroke.Clear();
        _stroke.Add(_annotationStart);
        Capture = true;
        return true;
    }

    private void AddAnnotation(Annotation annotation)
    {
        annotation.SetPixelScale(_metrics.DpiScale);
        _history.Add(annotation);
    }

    private void UpdateAnnotation()
    {
        PhysPointF p = AnnotationPoint();
        var style = new AnnotationStyle(_toolbar.State.ActiveColorArgb, _toolbar.State.Brush);
        var rect = new PhysRectF(Math.Min(p.X, _annotationStart.X), Math.Min(p.Y, _annotationStart.Y),
            Math.Abs(p.X - _annotationStart.X), Math.Abs(p.Y - _annotationStart.Y));
        if (_stroke.Count == 0 || _stroke[^1] != p) _stroke.Add(p);
        int block = Math.Max(6, (int)Math.Round((6 + (int)style.Brush * 4) * _metrics.DpiScale));
        _previewAnnotation = _toolbar.ActiveTool switch
        {
            AnnotationKind.Rectangle => new RectAnnotation(rect, style),
            AnnotationKind.Ellipse => new EllipseAnnotation(rect, style),
            AnnotationKind.Arrow => new ArrowAnnotation(_annotationStart, p, style),
            AnnotationKind.Pen => new PenAnnotation(_stroke, style),
            AnnotationKind.Mosaic => new MosaicAnnotation(MosaicGeometry.QuantizeRect(rect, block), block, MosaicMode.Block, style, rect),
            _ => null
        };
        _previewAnnotation?.SetPixelScale(_metrics.DpiScale);
        ResetAnnotationComposite();
    }

    private void ResetAnnotationComposite()
    {
        _annotationComposite?.Dispose();
        _annotationComposite = null;
    }

    // 经真实鼠标/快捷键处理函数验证接线；不向桌面注入输入。
    internal bool CheckEditorInteractions()
    {
        SetSelection(new PhysRect(_virtualBounds.X + 80, _virtualBounds.Y + 80, 720, 480));
        PhysRect? original = _selection;
        for (int tool = 0; tool < 5; tool++)
        {
            SelectToolByOrdinal(tool);
            OnMouseDown(new MouseEventArgs(MouseButtons.Left, 1, 150, 160, 0));
            OnMouseMove(new MouseEventArgs(MouseButtons.Left, 0, 350, 320, 0));
            OnMouseUp(new MouseEventArgs(MouseButtons.Left, 1, 350, 320, 0));
        }
        if (_history.Count != 5 || _selection != original) return false;
        Message msg = default;
        ProcessCmdKey(ref msg, Keys.Control | Keys.Z);
        if (_history.Count != 4) return false;
        ProcessCmdKey(ref msg, Keys.Control | Keys.Y);
        if (_history.Count != 5) return false;
        // 第 7 项是序号工具，单击添加。
        SelectToolByOrdinal(6);
        OnMouseDown(new MouseEventArgs(MouseButtons.Left, 1, 280, 240, 0));
        if (_history.Count != 6) return false;
        // 预览与导出必须使用同一份合成图，尤其马赛克不能只在输出时出现。
        using var expected = ComposeSelectionBitmap(true);
        using var canvas = new Bitmap(_virtualBounds.Width, _virtualBounds.Height);
        using (var graphics = Graphics.FromImage(canvas)) PaintEditorAnnotations(graphics, _selection!.Value);
        if (expected is null) return false;
        for (int y = 0; y < expected.Height; y += 19)
            for (int x = 0; x < expected.Width; x += 19)
                if (expected.GetPixel(x, y).ToArgb() != canvas.GetPixel(x + 80, y + 80).ToArgb()) return false;
        return true;
    }

    private void PaintEditorAnnotations(Graphics graphics, PhysRect sel)
    {
        if (_history.Count == 0 && _previewAnnotation is null) return;
        if (_annotationComposite is null || _annotationComposite.Width != sel.Width || _annotationComposite.Height != sel.Height)
        {
            ResetAnnotationComposite();
            using var raw = ShotTool.Capture.SelectionCropper.Crop(_frame, sel);
            if (raw is null) return;
            var items = _history.Snapshot();
            if (_previewAnnotation is not null) items.Add(_previewAnnotation);
            _annotationComposite = AnnotationRasterizer.Compose(raw, items, RenderOptions.Create(_metrics.DpiScale));
        }
        graphics.DrawImageUnscaled(_annotationComposite, sel.X - _virtualBounds.X, sel.Y - _virtualBounds.Y);
    }

    uint IToolbarHostSurface.Dpi => _dpi;
    PhysRect IToolbarHostSurface.Selection => _selection ?? PhysRect.Empty;
    PhysRect IToolbarHostSurface.WorkArea => _virtualBounds;
    PhysRectF IToolbarHostSurface.ToolbarBounds => new(_toolbar.Bounds.X + _virtualBounds.X,
        _toolbar.Bounds.Y + _virtualBounds.Y, _toolbar.Bounds.Width, _toolbar.Bounds.Height);
    void IToolbarHostSurface.InvalidateToolbar()
    {
        _toolbar.Layout(_selection, _virtualBounds, _dpi);
        InvalidateOverlay(_lastFootprint);
    }
    void IToolbarHostSurface.ShowToast(string text) => ShowToast(text);
    void IToolbarHostSurface.AttachPanel(ToolbarPanelHost panel)
    {
        panel.Location = new Point(panel.Left - _virtualBounds.X, panel.Top - _virtualBounds.Y);
        Controls.Add(panel);
        panel.BringToFront();
    }
    void IToolbarHostSurface.DetachPanel(ToolbarPanelHost panel) => Controls.Remove(panel);
}
