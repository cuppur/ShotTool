using System.Diagnostics;
using System.Drawing.Imaging;
using ShotTool.Ai;
using ShotTool.Ai.Translation;
using ShotTool.App.Overlay;
using ShotTool.Core;

namespace ShotTool.App;

// 使用真实工具条回调、面板、OCR 和本地翻译；只处理自建测试图，不抓取用户内容。
internal static class AiUiSelfTest
{
    internal static bool Run(IAppLog log, string directory)
    {
        var previousContext = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
        var context = new ShotToolContext(log);
        CompositionRoot.TryCompose(context);
        var ai = (AiServiceRegistry)context.Ai!;
        try
        {
            string? modelPath = Environment.GetEnvironmentVariable("SHOTTOOL_TEST_MODEL_DIR");
            if (!string.IsNullOrWhiteSpace(modelPath))
                ((NllbModelLocator)ai.Translations2!.Locator).SetDirectoryOverride(modelPath);
            ai.Translations2!.ApplyBackendSelection("nllb");
            ai.ApplyBackend("windows");
            if (ai.ResolveDefaultRecognizer()?.Id != "windows.media.ocr")
                throw new InvalidOperationException("指定 Windows OCR 未生效");
            ai.ApplyBackend("paddle");
            if (ai.ResolveDefaultRecognizer()?.Id != "paddle.onnx")
                throw new InvalidOperationException("指定 Paddle OCR 未生效");
            ai.ApplyBackend("windows");

            var bitmap = new Bitmap(1920, 1080, PixelFormat.Format32bppPArgb);
            using (var g = Graphics.FromImage(bitmap))
            using (var font = new Font("Arial", 36, GraphicsUnit.Pixel))
            {
                g.Clear(Color.White);
                g.DrawString("Hello world", font, Brushes.Black, 140, 160);
            }
            uint dpi = uint.TryParse(Environment.GetEnvironmentVariable("SHOTTOOL_TEST_DPI"), out var testDpi) ? testDpi : 96;
            var bounds = new PhysRect(0, 0, 1920, 1080);
            using var frame = new CapturedFrame(bitmap, bounds,
                new[] { new MonitorInfo("synthetic", bounds, bounds, dpi, true) },
                new PhysPoint(180, 180), CaptureBackend.GdiBitBlt, DateTime.UtcNow);
            using var form = new SelectionOverlayForm(context, frame) { DiagnosticMode = true };
            form.ApplyDiagnosticState(new PhysRect(100, 100, 650, 320), new PhysPoint(180, 180), false);
            // 让子控件走实际布局/绘制生命周期，同时把诊断窗口放在屏幕之外且不激活。
            form.Location = new Point(-20000, -20000);
            form.Show();

            var ocrPanel = form.InvokeExtensionForDiagnostics("ext.ai.ocr")
                ?? throw new InvalidOperationException("OCR 面板没有创建");
            CheckPanel(ocrPanel);
            PumpUntil(() => Descendants(ocrPanel).OfType<Label>().Any(t => t.Text.Contains("行 /"))
                && Descendants(ocrPanel).OfType<TextBox>().Any(t => t.TextLength >= 5), 15000);
            SavePanel(ocrPanel, Path.Combine(directory, "ocr-panel.png"));
            Console.WriteLine("[PASS] OCR UI: " + ocrPanel.Size + " | "
                + string.Join(" | ", Descendants(ocrPanel).OfType<TextBox>().Select(t => t.Text)));

            ai.ApplyBackend("paddle");
            var paddlePanel = form.InvokeExtensionForDiagnostics("ext.ai.ocr")!;
            CheckPanel(paddlePanel);
            PumpUntil(() => Descendants(paddlePanel).OfType<Label>().Any(t => t.Text.Contains("行 /"))
                && Descendants(paddlePanel).OfType<TextBox>().Any(t => t.TextLength >= 5), 15000);
            Console.WriteLine("[PASS] Paddle OCR UI: " + string.Join(" | ", Descendants(paddlePanel).OfType<TextBox>().Select(t => t.Text)));
            SavePanel(paddlePanel, Path.Combine(directory, "ocr-panel.png"));
            Descendants(paddlePanel).OfType<Button>().First(b => b.Text == "复制").PerformClick();
            if (!ShotTool.Output.TextClipboard.Read().Contains("Hello", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("OCR 复制按钮没有写入识别结果");

            if (ai.Translations2.ResolveActiveProvider() is null)
                throw new InvalidOperationException("未找到翻译模型，请设置 SHOTTOOL_TEST_MODEL_DIR");
            var translationPanel = form.InvokeExtensionForDiagnostics("ext.ai.translate")
                ?? throw new InvalidOperationException("翻译面板没有创建");
            CheckPanel(translationPanel);
            PumpUntil(() => Descendants(translationPanel).OfType<Label>().Any(t => t.Text.Contains("→"))
                && Descendants(translationPanel).OfType<TextBox>().Any(t => t.Text.Any(c => c is >= '\u4e00' and <= '\u9fff')), 45000);
            var texts = Descendants(translationPanel).OfType<TextBox>().Select(t => t.Text).ToArray();
            Console.WriteLine("[PASS] Translation UI: " + string.Join(" | ", texts));
            Descendants(translationPanel).OfType<Button>().First(b => b.Text == "复制译文").PerformClick();
            if (!ShotTool.Output.TextClipboard.Read().Contains("世界")) throw new InvalidOperationException("复制译文失败");
            Console.WriteLine("[PASS] OCR / translation copy buttons wrote text to the Windows clipboard");
            SavePanel(translationPanel, Path.Combine(directory, "translation-panel.png"));
            form.SaveToolbarForDiagnostics(Path.Combine(directory, "toolbar-current.png"));
            return true;
        }
        catch (Exception ex)
        {
            log.Error("AI UI 整合自检失败", ex);
            Console.WriteLine("[FAIL] AI UI: " + ex.Message);
            return false;
        }
        finally
        {
            ai.Dispose();
            (context.Settings as IDisposable)?.Dispose();
            (context.Capture as IDisposable)?.Dispose();
            context.Hotkeys?.Dispose();
            SynchronizationContext.SetSynchronizationContext(previousContext);
        }
    }

    private static void CheckPanel(Control panel)
    {
        if (panel.Width < 200 || panel.Height < 100)
            throw new InvalidOperationException($"面板尺寸无效：{panel.Width}x{panel.Height}");
        panel.CreateControl();
        panel.PerformLayout();
    }

    private static IEnumerable<Control> Descendants(Control control)
    {
        foreach (Control child in control.Controls)
        {
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }

    private static void PumpUntil(Func<bool> condition, int timeoutMs)
    {
        var timer = Stopwatch.StartNew();
        while (!condition())
        {
            if (timer.ElapsedMilliseconds > timeoutMs) throw new TimeoutException("等待结果显示超时");
            Application.DoEvents();
            Thread.Sleep(10);
        }
    }

    private static void SavePanel(Control panel, string path)
    {
        using var image = new Bitmap(panel.Width, panel.Height);
        panel.DrawToBitmap(image, new Rectangle(Point.Empty, panel.Size));
        image.Save(path, ImageFormat.Png);
    }
}
