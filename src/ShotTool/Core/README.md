# `ShotTool.Core` —— 共享契约（architect 独占维护）

本目录只有两个文件，是全项目**唯一的跨层契约来源**。三层开发都实现或消费它，因此**改动前必须在群里同步**，不要单方面改签名。

| 文件 | 内容 |
|---|---|
| `Geometry.cs` | `PhysPoint` / `PhysSize` / `PhysRect`（**int，物理像素**）、`PhysRectF`（double，供标注模型保留亚像素精度）、`SelectionHandle`、`SelectionGeometry`（8 手柄命中与拖拽求解）、`DpiHelper`（唯一的 DIP↔px 换算） |
| `Contracts.cs` | 全部接口 + 枚举 + 只读数据结构 + 组合根 `ShotToolContext`。无 IO、无 UI、无状态机实现 |

## 坐标系（全项目唯一约定）

int 物理像素、虚拟桌面坐标系、矩形半开区间 `[Left,Right)×[Top,Bottom)`；`GetSystemMetrics` 读到的即物理像素（进程是 PerMonitorV2）。UI 元素尺寸才用 DIP，且必须经 `DpiHelper.DipToPx`。

## 谁实现、谁调用

| 契约 | 实现者 | 调用者 |
|---|---|---|
| `ICaptureSource` → `CapturedFrame` | **dev-capture**（`Capture/GdiCaptureSource.cs`） | dev-editor（`IShotCaptureUi.RunAsync` 内部） |
| `IShotSession` / `IOverlayHost` / `IToolbarHost` / `IToolbarRegistry` / `IAnnotationOverlayPainterRegistry` / `IShotCaptureUi` | **dev-editor**（`Editor/**`） | dev-capture（托盘点「截图」→ `IShotCaptureUi`）、dev-ai（注册按钮/挂 painter） |
| `IOutputSink` / `ISettingsStore` | **dev-integration**（`Output/**`、`Settings/**`） | dev-editor（复制/保存）、dev-capture（读热键与输出选项）、dev-ai（读 OCR 配置） |
| `ITextRecognizer` / `ITranslationProvider` / `IAiServiceRegistry` / `IToolbarItem(Provider)` | **dev-ai**（`Ai/**`） | dev-editor（枚举 OCR/翻译按钮）、dev-capture（装配注册表） |
| `IUndoStack`（只读+触发）、`IAnnotation`（只读视图） | **dev-editor** 的模型层实现 | OCR/翻译/工具条/日志等只读消费方 |

## dev-editor 适配 `IAnnotation`（共 2 行）

```csharp
// Editor/Model/Annotation.cs —— 抽象基类加接口声明
public abstract class Annotation : ShotTool.Core.IAnnotation
{
    public abstract string KindName { get; }
    public abstract System.Drawing.Color Color { get; }
    public abstract double StrokeWidthPx { get; }
    public ShotTool.Core.PhysRectF VisualBounds => new(X, Y, Width, Height); // ← 唯一需要新增的成员
    // Bounds / IsEmpty 由接口默认实现从 VisualBounds 派生，无需重写
}
```

`IAnnotation` 只要求 `VisualBounds`（double），**不会**把内部 double 几何逼成整型。

## 几何映射（两套 `Phys*` 并存，勿混用）

- `ShotTool.Core.PhysRect/PhysPoint/PhysSize` = **int**，契约与采集/输出层用；
- `ShotTool.Editor.*`（dev-editor 的模型层）= **double**，算法内部用；
- 互转：`PhysRectF.FromPhysRect(...)` / `PhysRect.From(x, y, w, h)`（四舍五入）；字段名一致可逐字段拷贝。
- C# 名字解析：`ShotTool.Editor.*` 内部写 `PhysRect` 会优先解析到 Editor 版本，不会歧义；跨层显式限定 `ShotTool.Core.PhysRect`。

## 约定

1. 本目录**不放**标注子类（`RectAnnotation` 等）、撤销命令、视觉常量（配色/尺寸/图标）—— 这些分别归 `Editor/Model`、`Editor/Algorithms`、`Editor/Theme`。
2. 新增跨层字段/接口时，同时补 `docs/02-architecture.md` 的契约表与本文件的「谁实现、谁调用」表。
3. 标记 `[P2]` 的成员是可选/未来项，P0 阶段可以不实现，但签名已定，实现时不要改。
