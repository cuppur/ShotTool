# Assets/icons —— 自绘图标几何真相源

本目录所有 SVG 均为**本项目手工绘制**（24×24 线性网格，`stroke-width: 1.6`，`stroke: currentColor`），
不包含、不派生、不复制任何第三方（含腾讯）资源文件。原始几何由 `tool-rect.svg` 等文件逐条给出，
可人工复核每条 path 的坐标。

## 为什么同时提供 SVG 与「代码绘制」

- SVG 是**可审阅的几何真相源**（便于 reviewer 核对"图标是否自绘"、便于后续微调坐标）。
- **运行时不解析 SVG**：Editor 层用同一份 path data 通过代码绘制（WPF `DrawingContext` /
  `Geometry.Parse`，或 GDI+ `GraphicsPath`）。这样既不引入第三方 SVG 渲染库，也让图标随主题色
  （`currentColor`）与 DPI 缩放一起走矢量重绘，不会出现位图放大发虚。
- `manifest.json` 给出 key → 文件 → 中文名 → 分组的映射，代码与文档共用同一份清单。

## 尺寸/描边约定（DPI 相关）

| 项 | 规则 |
|---|---|
| 画布 | 24×24 设计网格 |
| 描边 | 设计值 1.6；渲染时 `strokePx = round(1.6 × renderSize / 24 × dpiScale) / dpiScale`，保证落在物理像素栅格上（物理描边 1~2 px） |
| 渲染尺寸 | 由工具条按钮尺寸决定（默认 18 逻辑 px，可配置），不使用位图拉伸 |
| 颜色 | `currentColor`：默认白 `#FFFFFF`，悬停/激活由主题替换 |

> 具体按钮尺寸/配色以 `docs/06-toolbar-spec.md`（recon 实测）与 `docs/04-editor-spec.md` 为准；
> 实测值到位后只改数值，不改图标几何。

## 分组语义

| group | 含义 |
|---|---|
| `tool` | 标注工具（互斥选中） |
| `action` | 一次性动作（撤销 / 提取文字 / 保存 / 复制 / 取消） |
| `brush` | 笔触档位与填充模式（次级面板） |
| `textsize` | 文字档位（次级面板） |
| `p1-reserved` | P1 阶段（贴图钉住）预留，P0 不显示 |
