using ShotTool.Core;
using ShotTool.Editor.Algorithms;
using ShotTool.Editor.Model;

namespace ShotTool.App.Overlay;

internal sealed partial class SelectionOverlayForm
{
    internal void CheckTextInteractions(string directory)
    {
        void Require(bool pass, string name) { if (!pass) throw new InvalidOperationException(name); }
        SetSelection(new PhysRect(20, 20, 720, 420));
        var text = new TextAnnotation(new PhysPointF(40, 50), "可以移动文字", 26,
            new AnnotationStyle(0xFFFF4444, BrushLevel.Middle));
        TextMeasurer.Fill(text); AddAnnotation(text);
        var sentinel = new RectAnnotation(new PhysRectF(300, 230, 150, 90), AnnotationStyle.Default);
        AddAnnotation(sentinel);
        _toolbar.State.ActiveIndex = -1;
        OnMouseDown(new MouseEventArgs(MouseButtons.Left, 1, 70, 85, 0));
        OnMouseMove(new MouseEventArgs(MouseButtons.Left, 0, 130, 135, 0));
        Require(ReferenceEquals(_history.Items[0], text), "drag preview mutated committed annotation");
        OnMouseUp(new MouseEventArgs(MouseButtons.Left, 1, 140, 145, 0));
        var moved = (TextAnnotation)_history.Items[0];
        Require(moved.TopLeft == new PhysPointF(110, 110), "text move/release position");
        Require(_history.Count == 2 && ReferenceEquals(_history.Items[1], sentinel), "move changed z-order");
        _history.Undo(); Require(ReferenceEquals(_history.Items[0], text), "undo move");
        _history.Redo(); Require(ReferenceEquals(_history.Items[0], moved), "redo move");
        var edited = CopyText(moved, moved.TopLeft, "修改后的文字\n第二行");
        _history.Replace(moved, edited);
        _history.Undo(); Require(((TextAnnotation)_history.Items[0]).Text == moved.Text, "undo text edit");
        _history.Redo(); Require(ReferenceEquals(_history.Items[0], edited), "redo text edit");
        _selectedText = edited;
        using var withBorder = new Bitmap(800, 500);
        using (var g = Graphics.FromImage(withBorder)) PaintScene(g);
        withBorder.Save(Path.Combine(directory, "text-selected.png"));
        using var output = ComposeSelectionBitmap(true)!;
        _selectedText = null;
        using var plainOutput = ComposeSelectionBitmap(true)!;
        for (int y = 0; y < output.Height; y += 7)
            for (int x = 0; x < output.Width; x += 7)
                Require(output.GetPixel(x, y) == plainOutput.GetPixel(x, y), "selection border leaked into export");
        output.Save(Path.Combine(directory, "text-export.png"));
        OnMouseDown(new MouseEventArgs(MouseButtons.Left, 1, 140, 145, 0));
        OnMouseUp(new MouseEventArgs(MouseButtons.Left, 1, 2000, 2000, 0));
        var clamped = (TextAnnotation)_history.Items[0];
        Require(clamped.VisualBounds.Right <= 720.1 && clamped.VisualBounds.Bottom <= 420.1, "text escaped crop bounds");
        _history.Undo(); _history.Undo();
        Require(_history.CanRedo, "redo missing");
        _history.Add(new TextAnnotation(default, "新分支", 20));
        Require(!_history.CanRedo, "new action did not discard redo branch");
    }
}
