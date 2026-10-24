using System.Diagnostics;
using ShotTool.Core;
using ShotTool.Interop;

namespace ShotTool.App;

internal static class WindowPickingSelfTest
{
    internal static bool Run(IAppLog log, string directory)
    {
        try
        {
            // Real HWND fixtures live outside all monitors and never activate.
            using var back = new Fixture { Bounds = new Rectangle(-20000, -20000, 500, 400) };
            using var front = new Fixture { Bounds = new Rectangle(-19900, -19900, 500, 400) };
            var panel = new Panel { Bounds = new Rectangle(40, 40, 250, 180) };
            var button = new Button { Bounds = new Rectangle(20, 20, 80, 35) };
            panel.Controls.Add(button);
            front.Controls.Add(panel);
            back.Show(); front.Show();
            var desktop = new PhysRect(-21000, -21000, 3000, 3000);
            using (var snapshot = WindowTargetPicker.Snapshot(desktop))
            {
                var chain = snapshot.FindAt(new PhysPoint(-19830, -19830));
                Require(chain.Count >= 3 && chain[0].Handle == front.Handle && chain[^1].Handle == button.Handle,
                    "frontmost window and nested native button");
                Require(chain[0].Bounds == new PhysRect(-19900, -19900, 500, 400), "physical window edges");
                Require(chain[^1].Bounds == new PhysRect(-19840, -19840, 80, 35), "physical child edges");
                front.Location = new Point(-19500, -19500);
                Require(snapshot.FindAt(new PhysPoint(-19830, -19830))[0].Handle == front.Handle, "frozen geometry");
                Require(snapshot.FindAt(new PhysPoint(-19950, -19950))[0].Handle == back.Handle, "uncovered back window");
            }
            front.Hide();
            using (var hidden = WindowTargetPicker.Snapshot(desktop))
                Require(hidden.FindAt(new PhysPoint(-19830, -19830))[0].Handle == back.Handle, "hidden window excluded");
            front.Show();
            front.Location = new Point(-19900, -19900);
            using (var path = new System.Drawing.Drawing2D.GraphicsPath())
            {
                path.AddEllipse(0, 0, 500, 400);
                front.Region = new Region(path);
            }
            using (var shaped = WindowTargetPicker.Snapshot(desktop))
                Require(shaped.FindAt(new PhysPoint(-19899, -19899))[0].Handle == back.Handle, "transparent shaped corner");
            front.Close(); back.Close();
            Console.WriteLine("[PASS] Real HWND: z-order, exact edges, child hierarchy, frozen state, hidden and shaped windows");

            foreach (uint dpi in new uint[] { 96, 216 })
            {
                var root = new WindowTargetPicker.Target(new IntPtr(101), new PhysRect(-500, 20, 600, 400),
                    new[] { new WindowTargetPicker.Target(new IntPtr(102), new PhysRect(-450, 80, 240, 180), Array.Empty<WindowTargetPicker.Target>()) });
                var picker = new WindowTargetPicker(new[] { root });
                var bounds = new PhysRect(-600, 0, 900, 600);
                using var bitmap = new Bitmap(900, 600);
                using (var g = Graphics.FromImage(bitmap)) g.Clear(Color.FromArgb(45, 56, 74));
                using var frame = new CapturedFrame((Bitmap)bitmap.Clone(), bounds,
                    new[] { new MonitorInfo("fixture", bounds, bounds, dpi, true) }, new PhysPoint(-400, 120), CaptureBackend.GdiBitBlt, DateTime.UtcNow);
                using var form = new Overlay.SelectionOverlayForm(new ShotToolContext(log), frame, windowTargets: picker) { DiagnosticMode = true };
                form.CheckWindowPicking(directory, dpi);
            }
            var watch = Stopwatch.StartNew();
            using (var snapshot = WindowTargetPicker.Snapshot(MonitorService.GetVirtualDesktopBounds()))
            {
                long snapshotMs = watch.ElapsedMilliseconds;
                watch.Restart();
                for (int i = 0; i < 10000; i++) _ = snapshot.FindAt(new PhysPoint(i % 1000, i % 700));
                Console.WriteLine($"[INFO] Snapshot {snapshotMs}ms; 10000 cached hit tests {watch.ElapsedMilliseconds}ms");
            }
            return true;
        }
        catch (Exception ex)
        {
            log.Error("窗口识别检查失败", ex);
            Console.WriteLine("[FAIL] Window picking: " + ex.Message);
            return false;
        }
    }
    private static void Require(bool pass, string name) { if (!pass) throw new InvalidOperationException(name); }
    private sealed class Fixture : Form
    {
        internal Fixture() { FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = false; StartPosition = FormStartPosition.Manual; AutoScaleMode = AutoScaleMode.None; }
        protected override bool ShowWithoutActivation => true;
    }
}
