using ShotTool.Core;
using ShotTool.Editor.Toolbar;

namespace ShotTool.App;

internal static class TextEditingSelfTest
{
    internal static bool Run(IAppLog log, string directory)
    {
        try
        {
            using var background = new Bitmap(800, 500);
            using (var g = Graphics.FromImage(background))
                for (int y = 0; y < 500; y += 20)
                    for (int x = 0; x < 800; x += 20)
                    {
                        using var brush = new SolidBrush((x / 20 + y / 20) % 2 == 0 ? Color.FromArgb(24, 83, 117) : Color.FromArgb(62, 42, 90));
                        g.FillRectangle(brush, x, y, 20, 20);
                    }
            using (var input = new TextInputForm("透明文字 Hello", 28, Color.White, new PhysRect(20, 20, 500, 200), 1,
                background, default) { DiagnosticMode = true })
            {
                input.Location = new Point(-20000, -20000);
                input.Show(); Application.DoEvents();
                input.SetDiagnosticText("透明文字 Hello\n第二行文字");
                Application.DoEvents(); input.Update();
                if (!input.DiagnosticTextFits) throw new InvalidOperationException("多行输入的最后一行被裁切。");
                using var render = new Bitmap(input.Width, input.Height);
                input.DrawToBitmap(render, input.ClientRectangle);
                render.Save(Path.Combine(directory, "transparent-text-input.png"));
                int matches = 0;
                int y = input.ClientSize.Height - 4;
                for (int x = 8; x < input.Width - 4; x += 17)
                    if (render.GetPixel(x, y).ToArgb() == background.GetPixel(x + 19, y + 19).ToArgb()) matches++;
                if (matches < 5) throw new InvalidOperationException("输入区域没有透出棋盘背景。");
                input.CommitForDiagnostics();
                if (input.Result != "透明文字 Hello\n第二行文字") throw new InvalidOperationException("多行文本提交错误。");
            }
            var bounds = new PhysRect(0, 0, 800, 500);
            using var frame = new CapturedFrame((Bitmap)background.Clone(), bounds,
                new[] { new MonitorInfo("fixture", bounds, bounds, 144, true) }, new PhysPoint(200, 120), CaptureBackend.GdiBitBlt, DateTime.UtcNow);
            using var form = new Overlay.SelectionOverlayForm(new ShotToolContext(log), frame) { DiagnosticMode = true };
            form.CheckTextInteractions(directory);
            Console.WriteLine("[PASS] Text: screenshot backdrop, multiline commit, drag/release, bounds clamp, undo/redo, export without selection border");
            return true;
        }
        catch (Exception ex) { log.Error("文字编辑检查失败", ex); Console.WriteLine("[FAIL] Text: " + ex.Message); return false; }
    }
}
