using System.Drawing.Imaging;
using ShotTool.App.Overlay;
using ShotTool.Core;
using ShotTool.Editor.Model;

namespace ShotTool.App;

internal static class PinSelfTest
{
    internal static bool Run(IAppLog log, string directory)
    {
        var output = new RecordingOutput();
        var context = new ShotToolContext(log) { Output = output };
        try
        {
            var bitmap = new Bitmap(640, 360, PixelFormat.Format32bppPArgb);
            using (var g = Graphics.FromImage(bitmap)) g.Clear(Color.White);
            using (var frame = new CapturedFrame(bitmap, new PhysRect(0, 0, 640, 360),
                Array.Empty<MonitorInfo>(), new PhysPoint(100, 100), CaptureBackend.GdiBitBlt, DateTime.UtcNow))
            using (var form = new SelectionOverlayForm(context, frame) { DiagnosticMode = true })
            {
                form.SetSelection(frame.VirtualDesktopBounds);
                form.History.Add(new RectAnnotation(new PhysRectF(40, 40, 300, 200),
                    new AnnotationStyle(0xFFFF0000, BrushLevel.Thick)));
                form.AttachHandoff(new SelectionHandoff(context, diagnosticPins: true));
                var result = form.CompleteAsync(ShotCompletion.Pin).GetAwaiter().GetResult();
                if (!result.Success) throw new InvalidOperationException("固定截图交付失败", result.Error);
            }
            // 此时原始帧及编辑器都已释放；贴图必须仍拥有自己的成品图。
            var pin = Application.OpenForms.OfType<PinWindow>().Single();
            if (!pin.TopMost || pin.Size != new Size(640, 360)) throw new InvalidOperationException("贴图窗口状态错误");
            pin.ZoomAt(new Point(100, 100), 0.5);
            if (pin.Size != new Size(320, 180)) throw new InvalidOperationException("缩放错误");
            pin.CopyAsync().GetAwaiter().GetResult();
            if (output.Image?.Size != new Size(640, 360)) throw new InvalidOperationException("缩放后的复制丢失原图分辨率");
            var pixel = output.Image.GetPixel(40, 100);
            if (pixel.R < 200 || pixel.G > 80) throw new InvalidOperationException("贴图没有保留标注");
            output.Image.Save(Path.Combine(directory, "pin-source.png"), ImageFormat.Png);
            pin.ResetZoom();
            if (pin.Size != new Size(640, 360)) throw new InvalidOperationException("恢复原始大小失败");
            using (var view = new Bitmap(pin.Width, pin.Height))
            {
                pin.DrawToBitmap(view, new Rectangle(Point.Empty, pin.Size));
                view.Save(Path.Combine(directory, "pin-window.png"), ImageFormat.Png);
            }
            using var secondImage = new Bitmap(100, 100);
            var second = PinWindow.ShowPinned(secondImage, new PhysRect(0, 0, 100, 100), output, log, true);
            if (Application.OpenForms.OfType<PinWindow>().Count() != 2) throw new InvalidOperationException("无法同时保留多张贴图");
            PinWindow.CloseAll();
            if (!pin.IsDisposed || !second.IsDisposed) throw new InvalidOperationException("贴图未释放");
            Console.WriteLine("[PASS] PIN: annotated image survives editor disposal; zoom/reset; original-resolution copy; multiple pins; close/dispose");
            return true;
        }
        catch (Exception ex)
        {
            log.Error("固定截图自检失败", ex);
            Console.WriteLine("[FAIL] PIN: " + ex.Message);
            return false;
        }
        finally { PinWindow.CloseAll(); output.Image?.Dispose(); }
    }

    private sealed class RecordingOutput : IOutputSink
    {
        internal Bitmap? Image;
        public Task CopyToClipboardAsync(Bitmap image, CancellationToken cancellationToken = default)
        {
            Image?.Dispose();
            Image = (Bitmap)image.Clone();
            return Task.CompletedTask;
        }
        public Task<string> SaveAsync(Bitmap image, SaveRequest request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public string ResolvePath(SaveRequest request) => string.Empty;
    }
}
