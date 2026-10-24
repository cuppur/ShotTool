// ============================================================================
//  App/SelfTest.cs —— 无交互自测（给验收/CI 取证用，不属于产品功能）
//
//  存在的理由：截图工具的验收点全是"看得见"的东西（蒙版、边框、控制柄、尺寸 HUD、
//  放大镜、工具条位置），但热键 + 鼠标拖拽没法自动化。因此提供两条离屏通路：
//
//    ShotTool.exe --selftest         采集自测：虚拟桌面/显示器/DPI、抓屏耗时、
//                                    选区裁剪、**GDI 句柄泄漏检查**（完成/取消两条路径都建帧再释放）
//    ShotTool.exe --selftest-overlay overlay 自测：把 overlay 的**真实绘制**渲染到离屏位图
//                                    （初始态：遮罩+十字+放大镜；框选态：遮罩+边框+8柄+HUD+工具条+放大镜）
//
//  输出目录：环境变量 SHOTTOOL_SELFTEST_DIR，缺省 %LOCALAPPDATA%\ShotTool\selftest
//  退出码：0 = 全部 PASS；1 = 有 FAIL（便于脚本断言）
//
//  ★ 两条通路都不显示窗口、不抢焦点，可以在无人值守环境里跑。
// ============================================================================

using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using ShotTool.Capture;
using ShotTool.Core;
using ShotTool.Interop;

namespace ShotTool.App;

/// <summary>离屏自测入口。</summary>
internal static class SelfTest
{
    private const string CaptureFlag = "--selftest";
    private const string OverlayFlag = "--selftest-overlay";
    private const string ComposeFlag = "--selftest-compose";

    private static readonly List<string> Results = new();

    /// <summary>命中自测参数则执行并返回退出码；否则返回 null（继续正常启动）。</summary>
    internal static int? TryRun(string[] args, IAppLog log)
    {
        if (args is null || args.Length == 0)
        {
            return null;
        }

        bool capture = args.Any(a => string.Equals(a, CaptureFlag, StringComparison.OrdinalIgnoreCase));
        bool overlay = args.Any(a => string.Equals(a, OverlayFlag, StringComparison.OrdinalIgnoreCase));
        bool compose = args.Any(a => string.Equals(a, ComposeFlag, StringComparison.OrdinalIgnoreCase));
        bool editor = args.Contains("--selftest-editor");
        bool aiUi = args.Contains("--selftest-ai-ui");
        bool pin = args.Contains("--selftest-pin");
        bool settingsUi = args.Contains("--selftest-settings");
        bool windows = args.Contains("--selftest-windows");
        bool icons = args.Contains("--export-icon");

        if (!capture && !overlay && !compose && !editor && !aiUi && !pin && !settingsUi && !windows && !icons)
        {
            return null;
        }

        Results.Clear();
        string outputDirectory = ResolveOutputDirectory();
        Directory.CreateDirectory(outputDirectory);

        log.Info($"自测开始：capture={capture} overlay={overlay} compose={compose} 输出目录={outputDirectory}");

        bool ok = true;
        try
        {
            if (icons) TrayIconFactory.Export(Path.Combine(outputDirectory, "ShotTool.ico"), Path.Combine(outputDirectory, "app-icon.png"));
            if (windows) ok &= WindowPickingSelfTest.Run(log, outputDirectory);
            if (settingsUi) ok &= SettingsSelfTest.Run(log, outputDirectory);
            if (pin) ok &= PinSelfTest.Run(log, outputDirectory);
            if (aiUi) ok &= AiUiSelfTest.Run(log, outputDirectory);
            if (capture)
            {
                ok &= RunCapture(log, outputDirectory);
            }

            if (overlay)
            {
                ok &= RunOverlay(log, outputDirectory);
            }

            if (compose)
            {
                ok &= RunCompose(log, outputDirectory);
            }
            if (editor)
            {
                var context = new ShotToolContext(log);
                CompositionRoot.TryCompose(context);
                using var source = new GdiCaptureSource(log);
                using var frame = source.CaptureVirtualDesktop(CaptureOptions.Default);
                using var form = new Overlay.SelectionOverlayForm(context, frame) { DiagnosticMode = true };
                ok &= Check("编辑器交互接线", form.CheckEditorInteractions(),
                    "矩形/椭圆/箭头/画笔/马赛克/序号，撤销重做，预览与导出像素一致");
                using var bitmap = form.ComposeSelectionBitmap(true)!;
                context.Output!.CopyToClipboardAsync(bitmap).GetAwaiter().GetResult();
                ok &= Check("整合剪贴板输出", Clipboard.ContainsImage(), "标注成品已写入 Windows 剪贴板");
                bitmap.Save(Path.Combine(outputDirectory, "selftest-editor-result.png"), ImageFormat.Png);
                (context.Ai as IDisposable)?.Dispose();
            }
        }
        catch (Exception ex)
        {
            log.Error("自测抛出异常。", ex);
            Report("FAIL", "自测整体", ex.Message);
            ok = false;
        }

        Console.WriteLine();
        Console.WriteLine("==== SELFTEST SUMMARY ====");
        foreach (string line in Results)
        {
            Console.WriteLine(line);
        }

        Console.WriteLine(ok ? "SELFTEST_RESULT=PASS" : "SELFTEST_RESULT=FAIL");
        return ok ? 0 : 1;
    }

    // ------------------------------------------------------------------ 采集自测

    private static bool RunCapture(IAppLog log, string outputDirectory)
    {
        var ok = true;

        PhysRect bounds = MonitorService.GetVirtualDesktopBounds();
        IReadOnlyList<MonitorInfo> monitors = MonitorService.EnumerateMonitors(log);

        Console.WriteLine($"虚拟桌面 = {MonitorService.DescribeVirtualDesktop()}");
        Console.WriteLine($"显示器   = {MonitorService.DescribeMonitors(monitors)}");
        Console.WriteLine($"默认 DPI = {MonitorService.GetPrimaryDpi()}（225% 应为 216）");

        ok &= Check("虚拟桌面非空", !bounds.IsEmpty, bounds.ToString());
        ok &= Check("枚举到至少一个显示器", monitors.Count >= 1, $"{monitors.Count} 屏");

        // PMv2 自证：物理像素应显著大于逻辑像素（本机 3840x2160 vs 逻辑 1707x960）
        ok &= Check(
            "物理像素尺寸（DPI 感知生效）",
            bounds.Width >= 1280 && bounds.Height >= 720,
            $"{bounds.Width}x{bounds.Height}");

        using var source = new GdiCaptureSource(log);

        // ---- 1) 基本抓屏 ----
        string fullPath = Path.Combine(outputDirectory, "selftest-frame.png");
        long elapsedMs;
        using (CapturedFrame frame = source.CaptureVirtualDesktop(CaptureOptions.Default))
        {
            elapsedMs = source.LastElapsedMs;
            frame.Bitmap.Save(fullPath, ImageFormat.Png);
            ok &= Check("抓屏成功", frame.Bitmap.Width == bounds.Width && frame.Bitmap.Height == bounds.Height,
                $"{frame.Bitmap.Width}x{frame.Bitmap.Height}，{elapsedMs} ms");
            ok &= Check("像素格式 32bppPArgb", frame.Bitmap.PixelFormat == PixelFormat.Format32bppPArgb,
                frame.Bitmap.PixelFormat.ToString());

            // ---- 2) 选区裁剪（中心 800x600，夹进桌面）----
            int w = Math.Min(800, bounds.Width);
            int h = Math.Min(600, bounds.Height);
            var selection = new PhysRect(
                bounds.X + ((bounds.Width - w) / 2),
                bounds.Y + ((bounds.Height - h) / 2),
                w,
                h);

            using Bitmap? cropped = SelectionCropper.Crop(frame, selection);
            ok &= Check("选区裁剪成功", cropped is not null, selection.ToString());

            if (cropped is not null)
            {
                string cropPath = Path.Combine(outputDirectory, "selftest-selection.png");
                cropped.Save(cropPath, ImageFormat.Png);
                ok &= Check("裁剪尺寸与选区一致", cropped.Width == w && cropped.Height == h,
                    $"{cropped.Width}x{cropped.Height}");
                Console.WriteLine($"  选区裁剪 → {cropPath}");
            }

            Console.WriteLine($"  整屏抓屏 → {fullPath}");
        }

        // ---- 3) GDI 句柄泄漏检查（完成/取消两条路径都走一次）----
        uint before = NativeMethods.GetGuiResources(NativeMethods.GetCurrentProcess(), 0);
        for (int i = 0; i < 5; i++)
        {
            using CapturedFrame frame = source.CaptureVirtualDesktop(CaptureOptions.Default);
            using Bitmap? cropped = SelectionCropper.Crop(
                frame,
                new PhysRect(bounds.X, bounds.Y, Math.Min(400, bounds.Width), Math.Min(300, bounds.Height)));

            // 模拟"完成"路径：位图交给下游后释放
            cropped?.Dispose();

            // 模拟"取消"路径：不生成选区位图，直接释放帧（using 已覆盖）
        }

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        uint after = NativeMethods.GetGuiResources(NativeMethods.GetCurrentProcess(), 0);
        long delta = (long)after - before;

        ok &= Check("5 轮抓屏后 GDI 对象无泄漏（容差 ≤ 20）", delta <= 20, $"before={before} after={after} Δ={delta}");

        return ok;
    }

    // ------------------------------------------------------------------ overlay 自测

    private static bool RunOverlay(IAppLog log, string outputDirectory)
    {
        var ok = true;

        var context = new ShotToolContext(log);
        using var source = new GdiCaptureSource(log);
        using CapturedFrame frame = source.CaptureVirtualDesktop(CaptureOptions.Default);
        PhysRect bounds = frame.VirtualDesktopBounds;

        // ---- 状态一：初始态（未框选）→ 遮罩 + 全屏十字 + 放大镜 ----
        var initialCursor = new PhysPoint(bounds.X + (bounds.Width / 2), bounds.Y + (bounds.Height / 2));
        ok &= RenderScene(
            log,
            context,
            frame,
            selection: null,
            cursor: initialCursor,
            annotating: false,
            outputDirectory,
            fileName: "selftest-overlay-initial.png",
            label: "初始态（十字 + 放大镜）");

        // ---- 状态二：已框选 → 遮罩 + 绿色边框 + 8 控制柄 + 尺寸 HUD + 工具条 + 放大镜 ----
        int w = Math.Min(1600, bounds.Width / 2);
        int h = Math.Min(900, bounds.Height / 2);
        var selection = new PhysRect(
            bounds.X + (bounds.Width / 4),
            bounds.Y + (bounds.Height / 4),
            w,
            h);

        var selectionCursor = new PhysPoint(selection.X + (w / 3), selection.Y + (h / 3));
        ok &= RenderScene(
            log,
            context,
            frame,
            selection: selection,
            cursor: selectionCursor,
            annotating: true,
            outputDirectory,
            fileName: "selftest-overlay-selected.png",
            label: "框选态（边框/控制柄/HUD/工具条/放大镜）",
            detailCrop: selection);

        return ok;
    }

    private static bool RenderScene(
        IAppLog log,
        ShotToolContext context,
        CapturedFrame frame,
        PhysRect? selection,
        PhysPoint cursor,
        bool annotating,
        string outputDirectory,
        string fileName,
        string label,
        PhysRect? detailCrop = null)
    {
        PhysRect bounds = frame.VirtualDesktopBounds;

        using var form = new Overlay.SelectionOverlayForm(context, frame);
        form.ApplyDiagnosticState(selection, cursor, annotating);

        using var canvas = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppPArgb);
        using (Graphics graphics = Graphics.FromImage(canvas))
        {
            form.PaintScene(graphics);
        }

        string path = Path.Combine(outputDirectory, fileName);
        canvas.Save(path, ImageFormat.Png);
        Console.WriteLine($"  overlay[{label}] → {path}");

        bool ok = Check($"overlay 渲染：{label}", canvas.Width == bounds.Width && canvas.Height == bounds.Height,
            $"{canvas.Width}x{canvas.Height}");

        // 细节裁剪：便于人眼核对工具条 / HUD / 控制柄的像素级位置
        if (detailCrop is { } crop)
        {
            Rectangle region = crop.ToRectangle();
            region.Inflate(60, 60);
            Rectangle clamped = Rectangle.Intersect(
                region,
                new Rectangle(0, 0, canvas.Width, canvas.Height));

            if (clamped.Width > 0 && clamped.Height > 0)
            {
                using Bitmap detail = canvas.Clone(clamped, PixelFormat.Format32bppPArgb);
                string detailPath = Path.Combine(outputDirectory, Path.GetFileNameWithoutExtension(fileName) + "-detail.png");
                detail.Save(detailPath, ImageFormat.Png);
                Console.WriteLine($"  细节裁剪 → {detailPath}（{clamped.Width}x{clamped.Height}）");
            }
        }

        log.Info($"overlay 自测渲染完成：{label} → {path}");
        return ok;
    }

    // ------------------------------------------- 标注合成自测（验证"标注进成品图"）

    /// <summary>
    /// 验证「标注 → 成品位图」这条路（本层是交付出口，标注模型/栅格化属 dev-editor）：
    /// 往会话的 AnnotationHistory 里塞矩形 + 箭头，比较 includeAnnotations=false/true 两张图，
    /// 断言：尺寸一致、且标注笔迹处的像素确实被改写过。
    /// </summary>
    private static bool RunCompose(IAppLog log, string outputDirectory)
    {
        var ok = true;

        var context = new ShotToolContext(log);
        using var source = new GdiCaptureSource(log);
        using CapturedFrame frame = source.CaptureVirtualDesktop(CaptureOptions.Default);
        PhysRect bounds = frame.VirtualDesktopBounds;

        int w = Math.Min(900, bounds.Width / 3);
        int h = Math.Min(600, bounds.Height / 3);
        var selection = new PhysRect(bounds.X + (bounds.Width / 8), bounds.Y + (bounds.Height / 8), w, h);

        using var form = new Overlay.SelectionOverlayForm(context, frame);
        form.ApplyDiagnosticState(selection, selection.Center, annotating: true);

        // 标注坐标 = 选区局部坐标（dev-editor 约定）。这里画一个矩形 + 一支箭头。
        var style = new Editor.Model.AnnotationStyle(0xFFFF3B30u, Editor.Model.BrushLevel.Thick);
        form.History.Clear();
        form.History.Add(new Editor.Model.RectAnnotation(
            new PhysRectF(60, 60, w - 120, h - 120),
            style));
        form.History.Add(new Editor.Model.ArrowAnnotation(
            new Editor.Model.PhysPointF(80, h - 100),
            new Editor.Model.PhysPointF(w - 140, h / 2),
            style));

        using Bitmap? plain = form.ComposeSelectionBitmap(includeAnnotations: false);
        using Bitmap? composed = form.ComposeSelectionBitmap(includeAnnotations: true);

        ok &= Check("标注合成：两张图都生成", plain is not null && composed is not null,
            $"plain={plain?.Width}x{plain?.Height} composed={composed?.Width}x{composed?.Height}");

        if (plain is null || composed is null)
        {
            return ok;
        }

        ok &= Check("标注合成：尺寸与选区一致",
            plain.Width == w && plain.Height == h && composed.Width == w && composed.Height == h,
            $"{composed.Width}x{composed.Height} vs selection {w}x{h}");

        // 统计差异像素：矩形描边 + 箭头必然改变一部分像素
        int diff = 0;
        int sampleStep = 2;
        for (int y = 0; y < composed.Height; y += sampleStep)
        {
            for (int x = 0; x < composed.Width; x += sampleStep)
            {
                if (plain.GetPixel(x, y).ToArgb() != composed.GetPixel(x, y).ToArgb())
                {
                    diff++;
                }
            }
        }

        ok &= Check("标注合成：笔迹像素被写入（差异 > 500 个采样点）", diff > 500, $"差异采样点={diff}");

        string composedPath = Path.Combine(outputDirectory, "selftest-compose-annotated.png");
        composed.Save(composedPath, ImageFormat.Png);
        Console.WriteLine($"  标注成品图 → {composedPath}");

        log.Info($"标注合成自测：history={form.History.Items.Count} 条，差异采样点={diff}，产物={composedPath}");
        return ok;
    }

    // ------------------------------------------------------------------ 小工具

    private static string ResolveOutputDirectory()
    {
        string? fromEnv = Environment.GetEnvironmentVariable("SHOTTOOL_SELFTEST_DIR");
        if (!string.IsNullOrWhiteSpace(fromEnv))
        {
            return Path.GetFullPath(fromEnv);
        }

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ShotTool",
            "selftest");
    }

    private static bool Check(string name, bool condition, string detail)
    {
        Report(condition ? "PASS" : "FAIL", name, detail);
        return condition;
    }

    private static void Report(string status, string name, string detail)
    {
        string line = $"[{status}] {name} :: {detail}";
        Console.WriteLine(line);
        Results.Add(line);

        if (status == "FAIL")
        {
            Debug.WriteLine(line);
        }
    }
}
