# 架构与构建

ShotTool 是 C# / .NET 8 Windows Forms 桌面程序，面向 Windows x64。它直接使用系统抓屏、全局快捷键与剪贴板接口，不依赖微信账号或微信进程。

| 目录 | 职责 |
|---|---|
| `App` | 托盘、快捷键、截图会话、覆盖层、贴图窗口 |
| `Capture` / `Interop` | GDI 抓屏、显示器与 Win32 交互 |
| `Editor` | 标注模型、撤销栈、栅格化、工具条与图标 |
| `Output` | 图片 / 文本剪贴板、图片保存 |
| `Settings` | JSON 配置、分页设置窗口、自启登记 |
| `Ai` | Windows OCR、Paddle OCR、NLLB 和 Ollama 适配 |
| `Core` | 几何、DPI 换算及共享接口 |

捕获和标注采用物理像素坐标。覆盖层启用双缓冲，先合成背景、选区、标注和工具条，再呈现。导出的成品通过统一栅格化入口生成。贴图持有成品副本，其生命周期独立于截图编辑器。

设置页采用手动 DIP 换算与固定底栏，避免字体自动缩放、固定像素控件和长标签混用。各页可以纵向滚动。

## 本地构建

安装 .NET 8 或更新 SDK，在 Windows x64 下运行：

```powershell
./scripts/build.ps1
```

默认源码不含 OCR 权重。此时可构建运行，使用系统 OCR。需要 Paddle OCR 时先执行：

```powershell
./scripts/fetch-ocr-models.ps1
./scripts/publish.ps1 -Version 0.8.0
```

脚本下载约 15 MB 的 PP-OCRv4 文件，并校验固定 SHA-256。文件嵌入 exe，受各自上游许可约束，见 `licenses/`。上游变化导致校验失败时，脚本不会替换现有模型。

发布结果位于 `dist/ShotTool-0.8.0-win-x64/`。默认自包含，无需目标机安装 .NET。翻译大模型不随标准构建分发；如确实需要，可使用 `-IncludeTranslationModels -ModelSource <目录>`。同卷本地迭代可额外使用 `-LinkTranslationModels`，通过硬链接减少重复磁盘占用。

## 轻量验证

程序提供开发自检参数：

```powershell
ShotTool.exe --selftest-settings
ShotTool.exe --selftest-pin --selftest-editor
```

设置检查使用独立配置及屏外窗口，覆盖 100%、150%、225% DPI、四个页签和最小尺寸布局。贴图检查覆盖标注保留、原图释放后的独立生命周期、缩放及关闭。截图自检会捕获当前桌面，`--selftest-editor` 会覆盖剪贴板为测试图片，因此按需运行；本仓库不包含本机截图、运行日志和这些检查的临时配置。

自检输出默认为 `%LOCALAPPDATA%\ShotTool\selftest`，可用 `SHOTTOOL_SELFTEST_DIR` 改变。`--selftest-ai-ui` 需要可用的 OCR 和本地翻译模型，用自建图片检查识别、翻译及面板复制链路；这不能替代实机鼠标和混合 DPI 验收。

文字编辑使用原生 Rich Edit 与冻结截图背景合成，保留原生输入法处理。文字移动、修改和删除采用独立撤销事务，临时选中框只在覆盖层绘制。登录自启使用用户目录中的独立单文件副本和当前用户登录任务，受策略限制时回退到 Run 项。
