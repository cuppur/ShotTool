using ShotTool.Core;

namespace ShotTool.App.Overlay;

internal sealed partial class SelectionOverlayForm
{
    internal void CheckWindowPicking(string directory, uint dpi)
    {
        void Require(bool pass, string name) { if (!pass) throw new InvalidOperationException($"DPI {dpi}: {name}"); }
        void Reset()
        {
            _selection = null; _windowCandidate = false; _pendingWindowPick = false; _drag = DragMode.None;
            _toolbar.Layout(null, _virtualBounds, _dpi);
            _candidateDepth = 0;
        }
        void Move(int x, int y) => OnMouseMove(new MouseEventArgs(MouseButtons.None, 0, x, y, 0));
        void Down(int x, int y) => OnMouseDown(new MouseEventArgs(MouseButtons.Left, 1, x, y, 0));
        void Up(int x, int y) => OnMouseUp(new MouseEventArgs(MouseButtons.Left, 1, x, y, 0));
        Move(200, 120);
        Require(_windowCandidate && Selection == new PhysRect(-500, 20, 600, 400), "whole-window preview");
        OnMouseWheel(new MouseEventArgs(MouseButtons.None, 0, 200, 120, -120));
        Require(Selection == new PhysRect(-450, 80, 240, 180), "wheel selects child");
        Move(201, 121);
        Require(Selection == new PhysRect(-450, 80, 240, 180), "child level stable while moving");
        using (var preview = new Bitmap(_virtualBounds.Width, _virtualBounds.Height))
        {
            using (var g = Graphics.FromImage(preview)) PaintScene(g);
            preview.Save(Path.Combine(directory, $"window-preview-{dpi}.png"));
        }
        Down(200, 120); Up(202, 122);
        Require(!_windowCandidate && _toolbar.IsVisible && Selection == new PhysRect(-450, 80, 240, 180), "click locks child");
        var message = new Message();
        _ = ProcessCmdKey(ref message, Keys.Right);
        _ = ProcessCmdKey(ref message, Keys.Shift | Keys.Down);
        Require(Selection == new PhysRect(-449, 90, 240, 180), "1px / 10px precision movement");
        Reset(); Move(200, 120); UpdateWindowCandidateAtCursor(disableSnap: true);
        Require(Selection is null && !_windowCandidate, "Shift bypasses preview");
        UpdateWindowCandidateAtCursor(disableSnap: false);
        Require(_windowCandidate, "releasing Shift restores preview");
        Reset(); Move(200, 120); Down(200, 120); Move(300, 220); Up(310, 230);
        Require(Selection == new PhysRect(-400, 120, 110, 110) && !_windowCandidate, "drag overrides snap; final release coordinates");
        Reset(); Move(200, 120); Down(200, 120); Up(300, 220);
        Require(Selection == new PhysRect(-400, 120, 100, 100), "fast drag without move event");
        Reset(); Move(200, 120); SelectFullVirtualDesktop(); Move(210, 130);
        Require(!_windowCandidate && Selection == _virtualBounds && _toolbar.IsVisible, "Ctrl+A locks full desktop");
        Reset(); Move(200, 120); Down(200, 120); Up(205, 120);
        Require(dpi == 96 ? Selection == new PhysRect(-400, 120, 5, 0) || Selection is null
            : Selection == new PhysRect(-500, 20, 600, 400), "DPI-aware click tolerance");
        Console.WriteLine($"[PASS] Overlay DPI {dpi}: hover, hierarchy, jitter, click, drag, fast release, Ctrl+A, click tolerance");
    }
}
