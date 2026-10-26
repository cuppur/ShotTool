using System.Drawing.Drawing2D;
using ShotTool.Core;
using ShotTool.Editor.Algorithms;
using ShotTool.Editor.Model;
using ShotTool.Editor.Toolbar;

namespace ShotTool.App.Overlay;

internal sealed partial class SelectionOverlayForm
{
    private TextAnnotation? _selectedText;
    private TextAnnotation? _movingTextOriginal;
    private TextAnnotation? _movingTextPreview;
    private PhysPoint _textDragStart;

    private TextAnnotation? HitText()
    {
        if (_windowCandidate || _selection is not { } sel || !sel.Contains(_cursorVirtual)) return null;
        if (_toolbar.ActiveTool is not (null or AnnotationKind.Text)) return null;
        var point = AnnotationPoint();
        return _history.Items.OfType<TextAnnotation>().Reverse()
            .FirstOrDefault(t => t.HitTest(point, Math.Max(2, 2 * _metrics.DpiScale)));
    }
    private bool BeginTextMove()
    {
        var text = HitText();
        if (text is null) { _selectedText = null; return false; }
        _selectedText = text; _movingTextOriginal = text; _movingTextPreview = text;
        _textDragStart = _cursorVirtual;
        Capture = true;
        InvalidateOverlay(_lastFootprint);
        return true;
    }
    private static TextAnnotation CopyText(TextAnnotation source, PhysPointF position, string? content = null)
    {
        var copy = new TextAnnotation(position, content ?? source.Text, source.FontSizePhys, source.Style, source.FontFamily);
        copy.SetPixelScale(source.PixelScale);
        TextMeasurer.Fill(copy);
        return copy;
    }
    private void UpdateTextMove()
    {
        if (_movingTextOriginal is not { } original || _selection is not { } sel) return;
        TextMeasurer.Fill(original);
        double x = Math.Clamp(original.TopLeft.X + _cursorVirtual.X - _textDragStart.X, 0, Math.Max(0, sel.Width - original.VisualBounds.Width));
        double y = Math.Clamp(original.TopLeft.Y + _cursorVirtual.Y - _textDragStart.Y, 0, Math.Max(0, sel.Height - original.VisualBounds.Height));
        _movingTextPreview = CopyText(original, new PhysPointF(x, y));
        ResetAnnotationComposite();
        InvalidateOverlay(_lastFootprint);
    }
    private void EndTextMove()
    {
        UpdateTextMove();
        var original = _movingTextOriginal;
        var moved = _movingTextPreview;
        _movingTextOriginal = null; _movingTextPreview = null;
        Capture = false;
        if (original is not null && moved is not null && original.TopLeft != moved.TopLeft)
        {
            _selectedText = moved;
            _history.Replace(original, moved, "移动文字");
        }
        ResetAnnotationComposite(); InvalidateOverlay(_lastFootprint);
    }
    private void EditTextAnnotation(TextAnnotation? original, PhysPointF position, double fontSize, AnnotationStyle style)
    {
        if (_selection is not { } sel) return;
        using var raw = ShotTool.Capture.SelectionCropper.Crop(_frame, sel);
        if (raw is null) return;
        var items = _history.Snapshot();
        if (original is not null) items.Remove(original);
        using var background = AnnotationRasterizer.Compose(raw, items, RenderOptions.Create(_metrics.DpiScale));
        int x = sel.X + (int)Math.Round(position.X), y = sel.Y + (int)Math.Round(position.Y);
        string? text;
        _ownedDialog = true;
        try
        {
            text = TextInputForm.Edit(this, new PhysRect(x, y, sel.Right - x, sel.Bottom - y),
                fontSize, style.Argb, original?.Text, _metrics.DpiScale, background, new PhysPoint(sel.X, sel.Y));
        }
        finally { _ownedDialog = false; Activate(); }
        if (text is null) return; // Escape preserves the original annotation.
        if (string.IsNullOrWhiteSpace(text))
        {
            if (original is not null) _history.Remove(original);
            _selectedText = null; return;
        }
        var replacement = new TextAnnotation(position, text, fontSize, style, original?.FontFamily);
        replacement.SetPixelScale(original?.PixelScale ?? _metrics.DpiScale);
        TextMeasurer.Fill(replacement);
        if (original is null) _history.Add(replacement);
        else if (text != original.Text) _history.Replace(original, replacement);
        else replacement = original;
        _selectedText = replacement;
        ResetAnnotationComposite(); InvalidateOverlay(_lastFootprint);
    }
    private void PaintTextSelection(Graphics graphics, PhysRect sel)
    {
        var selected = _movingTextPreview ?? _selectedText;
        if (selected is null || !_history.Items.Contains(_movingTextOriginal ?? selected)) return;
        TextMeasurer.Fill(selected);
        var b = selected.VisualBounds;
        using var pen = new Pen(Color.FromArgb(0, 195, 117), Math.Max(1, (float)_metrics.DpiScale)) { DashStyle = DashStyle.Dash };
        var state = graphics.Save();
        graphics.SetClip(ToLocalRect(sel));
        graphics.DrawRectangle(pen, (float)(sel.X - _virtualBounds.X + b.X - 2), (float)(sel.Y - _virtualBounds.Y + b.Y - 2),
            (float)b.Width + 4, (float)b.Height + 4);
        graphics.Restore(state);
    }
}
