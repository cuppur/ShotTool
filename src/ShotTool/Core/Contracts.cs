using System.Drawing;
using System.Windows.Forms;

namespace ShotTool.Core;

// ============================================================================
//  Contracts.cs —— 共享契约（四层并行开发的唯一交互面）
//
//  ★ 本文件由 architect 独占维护。改动前必须同步 dev-capture / dev-editor /
//    dev-integration / dev-ai，因为四个方向的代码都实现或消费它。
//
//  分层与唯一交互面（详见 docs/02-architecture.md §6 与 Core/README.md）:
//
//    dev-capture     ──ICaptureSource──► CapturedFrame（Bitmap + 物理像素几何）
//                                            │
//    dev-capture     ── 拥有 overlay 窗口 ───┘  消费 CapturedFrame，
//                     实现 IShotSession / IShotCaptureUi / IOverlayHost
//                     （App/Overlay/SelectionOverlayForm.cs：窗体 + 会话状态机 + 命中测试）
//                    ├──IOutputSink────────► dev-integration（剪贴板 / 保存）
//                    ├──IAnnotationOverlayPainter──► dev-ai（AI 高亮等叠加绘制）
//                    └──IToolbarRegistry ──► dev-ai 注入 OCR / 翻译按钮与面板
//
//    dev-editor      ── 标注渲染与工具条**内容**（被 App 层调用，不反向依赖 App）：
//                     实现 IToolbarHost / IToolbarRegistry / IAnnotationOverlayPainterRegistry，
//                     提供标注模型/算法（Editor/Model、Editor/Algorithms）与图标资源
//
//  ★ 依赖方向（唯一允许，reviewer 必查）：App → Editor → Core，**单向**。
//    不允许 Editor 引用 App；不允许 Core 引用任何上层。Core 只被依赖，不依赖别人。
//
//    dev-integration ──ISettingsStore──────► 所有层（App 配置 + 各层 section 扩展）
//                    ──IOutputSink─────────► Editor 调用
//
//    dev-ai          ──ITextRecognizer / ITranslationProvider──► 注册进 IAiServiceRegistry
//
//  不做的事（重要）:
//    * 本文件**不**定义标注对象模型（矩形/箭头/画笔/文字的数据结构）。
//      归 dev-editor：src/ShotTool/Editor/Model/**。Core 只提供只读视图 IAnnotation
//      （含 double 精度 VisualBounds，便于 Editor 内部用 double 几何无损实现），
//      用于 OCR / 工具条 / 日志等只读消费方。
//    * 本文件**不**定义撤销命令的具体类型。归 dev-editor：Editor/Algorithms/**。
//      Core 只提供只读 + 触发式接口 IUndoStack，Editor 的实现加一行接口声明即可。
//    * 本文件**不**定义视觉常量（配色 / 按钮尺寸 / 工具条布局）。归 dev-editor。
//
//  线程模型契约:
//    * 所有 UI 相关调用（IShotSession、IOverlayHost、IToolbarHost、IToolbarItem、
//      IAnnotationOverlayPainter）都必须在 overlay 所在的 UI 线程执行。
//    * IOutputSink / ITextRecognizer / ITranslationProvider 的 Async 方法可以做 IO，
//      但返回值与回调仍回到 UI 线程（实现方 ConfigureAwait(true) 或显式 marshal）。
//    * CapturedFrame.Dispose 必须在 UI 线程执行（Bitmap 被 UI 线程的 Graphics 持有）。
// ============================================================================

#region 屏幕采集（dev-capture 实现 ICaptureSource）

/// <summary>采集后端。用于日志、诊断与降级可见性。</summary>
public enum CaptureBackend
{
    /// <summary>GDI BitBlt + CAPTUREBLT（P0 默认，见 ADR-002）。</summary>
    GdiBitBlt = 0,

    /// <summary>[P2] Windows.Graphics.Capture（Win10 1803+，可抓被遮挡内容；ICaptureSource 的可插拔备用后端）。</summary>
    WindowsGraphicsCapture = 1,

    /// <summary>[P2] DXGI Desktop Duplication（多帧 / 录屏 / 长截图场景；本阶段不实现）。</summary>
    DxgiDesktopDuplication = 2,
}

/// <summary>单台显示器的几何与 DPI（全部物理像素）。</summary>
public sealed class MonitorInfo
{
    public MonitorInfo(string deviceName, PhysRect bounds, PhysRect workArea, uint dpi, bool isPrimary)
    {
        DeviceName = deviceName ?? string.Empty;
        Bounds = bounds;
        WorkArea = workArea;
        Dpi = DpiHelper.Sanitize(dpi);
        IsPrimary = isPrimary;
    }

    /// <summary>GDI 设备名，如 <c>\\.\DISPLAY1</c>。</summary>
    public string DeviceName { get; }

    /// <summary>显示器完整区域（物理像素）。</summary>
    public PhysRect Bounds { get; }

    /// <summary>去掉任务栏后的工作区（物理像素）。</summary>
    public PhysRect WorkArea { get; }

    /// <summary>该显示器的有效 DPI（96 = 100%）。获取失败时回落到 96，由实现方记录日志。</summary>
    public uint Dpi { get; }

    public bool IsPrimary { get; }

    public double ScaleFactor => DpiHelper.ScaleFactor(Dpi);

    public override string ToString() => $"{DeviceName} {Bounds} dpi={Dpi}";
}

/// <summary>
/// 一次抓屏的结果：虚拟桌面完整位图 + 几何元数据。
///
/// 尺寸契约：<see cref="Bitmap"/> 尺寸 == <see cref="VirtualDesktopBounds"/>.Size，
/// 位图像素 (0,0) 对应虚拟桌面坐标 (VirtualDesktopBounds.X, VirtualDesktopBounds.Y)。
/// 因此「虚拟桌面坐标 → 位图坐标」= 减去 VirtualDesktopBounds.Location。
/// overlay 窗口的 Bounds 即 VirtualDesktopBounds，所以 overlay 客户区坐标 == 虚拟桌面坐标。
/// </summary>
public sealed class CapturedFrame : IDisposable
{
    public CapturedFrame(
        Bitmap bitmap,
        PhysRect virtualDesktopBounds,
        IReadOnlyList<MonitorInfo> monitors,
        PhysPoint cursorPosition,
        CaptureBackend backend,
        DateTime capturedAtUtc)
    {
        Bitmap = bitmap ?? throw new ArgumentNullException(nameof(bitmap));
        VirtualDesktopBounds = virtualDesktopBounds;
        Monitors = monitors ?? Array.Empty<MonitorInfo>();
        CursorPosition = cursorPosition;
        Backend = backend;
        CapturedAtUtc = capturedAtUtc;
    }

    /// <summary>
    /// 虚拟桌面整屏位图。像素格式约定 <see cref="System.Drawing.Imaging.PixelFormat.Format32bppPArgb"/>。
    /// 所有权归本对象，Dispose 时释放。
    /// </summary>
    public Bitmap Bitmap { get; }

    /// <summary>虚拟桌面边界（可为负原点的多屏并集）。</summary>
    public PhysRect VirtualDesktopBounds { get; }

    /// <summary>参与合成的显示器列表；空列表表示采集实现未提供（调用方需降级为单 DPI 假设）。</summary>
    public IReadOnlyList<MonitorInfo> Monitors { get; }

    /// <summary>抓屏瞬间的光标位置（物理像素，虚拟桌面坐标系）。放大镜 / 取色用。</summary>
    public PhysPoint CursorPosition { get; }

    public CaptureBackend Backend { get; }

    public DateTime CapturedAtUtc { get; }

    public bool IsDisposed { get; private set; }

    /// <summary>虚拟桌面坐标 → 位图坐标。</summary>
    public PhysPoint ToBitmapPoint(PhysPoint virtualPoint)
        => new(virtualPoint.X - VirtualDesktopBounds.X, virtualPoint.Y - VirtualDesktopBounds.Y);

    /// <summary>虚拟桌面坐标 → 位图坐标的矩形（已夹进边界）。</summary>
    public Rectangle ToBitmapRect(PhysRect virtualRect)
    {
        PhysRect hit = virtualRect.Clamp(VirtualDesktopBounds);
        return hit.IsEmpty
            ? Rectangle.Empty
            : new Rectangle(hit.X - VirtualDesktopBounds.X, hit.Y - VirtualDesktopBounds.Y, hit.Width, hit.Height);
    }

    /// <summary>找到包含该点的显示器（用于取该屏 DPI）；找不到返回 null。</summary>
    public MonitorInfo? MonitorAt(PhysPoint virtualPoint)
    {
        foreach (MonitorInfo monitor in Monitors)
        {
            if (monitor.Bounds.Contains(virtualPoint))
            {
                return monitor;
            }
        }

        return null;
    }

    public void Dispose()
    {
        if (IsDisposed)
        {
            return;
        }

        IsDisposed = true;
        Bitmap.Dispose();
    }
}

/// <summary>采集参数。</summary>
public sealed class CaptureOptions
{
    /// <summary>是否包含分层窗口（GDI 下对应 CAPTUREBLT 标志）。默认 true，与微信一致。</summary>
    public bool IncludeLayeredWindows { get; init; } = true;

    /// <summary>是否隐藏光标。默认 false（要知道光标位置才能画放大镜）。</summary>
    public bool HideCursor { get; init; }

    /// <summary>抓屏超时（毫秒）。WGC / DXGI 首帧可能阻塞，超时报错而不是永久挂起。</summary>
    public int TimeoutMs { get; init; } = 2000;

    /// <summary>重试次数（对 WGC 首帧偶发空帧有用；GDI 实现可忽略）。</summary>
    public int RetryCount { get; init; } = 1;

    public static CaptureOptions Default { get; } = new();
}

/// <summary>
/// 抓屏抽象。dev-capture 提供至少一个 GDI 实现；后续换 WGC / DXGI 时不改调用方。
/// 调用线程：任意线程可调用；实现内部负责 COM / D3D 初始化。
/// </summary>
public interface ICaptureSource
{
    CaptureBackend Backend { get; }

    /// <summary>当前环境是否可用（例如 WGC 需要 Win10 1803+ 与支持的显示器）。</summary>
    bool IsAvailable();

    /// <summary>
    /// 抓取整个虚拟桌面。失败必须抛 <see cref="CaptureException"/>，不要返回 null。
    /// 返回对象的 Bitmap 所有权转移给调用方，调用方负责 Dispose。
    /// </summary>
    CapturedFrame CaptureVirtualDesktop(CaptureOptions options, CancellationToken cancellationToken = default);
}

/// <summary>采集失败（含后端诊断信息，用于 UI 提示与日志）。</summary>
public sealed class CaptureException : Exception
{
    public CaptureException(string message, CaptureBackend backend, int? win32Error = null, Exception? inner = null)
        : base(message, inner)
    {
        Backend = backend;
        Win32Error = win32Error;
    }

    public CaptureBackend Backend { get; }

    public int? Win32Error { get; }
}

#endregion

#region 标注只读视图与叠加绘制挂点（标注模型本体归 dev-editor）

/// <summary>
/// 标注**只读视图**（Core 只定义「别人怎么看标注」，不定义「标注是什么」）。
///
/// 谁实现它：dev-editor 的 <c>Editor.Model.Annotation</c> 抽象类（<c>Annotation : IAnnotation</c>），
///           只需补一个成员：<c>public PhysRectF VisualBounds =&gt; new(X, Y, W, H);</c>
///           <see cref="Bounds"/> 有默认实现（由 VisualBounds 四舍五入派生），不必重写。
/// 谁消费它：OCR / 翻译（判断该不该包含标注）、工具条扩展按钮、诊断日志。
///
/// 精度约定：
///   * <see cref="VisualBounds"/> —— double，实现方保留原始精度（Editor 内部就是 double 几何）。
///   * <see cref="Bounds"/> —— int 物理像素，契约层消费方使用（裁剪、日志、命中）。
///   两者由同一个 VisualBounds 派生，不会互相矛盾。
///
/// 本接口**不**定义具体标注子类（RectAnnotation / ArrowAnnotation 等属 dev-editor 域）。
/// </summary>
public interface IAnnotation
{
    /// <summary>诊断/日志用的类型名，例如 "Rectangle" / "Mosaic"。</summary>
    string KindName { get; }

    /// <summary>实际绘制颜色（ARGB）。</summary>
    Color Color { get; }

    /// <summary>实际线宽（物理像素）。填充型标注返回 0。</summary>
    double StrokeWidthPx { get; }

    /// <summary>
    /// 亚像素包围盒（物理像素，虚拟桌面坐标系，不含笔宽外扩）。
    /// **实现方必须提供这一个成员**，其余成员可由本接口派生。
    /// </summary>
    PhysRectF VisualBounds { get; }

    /// <summary>整数包围盒（四舍五入自 <see cref="VisualBounds"/>）。默认实现，通常无需重写。</summary>
    PhysRect Bounds => VisualBounds.ToPhysRect();

    /// <summary>空标注（如起点=终点的矩形）返回 true：不绘制、不参与 OCR、不进入导出。</summary>
    bool IsEmpty => VisualBounds.IsEmpty;
}

/// <summary>
/// 叠加绘制上下文。由 dev-editor 在绘制「扩展叠加层」时构造。
/// 所有尺寸已是物理像素，painter 不需要再关心 DPI。
/// </summary>
public sealed class AnnotationPaintContext
{
    /// <summary>当前 overlay 所在显示器 DPI（96 = 100%）。</summary>
    public uint Dpi { get; init; } = DpiHelper.DefaultDpi;

    /// <summary>当前选区（painter 必须自行 clip 在选区内）。</summary>
    public PhysRect Selection { get; init; }

    /// <summary>选区在位图中的偏移（虚拟桌面坐标 → 位图坐标）。</summary>
    public PhysPoint BitmapOrigin { get; init; }

    /// <summary>true = 正在拖拽的预览态（可用更低透明度）。</summary>
    public bool IsPreview { get; init; }
}

/// <summary>
/// [P1] **叠加**绘制挂点：在 Editor 完成「背景 + 遮罩 + 标注 + 选区边框」之后追加绘制。
///
/// 注意区别：
///   * Editor 的标注渲染管线（矩形/箭头/马赛克/文字的栅格化）由 dev-editor 自己实现，
///     **不经过本接口**，不受本接口影响。
///   * 本接口是给第三层（dev-ai）挂「额外视觉」的：AI 打码候选框高亮、
///     OCR 命中行描边、翻译气泡等。Editor 不认识这些逻辑，只按注册顺序调用 Paint。
/// </summary>
public interface IAnnotationOverlayPainter
{
    /// <summary>越小越先绘制（背景类）／越大越靠上（提示类）。建议 100 起。</summary>
    int ZOrder { get; }

    /// <summary>是否参与本次绘制（例如只在 OCR 面板可见时绘制）。</summary>
    bool ShouldPaint(AnnotationPaintContext context);

    /// <summary>绘制。坐标系 = 虚拟桌面物理像素。</summary>
    void Paint(Graphics graphics, AnnotationPaintContext context);
}

/// <summary>叠加绘制挂点注册表。dev-editor 实现并在绘制末尾遍历。</summary>
public interface IAnnotationOverlayPainterRegistry
{
    void Register(IAnnotationOverlayPainter painter);

    void Unregister(IAnnotationOverlayPainter painter);

    /// <summary>按 ZOrder 升序返回全部 painter。</summary>
    IReadOnlyList<IAnnotationOverlayPainter> ResolveAll();

    event EventHandler? Changed;
}

#endregion

#region 撤销栈契约（实现归 dev-editor：Editor/Algorithms）

/// <summary>
/// 撤销栈契约（只读查询 + 触发式操作）。
/// 故意不含 Push / 不含命令类型 —— 命令与栈的实现归 dev-editor，
/// 因此 Editor 的撤销栈加一行接口声明即可实现本契约，无需改造。
/// </summary>
public interface IUndoStack
{
    bool CanUndo { get; }

    bool CanRedo { get; }

    /// <summary>下一次 Undo 的显示名（用于 tooltip「撤销 添加矩形」）；无可撤销时 null。</summary>
    string? NextUndoName { get; }

    /// <summary>下一次 Redo 的显示名；无可重做时 null。</summary>
    string? NextRedoName { get; }

    /// <summary>执行一次撤销。返回 false 表示没有可撤销项。</summary>
    bool Undo();

    /// <summary>执行一次重做。返回 false 表示没有可重做项。</summary>
    bool Redo();

    void Clear();

    /// <summary>栈内容变化（Editor 应据此刷新工具条的撤销/重做按钮可用态）。</summary>
    event EventHandler? Changed;
}

#endregion

#region 截图会话（App 层 = dev-capture 实现；Editor 只提供标注渲染与工具条内容）

/// <summary>会话阶段状态机。</summary>
public enum ShotPhase
{
    /// <summary>未开始（常驻态）。</summary>
    Idle = 0,

    /// <summary>正在抓屏（overlay 尚未显示）。</summary>
    Capturing = 1,

    /// <summary>overlay 已显示、尚未框选出有效选区（全屏遮罩 + 放大镜）。</summary>
    Selecting = 2,

    /// <summary>已有选区（可带标注）。</summary>
    Annotating = 3,

    /// <summary>正在输出（复制 / 保存 / 贴图）。</summary>
    Committing = 4,

    /// <summary>已完成，会话即将销毁。</summary>
    Done = 5,

    /// <summary>已取消（Esc / 右键）。</summary>
    Cancelled = 6,
}

/// <summary>会话变更种类（决定重绘范围与工具条刷新）。</summary>
public enum ShotChangeKind
{
    Phase = 0,
    Selection = 1,
    Annotations = 2,
    ActiveTool = 3,
    ActiveStyle = 4,
    Cursor = 5,
    Layout = 6,
    History = 7,
}

/// <summary>会话变更事件。</summary>
public sealed class ShotSessionChangedEventArgs : EventArgs
{
    public ShotSessionChangedEventArgs(ShotChangeKind kind, PhysRect dirtyRect = default)
    {
        Kind = kind;
        DirtyRect = dirtyRect;
    }

    public ShotChangeKind Kind { get; }

    /// <summary>需要重绘的区域（物理像素，虚拟桌面坐标系）。Empty 表示整窗重绘。</summary>
    public PhysRect DirtyRect { get; }
}

/// <summary>完成方式。</summary>
public enum ShotCompletion
{
    /// <summary>只复制到剪贴板（Enter 默认）。</summary>
    Copy = 0,

    /// <summary>只保存文件（Ctrl+S / 另存为）。</summary>
    Save = 1,

    /// <summary>复制 + 保存。</summary>
    CopyAndSave = 2,

    /// <summary>[P1] 贴图钉在桌面上。</summary>
    Pin = 3,
}

/// <summary>完成结果。</summary>
public sealed class ShotResult
{
    public ShotResult(bool success, string? filePath = null, Exception? error = null)
    {
        Success = success;
        FilePath = filePath;
        Error = error;
    }

    public bool Success { get; }

    public string? FilePath { get; }

    public Exception? Error { get; }

    public static ShotResult Ok(string? filePath = null) => new(true, filePath);

    public static ShotResult Fail(Exception error) => new(false, null, error);
}

/// <summary>
/// overlay 画布能力。由 **App 层（dev-capture）** 的 <c>App/Overlay/SelectionOverlayForm</c> 实现。
/// 只有编辑器内部与 IShotCaptureUi 使用者会碰到它；扩展按钮请用 <see cref="IToolbarHost"/>。
/// </summary>
public interface IOverlayHost
{
    /// <summary>overlay 窗口句柄（SetForegroundWindow / DPI 查询 / 定位工具条用）。</summary>
    IntPtr Handle { get; }

    /// <summary>虚拟桌面边界 == overlay 窗口 Bounds == 客户区坐标系。</summary>
    PhysRect VirtualBounds { get; }

    /// <summary>overlay 所在显示器 DPI（把 DIP 换算成物理像素）。</summary>
    uint Dpi { get; }

    /// <summary>请求重绘指定区域（物理像素）。</summary>
    void Invalidate(PhysRect rect);

    /// <summary>请求整窗重绘。</summary>
    void InvalidateAll();

    /// <summary>让 overlay 重新获得键盘焦点（关闭面板 / 文字输入结束后调用）。</summary>
    void Refocus();

    /// <summary>轻提示（不抢焦点，约 2 秒自动消失）。</summary>
    void ShowToast(string text);
}

/// <summary>
/// 一次截图会话。由 **App 层（dev-capture）** 实现（<c>App/Overlay/SelectionOverlayForm.cs</c> 持有会话状态机）。
///
/// 本接口只暴露「外部需要的只读状态 + 框选 + 完成/取消」。
/// 标注的增删改、工具切换、颜色线宽、文字输入等**全部在 Editor 内部**，
/// 不进本契约 —— 这样 dev-editor 可以自由演进标注模型而不影响其他层。
/// </summary>
public interface IShotSession : IDisposable
{
    ShotPhase Phase { get; }

    /// <summary>本次会话的抓屏帧。会话存活期间有效，Dispose 时释放。</summary>
    CapturedFrame Frame { get; }

    /// <summary>当前选区（物理像素，虚拟桌面坐标系）。null = 尚未框选 / 框选被清空。</summary>
    PhysRect? Selection { get; }

    /// <summary>已提交标注的只读视图，按绘制顺序（后加的在上面）。</summary>
    IReadOnlyList<IAnnotation> Annotations { get; }

    /// <summary>撤销栈契约（只读查询 + 触发）。</summary>
    IUndoStack Undo { get; }

    /// <summary>会话任意状态变化（每次变化只触发一次）。</summary>
    event EventHandler<ShotSessionChangedEventArgs>? SessionChanged;

    /// <summary>设置选区（null = 清空）。Editor 会校验并夹进虚拟桌面。</summary>
    void SetSelection(PhysRect? selection);

    /// <summary>
    /// 合成选区位图。includeAnnotations = false 返回原图裁剪（OCR 通常用原图，
    /// 也可以选择识别「用户看到的」= 含标注，由调用方决定）。
    /// 返回的 Bitmap 所有权归调用方。无选区时返回 null。
    /// </summary>
    Bitmap? ComposeSelectionBitmap(bool includeAnnotations);

    /// <summary>取消会话（Esc / 右键）。释放帧与标注，触发 Phase = Cancelled。</summary>
    void Cancel();

    /// <summary>完成会话：合成 → 输出 → 关闭。失败返回 ShotResult.Fail，不抛。</summary>
    Task<ShotResult> CompleteAsync(ShotCompletion mode, CancellationToken cancellationToken = default);
}

/// <summary>
/// 一次截图 UI 会话的启动器：由 **App 层（dev-capture）** 实现（<c>App/Overlay/OverlayShotUi</c>），
/// 由托盘/热键调用。这是 App 内部与 Editor/Integration 的唯一调用面。
/// 这是 App 层与 Editor 层**唯一**的调用面。
/// </summary>
public interface IShotCaptureUi
{
    /// <summary>是否有会话正在进行（防重入：连按 Alt+A 不应叠加 overlay）。</summary>
    bool IsRunning { get; }

    /// <summary>
    /// 完整跑一次截图：抓屏 → 显示 overlay → 用户交互 → 输出 → 收尾。
    /// <paramref name="forcedMode"/> 为 null 时由用户操作决定（Enter=复制 / Ctrl+S=保存）。
    /// 不会抛异常；失败信息放在 <see cref="ShotResult.Error"/>。
    /// </summary>
    Task<ShotResult> RunAsync(ShotCompletion? forcedMode = null, CancellationToken cancellationToken = default);

    /// <summary>请求取消当前会话（热键重按 / 托盘菜单「取消截图」）。</summary>
    void RequestCancel();
}

#endregion

#region 输出（dev-integration 实现 IOutputSink）

/// <summary>图片格式。</summary>
public enum ImageFileFormat
{
    Png = 0,
    /// <summary>[P1]</summary>
    Jpeg = 1,
    /// <summary>[P1]</summary>
    Bmp = 2,
}

/// <summary>保存请求。</summary>
public sealed class SaveRequest
{
    /// <summary>目标目录。null = 使用 <see cref="ShotToolOptions.SaveDirectory"/>。</summary>
    public string? Directory { get; init; }

    /// <summary>文件名（不含扩展名）。null = 按时间戳自动生成。</summary>
    public string? FileName { get; init; }

    public ImageFileFormat Format { get; init; } = ImageFileFormat.Png;

    /// <summary>[P1] true = 弹「另存为」对话框。</summary>
    public bool PromptUser { get; init; }

    /// <summary>是否写入 DPI 元数据（PNG pHYs）。默认 true，避免高 DPI 屏上打开偏小。</summary>
    public bool EmbedDpi { get; init; } = true;
}

/// <summary>
/// 输出通道。全部异步（剪贴板与磁盘可能阻塞 UI 线程）。
/// 实现方（dev-integration）负责所有 Win32 细节（OpenClipboard / CF_DIB / CF_DIBV5 / PNG）。
/// </summary>
public interface IOutputSink
{
    /// <summary>复制到剪贴板（写 CF_DIB + CF_DIBV5 + PNG，与微信一致并兼容更多宿主）。</summary>
    Task CopyToClipboardAsync(Bitmap image, CancellationToken cancellationToken = default);

    /// <summary>保存到文件，返回完整路径。</summary>
    Task<string> SaveAsync(Bitmap image, SaveRequest request, CancellationToken cancellationToken = default);

    /// <summary>仅解析目标路径（不写盘），用于 UI 预览与「另存为」默认值。</summary>
    string ResolvePath(SaveRequest request);
}

#endregion

#region 工具条扩展挂点（dev-ai 通过它插入 OCR / 翻译，不改 Editor 内部文件）

/// <summary>
/// 工具条分区（从左到右）。
/// 工具条**画在 overlay 窗口内**（同一 HWND，见 ADR-003），本枚举只表达顺序语义，
/// 不暗示独立窗口。
/// </summary>
public enum ToolbarSlot
{
    /// <summary>最左侧：关闭 / 取消。</summary>
    Left = 0,

    /// <summary>主工具组：矩形 / 椭圆 / 箭头 / 画笔 / 马赛克 / 文字（Editor 拥有）。</summary>
    Tools = 1,

    /// <summary>中间：颜色 / 线宽 / 文字档位（Editor 拥有）。</summary>
    Style = 2,

    /// <summary>右侧：撤销 / 重做 / 复制 / 保存 / 贴图（Editor 拥有）+ 扩展按钮（dev-ai 注入）。</summary>
    Right = 3,

    /// <summary>溢出区（宽度不足时折叠到这里）。</summary>
    Overflow = 4,
}

/// <summary>
/// 图标指令。Editor 提供自绘实现（不引用任何腾讯资源文件；项目内图标为自绘 SVG path data）。
/// dev-ai 新增按钮优先复用已有 Glyph；确实需要新图标则加枚举值并同步给 dev-editor。
/// </summary>
public enum ToolbarGlyph
{
    None = 0,
    Rect = 1,
    Ellipse = 2,
    Arrow = 3,
    Pen = 4,
    Mosaic = 5,
    Text = 6,
    /// <summary>[P1] 序号标注。</summary>
    Number = 7,
    Undo = 8,
    Redo = 9,
    Copy = 10,
    Save = 11,
    Pin = 12,
    Close = 13,
    Collapse = 14,

    /// <summary>文字识别（OCR）。</summary>
    Ocr = 20,

    /// <summary>[P2] 翻译。</summary>
    Translate = 21,

    /// <summary>[P2] AI 打码 / 智能遮挡。</summary>
    AiMask = 22,
}

/// <summary>面板停靠位置（由 Editor 决定实际呈现方式：overlay 内自绘面板或 owned 小窗口）。</summary>
public enum PanelPlacement
{
    /// <summary>overlay 右侧（竖直长条，适合 OCR 结果列表）。</summary>
    Right = 0,

    /// <summary>overlay 底部（横向长条，适合翻译结果）。</summary>
    Bottom = 1,

    /// <summary>[P1] 浮在指定点附近（由 <see cref="ToolbarPanelRequest.Anchor"/> 决定）。</summary>
    Floating = 2,
}

/// <summary>
/// 面板挂载请求。Content 是真正的 WinForms 控件，由调用方（dev-ai）创建并拥有；
/// Editor 只负责把它放进宿主并定位显示。
/// Editor 关闭面板时 Dispose 宿主但**不** Dispose Content（所有权归还调用方）。
/// </summary>
public sealed class ToolbarPanelRequest
{
    public ToolbarPanelRequest(Control content, PanelPlacement placement, string title)
    {
        Content = content ?? throw new ArgumentNullException(nameof(content));
        Placement = placement;
        Title = title ?? string.Empty;
    }

    public Control Content { get; }

    public PanelPlacement Placement { get; }

    public string Title { get; }

    /// <summary>Floating 时的锚点（物理像素，虚拟桌面坐标）。</summary>
    public PhysPoint Anchor { get; init; }

    /// <summary>期望尺寸（DIP）。Empty = 由 Content 的 PreferredSize 决定。</summary>
    public PhysSize PreferredSizeDip { get; init; } = PhysSize.Empty;

    /// <summary>面板关闭回调（调用方据此做清理）。</summary>
    public Action? Closed { get; init; }
}

/// <summary>
/// 工具条宿主能力（**由 App 层实现，因为工具条与 overlay 同窗口自绘**；绘制内容来自 Editor 的 IToolbarRegistry + painter）。
/// 因此这里只有「重建 / 失效 / 面板 / 提示」四类动作，不暴露任何窗口类型。
/// </summary>
public interface IToolbarHost
{
    /// <summary>当前 DPI（把 DIP 换算成物理像素用）。</summary>
    uint Dpi { get; }

    /// <summary>重新解析 <see cref="IToolbarRegistry"/> 并重建按钮列表（注册表变化后调用）。</summary>
    void Rebuild();

    /// <summary>工具条区域失效重绘（悬停/按下/可用态变化后调用）。</summary>
    void Invalidate();

    /// <summary>
    /// 挂出面板（OCR 结果 / 翻译结果）。传 null 关闭当前面板。
    /// 同一时刻只存在一个 panel；重复调用替换旧的（旧面板的 Closed 回调会触发）。
    /// </summary>
    void ShowPanel(ToolbarPanelRequest? request);

    /// <summary>轻提示。</summary>
    void ShowToast(string text);
}

/// <summary>
/// 工具条上下文：扩展按钮能做的事情的**受限**集合。
/// 故意不暴露 OverlayForm / ScreenshotSession 具体类型，避免 dev-ai 与 Editor 内部耦合。
/// </summary>
public sealed class ToolbarContext
{
    public ToolbarContext(
        IShotSession session,
        IOutputSink output,
        IOverlayHost overlay,
        IToolbarHost toolbar,
        Func<Bitmap?> selectionBitmapProvider,
        Action<string> showToast,
        Action closeOverlay)
    {
        Session = session ?? throw new ArgumentNullException(nameof(session));
        Output = output ?? throw new ArgumentNullException(nameof(output));
        Overlay = overlay ?? throw new ArgumentNullException(nameof(overlay));
        Toolbar = toolbar ?? throw new ArgumentNullException(nameof(toolbar));
        SelectionBitmapProvider = selectionBitmapProvider ?? throw new ArgumentNullException(nameof(selectionBitmapProvider));
        ShowToast = showToast ?? throw new ArgumentNullException(nameof(showToast));
        CloseOverlay = closeOverlay ?? throw new ArgumentNullException(nameof(closeOverlay));
    }

    public IShotSession Session { get; }

    public IOutputSink Output { get; }

    public IOverlayHost Overlay { get; }

    public IToolbarHost Toolbar { get; }

    /// <summary>
    /// 同步取当前选区合成图（含标注）。返回 null = 还没有选区。
    /// 返回的 Bitmap 由调用方 Dispose（不缓存在会话里）。
    /// 大图（&gt;2000×2000）请在后台线程处理后回 UI 线程。
    /// </summary>
    public Func<Bitmap?> SelectionBitmapProvider { get; }

    /// <summary>显示一条轻提示。</summary>
    public Action<string> ShowToast { get; }

    /// <summary>关闭 overlay（例如「提取文字」后立刻收起截图层并弹独立结果窗）。</summary>
    public Action CloseOverlay { get; }
}

/// <summary>
/// 工具条按钮实现契约。dev-ai 实现它，Editor 只按接口渲染与派发。
/// 这是「工具条可扩展挂点」的核心：Editor 完全不认识 OCR / 翻译，只认识 IToolbarItem。
/// </summary>
public interface IToolbarItem
{
    /// <summary>唯一 id，例如 "ai.ocr" / "ai.translate"。用于配置持久化与去重。</summary>
    string Id { get; }

    ToolbarSlot Slot { get; }

    /// <summary>同槽位内排序键（小的在左）。</summary>
    int Order { get; }

    /// <summary>悬停提示（可为空）。</summary>
    string Tooltip { get; }

    ToolbarGlyph Glyph { get; }

    /// <summary>是否显示（例如未安装 OCR 引擎时隐藏而不是灰显）。</summary>
    bool IsVisible(ToolbarContext context);

    bool IsEnabled(ToolbarContext context);

    /// <summary>是否为切换型按钮（如「显示原文 / 译文」）。</summary>
    bool IsToggle { get; }

    /// <summary>切换型按钮当前状态。</summary>
    bool IsChecked { get; }

    /// <summary>点击。Editor 会捕获异常并 ShowToast，不要让异常冒到消息循环。</summary>
    void OnClick(ToolbarContext context);
}

/// <summary>扩展按钮提供者。实现后注册到 <see cref="IToolbarRegistry"/> 即生效。</summary>
public interface IToolbarItemProvider
{
    IEnumerable<IToolbarItem> GetItems();

    /// <summary>按钮状态可能变化（例如识别完成）时触发，Editor 会重绘工具条。</summary>
    event EventHandler? ItemsChanged;
}

/// <summary>
/// 工具条按钮注册表。dev-editor 实现并在构建工具条时枚举；
/// dev-ai 在 App 启动时 Register 自己的 provider。
/// </summary>
public interface IToolbarRegistry
{
    void Register(IToolbarItemProvider provider);

    void Unregister(IToolbarItemProvider provider);

    /// <summary>
    /// 解析当前应显示的按钮（已按 Slot / Order 排序、已过滤 IsVisible）。
    /// 每次工具条重建时调用。
    /// </summary>
    IReadOnlyList<IToolbarItem> Resolve(ToolbarContext context);

    event EventHandler? ItemsChanged;
}

#endregion

#region OCR / 翻译（dev-ai 实现，只做可插拔后端）

/// <summary>OCR 参数。</summary>
public sealed class OcrOptions
{
    /// <summary>BCP-47 语言标签。本机 Windows.Media.Ocr 已装 zh-Hans-CN。</summary>
    public string LanguageTag { get; init; } = "zh-Hans-CN";

    /// <summary>true = 尽量保留原始排版（按行聚类 + 空格推断）。</summary>
    public bool PreserveLayout { get; init; } = true;

    /// <summary>是否把结果里的换行替换成空格（单行输出）。</summary>
    public bool SingleLine { get; init; }
}

/// <summary>OCR 单词。</summary>
public sealed class OcrWord
{
    public OcrWord(string text, PhysRect bounds, float confidence = 1.0f)
    {
        Text = text ?? string.Empty;
        Bounds = bounds;
        Confidence = confidence;
    }

    public string Text { get; }

    /// <summary>相对源图像的包围盒（物理像素，源图坐标系）。</summary>
    public PhysRect Bounds { get; }

    /// <summary>0..1。引擎不提供时按 1 处理。</summary>
    public float Confidence { get; }
}

/// <summary>OCR 行。</summary>
public sealed class OcrLine
{
    public OcrLine(string text, PhysRect bounds, IReadOnlyList<OcrWord> words)
    {
        Text = text ?? string.Empty;
        Bounds = bounds;
        Words = words ?? Array.Empty<OcrWord>();
    }

    public string Text { get; }

    public PhysRect Bounds { get; }

    public IReadOnlyList<OcrWord> Words { get; }
}

/// <summary>OCR 结果。</summary>
public sealed class OcrResult
{
    public OcrResult(string engineId, IReadOnlyList<OcrLine> lines, string plainText, TimeSpan elapsed)
    {
        EngineId = engineId ?? string.Empty;
        Lines = lines ?? Array.Empty<OcrLine>();
        PlainText = plainText ?? string.Empty;
        Elapsed = elapsed;
    }

    /// <summary>产生结果的引擎 id，与 <see cref="ITextRecognizer.Id"/> 对应。</summary>
    public string EngineId { get; }

    public IReadOnlyList<OcrLine> Lines { get; }

    /// <summary>整段文本（按 PreserveLayout / SingleLine 处理后的样子）。</summary>
    public string PlainText { get; }

    public TimeSpan Elapsed { get; }

    public static OcrResult Empty(string engineId) => new(engineId, Array.Empty<OcrLine>(), string.Empty, TimeSpan.Zero);
}

/// <summary>
/// 文字识别引擎。
///
/// 选型（ADR-006）：
///   * 默认实现 <c>Ai/WindowsMediaOcrRecognizer.cs</c>：Windows.Media.Ocr，零体积，
///     依赖系统已装语言包（本机 zh-Hans-CN 已装）。
///   * 可选实现 <c>Ai/PaddleOcrOnnxRecognizer.cs</c>：ONNX Runtime + ~15 MB 模型（Apache-2.0），
///     系统语言包缺失或需要更高精度时启用。
///   * **禁止**依赖、加载、提取或分发腾讯的 WeChatOcr.bin / wxocr.dll / .xnet 模型。
/// </summary>
public interface ITextRecognizer
{
    /// <summary>稳定 id，例如 "windows.media.ocr" / "paddle.onnx"。</summary>
    string Id { get; }

    string DisplayName { get; }

    /// <summary>引擎与语言包 / 模型是否就绪。false 时 Editor 应隐藏按钮而不是报错。</summary>
    bool IsAvailable { get; }

    /// <summary>识别。图像所有权仍在调用方。</summary>
    Task<OcrResult> RecognizeAsync(Bitmap image, OcrOptions options, CancellationToken cancellationToken = default);
}

/// <summary>
/// [P2] 翻译后端。本项目**只做可插拔接口 + 默认不打包任何权重**（ADR-006）：
///   * 默认无后端（按钮隐藏），用户在设置里配置后才启用；
///   * 可选本地后端（例如 ONNX 的 opus-mt）由 dev-ai 在 <c>Ai/</c> 内实现；
///   * 任何实现都不得内置第三方密钥，也不得在未告知的情况下把截图内容外发。
/// </summary>
public interface ITranslationProvider
{
    string Id { get; }

    string DisplayName { get; }

    /// <summary>是否已配置（填了 API Key 或本地模型存在）。</summary>
    bool IsConfigured { get; }

    Task<string> TranslateAsync(string text, string fromLanguageTag, string toLanguageTag, CancellationToken cancellationToken = default);
}

/// <summary>AI 能力注册表。dev-ai 启动时注册，Editor / 工具条只按接口消费。</summary>
public interface IAiServiceRegistry
{
    void Register(ITextRecognizer recognizer);

    void Register(ITranslationProvider provider);

    IReadOnlyList<ITextRecognizer> Recognizers { get; }

    IReadOnlyList<ITranslationProvider> Translations { get; }

    /// <summary>优先返回 <see cref="ShotToolOptions.PreferredRecognizerId"/> 对应引擎；不可用则回落到第一个 IsAvailable 的。</summary>
    ITextRecognizer? ResolveDefaultRecognizer();

    event EventHandler? Changed;
}

#endregion

#region 热键、设置与服务定位

/// <summary>全局热键定义。</summary>
public readonly struct HotkeyGesture : IEquatable<HotkeyGesture>
{
    public const uint ModAlt = 0x0001;
    public const uint ModControl = 0x0002;
    public const uint ModShift = 0x0004;
    public const uint ModWin = 0x0008;

    /// <summary>MOD_NOREPEAT：长按不重复触发（RegisterHotKey 用）。</summary>
    public const uint ModNoRepeat = 0x4000;

    public HotkeyGesture(uint modifiers, uint virtualKey)
    {
        Modifiers = modifiers & (ModAlt | ModControl | ModShift | ModWin);
        VirtualKey = virtualKey;
    }

    public uint Modifiers { get; }

    public uint VirtualKey { get; }

    public bool IsEmpty => VirtualKey == 0;

    /// <summary>用于 <c>RegisterHotKey</c> 的标志（含 MOD_NOREPEAT）。</summary>
    public uint RegisterFlags => Modifiers | ModNoRepeat;

    public static HotkeyGesture None => default;

    /// <summary>默认截图热键 Alt+A（与微信一致，见 docs/01-recon.md §5.1-A）。</summary>
    public static HotkeyGesture AltA => new(ModAlt, (uint)Keys.A);

    public static bool TryParse(string? text, out HotkeyGesture gesture)
    {
        gesture = None;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        uint modifiers = 0;
        uint virtualKey = 0;

        foreach (string rawPart in text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (rawPart.ToLowerInvariant())
            {
                case "alt":
                    modifiers |= ModAlt;
                    break;
                case "ctrl":
                case "control":
                    modifiers |= ModControl;
                    break;
                case "shift":
                    modifiers |= ModShift;
                    break;
                case "win":
                case "windows":
                    modifiers |= ModWin;
                    break;
                default:
                    if (!TryParseKey(rawPart, out virtualKey))
                    {
                        return false;
                    }

                    break;
            }
        }

        if (virtualKey == 0)
        {
            return false;
        }

        gesture = new HotkeyGesture(modifiers, virtualKey);
        return true;
    }

    private static bool TryParseKey(string text, out uint virtualKey)
    {
        virtualKey = 0;

        if (text.Length == 1)
        {
            char c = char.ToUpperInvariant(text[0]);
            if (c is >= 'A' and <= 'Z' or >= '0' and <= '9')
            {
                virtualKey = c;
                return true;
            }
        }

        if (text.Length is 2 or 3 && (text[0] is 'f' or 'F') && int.TryParse(text.AsSpan(1), out int fn)
            && fn is >= 1 and <= 24)
        {
            virtualKey = (uint)(0x70 + fn - 1); // VK_F1 = 0x70
            return true;
        }

        if (Enum.TryParse(text, ignoreCase: true, out Keys key) && key != Keys.None)
        {
            virtualKey = (uint)key;
            return true;
        }

        return false;
    }

    public override string ToString()
    {
        if (IsEmpty)
        {
            return "(未设置)";
        }

        var parts = new List<string>(4);
        if ((Modifiers & ModControl) != 0)
        {
            parts.Add("Ctrl");
        }

        if ((Modifiers & ModAlt) != 0)
        {
            parts.Add("Alt");
        }

        if ((Modifiers & ModShift) != 0)
        {
            parts.Add("Shift");
        }

        if ((Modifiers & ModWin) != 0)
        {
            parts.Add("Win");
        }

        string keyName = Enum.IsDefined(typeof(Keys), (int)VirtualKey)
            ? ((Keys)VirtualKey).ToString()
            : $"0x{VirtualKey:X2}";
        parts.Add(keyName);
        return string.Join('+', parts);
    }

    public bool Equals(HotkeyGesture other) => Modifiers == other.Modifiers && VirtualKey == other.VirtualKey;

    public override bool Equals(object? obj) => obj is HotkeyGesture other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(Modifiers, VirtualKey);

    public static bool operator ==(HotkeyGesture a, HotkeyGesture b) => a.Equals(b);

    public static bool operator !=(HotkeyGesture a, HotkeyGesture b) => !a.Equals(b);
}

/// <summary>
/// 应用级设置（App / 常驻 / 输出 / OCR 语境）。默认值即「微信 4.x 行为基线」。
///
/// 视觉与标注的默认值（颜色 / 线宽 / 字号 / 工具条外观）**不在这里** ——
/// 归 dev-editor 的 <c>EditorOptions</c>，通过 <see cref="ISettingsStore"/> 的
/// section 机制持久化（"editor" 段）。
/// </summary>
public sealed class ShotToolOptions
{
    // ---- 热键 ----
    public HotkeyGesture CaptureHotkey { get; set; } = HotkeyGesture.AltA;

    /// <summary>贴图热键（P1）。默认 F3 但关闭，避免与其它工具冲突。</summary>
    public HotkeyGesture PinHotkey { get; set; } = new(0, (uint)Keys.F3);

    public bool EnablePinHotkey { get; set; }

    // ---- overlay 交互 ----
    public bool ShowMagnifier { get; set; } = true;

    public int MagnifierZoom { get; set; } = 8;

    public bool ShowSizeHud { get; set; } = true;

    /// <summary>复制后是否自动关闭 overlay。</summary>
    public bool CloseAfterCopy { get; set; } = true;

    /// <summary>框选松开后是否直接进入标注态（微信：点「编辑」才进入）。</summary>
    public bool AutoEnterAnnotateMode { get; set; } = true;

    // ---- 输出 ----
    /// <summary>空 = 运行时解析到 <c>Pictures\ShotTool</c>。</summary>
    public string SaveDirectory { get; set; } = string.Empty;

    public ImageFileFormat SaveFormat { get; set; } = ImageFileFormat.Png;

    public bool CopyOnSave { get; set; } = true;

    public bool PlayShutterSound { get; set; }

    // ---- 常驻 ----
    /// <summary>[P1] 开机自启（HKCU Run 项由 dev-integration 实现）。</summary>
    public bool StartupWithWindows { get; set; }

    public bool ShowTrayNotifications { get; set; } = true;

    // ---- OCR / 翻译 ----
    public string OcrLanguageTag { get; set; } = "zh-Hans-CN";

    /// <summary>首选 OCR 引擎 id；空 = 自动选第一个可用的。</summary>
    public string PreferredRecognizerId { get; set; } = "windows.media.ocr";

    /// <summary>OCR 输入是否包含已绘制的标注（true = 用户看到什么就识别什么）。</summary>
    public bool OcrIncludeAnnotations { get; set; } = true;

    public string TranslationSourceLanguageTag { get; set; } = "zh-Hans";

    public string TranslationTargetLanguageTag { get; set; } = "en";
}

/// <summary>
/// 设置持久化。dev-integration 实现（<c>Settings/JsonSettingsStore.cs</c>，
/// 默认落在 <c>%LOCALAPPDATA%\ShotTool\settings.json</c>）。
///
/// 设计要点：App 级的强类型选项在 <see cref="App"/> 上；
/// 其它层（Editor / Ai）用自己的 POCO 序列化到独立 section，互不阻塞、互不冲突。
/// </summary>
public interface ISettingsStore
{
    /// <summary>配置文件完整路径（用于「打开配置目录」菜单）。</summary>
    string FilePath { get; }

    ShotToolOptions App { get; }

    /// <summary>
    /// 读取一个自定义段落（原样 JSON 字符串）。section 不存在返回 null。
    /// 约定 section 名："editor"（dev-editor）、"ai"（dev-ai）。
    /// </summary>
    string? GetSection(string name);

    /// <summary>写入一个自定义段落（原样 JSON 字符串，传 null 删除）。实现方负责线程安全与落盘节流。</summary>
    void SetSection(string name, string? json);

    /// <summary>立即落盘。</summary>
    void Save();

    /// <summary>设置变化（外部改写配置文件后触发，供热重载）。</summary>
    event EventHandler? Changed;
}

/// <summary>全局热键服务。dev-capture 实现（RegisterHotKey + 消息窗口）。</summary>
public interface IGlobalHotkeyService : IDisposable
{
    /// <summary>
    /// 注册热键。回调在 UI 线程执行。
    /// 返回 false 表示被其它程序占用（应提示用户换键，而不是静默失败）。
    /// </summary>
    bool TryRegister(string name, HotkeyGesture gesture, Action callback);

    void Unregister(string name);

    void UnregisterAll();

    event EventHandler<HotkeyRegistrationFailedEventArgs>? RegistrationFailed;
}

/// <summary>热键注册失败事件参数。</summary>
public sealed class HotkeyRegistrationFailedEventArgs : EventArgs
{
    public HotkeyRegistrationFailedEventArgs(string name, HotkeyGesture gesture, int win32Error)
    {
        Name = name;
        Gesture = gesture;
        Win32Error = win32Error;
    }

    public string Name { get; }

    public HotkeyGesture Gesture { get; }

    public int Win32Error { get; }
}

#endregion

#region 组合根与日志（dev-capture 装配，其它层按契约取用）

/// <summary>极简日志抽象。避免各层直接 Console / 文件，便于统一落盘与诊断。</summary>
public interface IAppLog
{
    void Info(string message);

    void Warn(string message);

    void Error(string message, Exception? error = null);
}

/// <summary>
/// 组合根：装配期由 dev-capture 的 Program.cs 依次填充，之后全程只读使用。
/// 属性可写是为了让四层能按任意顺序接入（骨架期允许部分为 null），
/// 使用方必须自行检查自己依赖的项非 null。
/// </summary>
public sealed class ShotToolContext
{
    public ShotToolContext(IAppLog log)
    {
        Log = log ?? throw new ArgumentNullException(nameof(log));
    }

    public IAppLog Log { get; }

    /// <summary>dev-capture 提供。</summary>
    public ICaptureSource? Capture { get; set; }

    /// <summary>dev-integration 提供。</summary>
    public IOutputSink? Output { get; set; }

    /// <summary>dev-integration 提供。</summary>
    public ISettingsStore? Settings { get; set; }

    /// <summary>dev-capture 提供。</summary>
    public IGlobalHotkeyService? Hotkeys { get; set; }

    /// <summary>dev-editor 提供（工具条按钮注册表）。</summary>
    public IToolbarRegistry? ToolbarExtensions { get; set; }

    /// <summary>dev-editor 提供（叠加绘制挂点注册表）。</summary>
    public IAnnotationOverlayPainterRegistry? Painters { get; set; }

    /// <summary>dev-ai 提供。</summary>
    public IAiServiceRegistry? Ai { get; set; }

    /// <summary>dev-editor 提供（截图 UI 门面）。App 层的托盘 / 热键调用它。</summary>
    public IShotCaptureUi? CaptureUi { get; set; }

    /// <summary>装配完成度（用于骨架期诊断，不用于运行时分支）。</summary>
    public bool IsComplete
        => Capture is not null && Output is not null && Settings is not null && CaptureUi is not null;

    /// <summary>列出缺失的装配项（骨架期托盘会显示这条诊断）。</summary>
    public IReadOnlyList<string> DescribeMissing()
    {
        var missing = new List<string>(8);
        if (Capture is null)
        {
            missing.Add("ICaptureSource (dev-capture)");
        }

        if (Output is null)
        {
            missing.Add("IOutputSink (dev-integration)");
        }

        if (Settings is null)
        {
            missing.Add("ISettingsStore (dev-integration)");
        }

        if (CaptureUi is null)
        {
            missing.Add("IShotCaptureUi (dev-editor)");
        }

        if (ToolbarExtensions is null)
        {
            missing.Add("IToolbarRegistry (dev-editor)");
        }

        if (Painters is null)
        {
            missing.Add("IAnnotationOverlayPainterRegistry (dev-editor)");
        }

        if (Ai is null)
        {
            missing.Add("IAiServiceRegistry (dev-ai)");
        }

        return missing;
    }
}

#endregion
