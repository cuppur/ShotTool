// 编辑器离屏自检：不依赖 overlay / 会话窗口，直接跑完整「标注 → 撤销 → 合成导出」链路，
// 生成一张带全部标注的 PNG 作为可核验证据（供 verifier 在 t3 overlay 接通前使用）。
//
// 用途边界：这是**产品代码里的可调用静态入口**（不是测试工程），也可被未来的"导出诊断"菜单复用。
#nullable enable
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using ShotTool.Core;
using ShotTool.Editor.Algorithms;
using ShotTool.Editor.Model;
using ShotTool.Editor.Toolbar;

namespace ShotTool.Editor.Diagnostics
{
    /// <summary>自检结果摘要。</summary>
    public sealed class EditorSelfCheckResult
    {
        public EditorSelfCheckResult(string outputPath, int width, int height, int annotationCount, string? nextRedoName, IReadOnlyList<string> steps)
        {
            OutputPath = outputPath;
            Width = width;
            Height = height;
            AnnotationCount = annotationCount;
            NextRedoName = nextRedoName;
            Steps = steps;
        }

        public string OutputPath { get; }

        public int Width { get; }

        public int Height { get; }

        /// <summary>撤销一次后仍留在图上的标注数。</summary>
        public int AnnotationCount { get; }

        /// <summary>撤销后栈顶的重做名（验证撤销/重做生效）。</summary>
        public string? NextRedoName { get; }

        public IReadOnlyList<string> Steps { get; }
    }

    /// <summary>离屏自检入口。</summary>
    public static class EditorSelfCheck
    {
        /// <summary>
        /// 生成样例 PNG：矩形 → 椭圆 → 箭头 → 马赛克 → 文字 → 序号 ×2 → 画笔 → 撤销一次 → 合成导出。
        /// 画布默认 1600×900（物理像素），dpiScale 默认本机实测 2.2494。
        /// </summary>
        public static EditorSelfCheckResult GenerateSamplePng(
            string outputPath,
            double dpiScale = 2.2494,
            int width = 1600,
            int height = 900,
            bool undoLast = true)
        {
            if (string.IsNullOrWhiteSpace(outputPath))
            {
                throw new ArgumentException("输出路径不能为空", nameof(outputPath));
            }

            double scale = dpiScale > 0.05 ? dpiScale : 1.0;
            List<string> steps = new List<string>();

            using (Bitmap canvas = new Bitmap(width, height, PixelFormat.Format32bppPArgb))
            {
                PaintBackdrop(canvas, width, height);
                steps.Add("背景：渐变 + 彩色方块（用于观察马赛克是否真的块化）");

                AnnotationHistory history = new AnnotationHistory();

                AnnotationStyle red = new AnnotationStyle(EditorDefaults.ColorRed, BrushLevel.Middle);
                AnnotationStyle blue = new AnnotationStyle(EditorDefaults.ColorBlue, BrushLevel.Thick);
                AnnotationStyle green = new AnnotationStyle(EditorDefaults.ColorGreen, BrushLevel.Middle);
                AnnotationStyle orange = new AnnotationStyle(EditorDefaults.ColorOrange, BrushLevel.Thin);
                AnnotationStyle white = new AnnotationStyle(EditorDefaults.ColorWhite, BrushLevel.Middle);

                history.Add(new RectAnnotation(EditorGeometry.FromEdges(60, 60, 520, 300), red));
                steps.Add("矩形：(60,60)-(520,300)，红色 中笔触，描边");

                history.Add(new EllipseAnnotation(EditorGeometry.FromEdges(560, 60, 900, 280), blue));
                steps.Add("椭圆：(560,60)-(900,280)，蓝色 粗笔触");

                history.Add(new ArrowAnnotation(new PhysPointF(940, 60), new PhysPointF(1350, 300), green));
                steps.Add("箭头：(940,60)→(1350,300)，绿色，头部按线宽推导");

                int blockSize = EditorDefaults.ResolveMosaicBlockSize(0, scale);
                List<PhysRectF> blocks = MosaicGeometry.QuantizeRect(EditorGeometry.FromEdges(60, 340, 480, 600), blockSize);
                history.Add(new MosaicAnnotation(blocks, blockSize, MosaicMode.Block, white, EditorGeometry.FromEdges(60, 340, 480, 600)));
                steps.Add(string.Format("马赛克：块状，(60,340)-(480,600)，块尺寸 {0} 物理 px（自动值）", blockSize));

                double textSize = EditorDefaults.TextSizeToPhys(TextSizeLevel.Large, scale);
                TextAnnotation text = new TextAnnotation(new PhysPointF(520, 380), "微信风格标注 4.x", textSize, white);
                TextMeasurer.Fill(text);
                history.Add(text);
                steps.Add(string.Format("文字：「微信风格标注 4.x」，物理字号 {0:0.#}（大档 × {1:0.###}）", textSize, scale));

                double radius = EditorDefaults.ResolveNumberRadius(textSize);
                history.AddNumber(new PhysPointF(560, 520), radius, red);
                history.AddNumber(new PhysPointF(640, 520), radius, red);
                steps.Add(string.Format("序号：圆心 (560,520) 与 (640,520)，半径 {0:0.#}，索引 1、2", radius));

                List<PhysPointF> penPoints = new List<PhysPointF>();
                for (int i = 0; i <= 40; i++)
                {
                    double x = 720 + i * 8.0;
                    double y = 520 + Math.Sin(i * 0.35) * 40.0;
                    PenSmoother.Append(penPoints, new PhysPointF(x, y), PenSmoother.MinSampleDistancePhys);
                }

                history.Add(new PenAnnotation(penPoints, orange));
                steps.Add(string.Format("画笔：{0} 个采样点的正弦曲线，橙色 细笔触（贝塞尔平滑）", penPoints.Count));

                int beforeUndo = history.Count;
                if (undoLast)
                {
                    history.Undo();
                    steps.Add(string.Format("撤销一次：{0} → {1} 个标注，重做名 = {2}", beforeUndo, history.Count, history.NextRedoName ?? "(空)"));
                }
                else
                {
                    steps.Add(string.Format("未撤销：保留全部 {0} 个标注（用于核对每一个标注的绘制）", beforeUndo));
                }

                using (Bitmap composed = AnnotationRasterizer.Compose(canvas, history.Snapshot(), RenderOptions.Create(scale)))
                {
                    composed.Save(outputPath, ImageFormat.Png);
                    steps.Add(string.Format("合成导出：{0}（{1}×{2}，32bppPArgb，含全部标注）", outputPath, composed.Width, composed.Height));
                    return new EditorSelfCheckResult(
                        outputPath,
                        composed.Width,
                        composed.Height,
                        history.Count,
                        history.NextRedoName,
                        steps);
                }
            }
        }

        /// <summary>
        /// 离屏渲染工具条（含选中态、禁用态、扩展按钮 OCR/翻译、展开的色板面板），用于目视核对视觉规格。
        /// 不需要宿主：直接调 ToolbarPainter.Paint。
        /// </summary>
        public static EditorSelfCheckResult GenerateToolbarPng(string outputPath, double dpiScale = 2.2494)
        {
            if (string.IsNullOrWhiteSpace(outputPath))
            {
                throw new ArgumentException("输出路径不能为空", nameof(outputPath));
            }

            double scale = dpiScale > 0.05 ? dpiScale : 1.0;
            List<string> steps = new List<string>();

            ToolbarState state = BuildDemoState();
            state.ActiveIndex = 4;                       // 马赛克（演示选中态：仅图标染 #00C375）
            state.SubPanel = SubPanelKind.Palette;       // 展开色板
            steps.Add("工具条：7 工具 + 分隔 + 撤销(禁用) + 分隔 + [OCR][翻译] 扩展位 + 保存/复制/取消");
            steps.Add("选中态：马赛克按钮（只染图标 #00C375，无背景块 —— 与 docs/06-toolbar-spec.md 实测一致）");
            steps.Add("次级面板：色板 6 色（黑/蓝/绿/橙/红/白），当前色红");

            double subOffset = (ToolbarLayout.SubPanelOffsetLogical + ToolbarLayout.SubPanelHeightLogical) * scale;
            PhysSizeF toolbarSize = ToolbarLayout.Measure(state, scale);
            int padding = (int)Math.Round(14.0 * scale);
            int width = (int)Math.Ceiling(toolbarSize.Width) + padding * 2;
            int height = (int)Math.Ceiling(toolbarSize.Height + subOffset) + padding * 2;
            PhysPointF topLeft = new PhysPointF(padding, padding + subOffset);

            using (Bitmap canvas = new Bitmap(Math.Max(1, width), Math.Max(1, height), PixelFormat.Format32bppPArgb))
            {
                using (Graphics graphics = Graphics.FromImage(canvas))
                {
                    // 模拟 overlay 遮罩底色，便于看清浅色工具条边缘
                    graphics.Clear(Color.FromArgb(0x4A, 0x55, 0x66));
                    ToolbarPainter.Paint(graphics, state, topLeft, scale);
                }

                canvas.Save(outputPath, ImageFormat.Png);
                steps.Add(string.Format("导出：{0}（{1}×{2}，dpiScale={3:0.###}）", outputPath, width, height, scale));
                return new EditorSelfCheckResult(outputPath, width, height, 0, null, steps);
            }
        }

        /// <summary>按 P0 真实顺序构造演示状态（扩展位插在 action.save 之前，与 ToolbarHost.Rebuild 一致）。</summary>
        private static ToolbarState BuildDemoState()
        {
            ToolbarState state = new ToolbarState();
            state.Add(new ToolbarItem("tool.rect", "矩形", ToolbarIconKey.Rect, ToolbarItemRole.Tool, AnnotationKind.Rectangle));
            state.Add(new ToolbarItem("tool.ellipse", "椭圆", ToolbarIconKey.Ellipse, ToolbarItemRole.Tool, AnnotationKind.Ellipse));
            state.Add(new ToolbarItem("tool.arrow", "箭头", ToolbarIconKey.Arrow, ToolbarItemRole.Tool, AnnotationKind.Arrow));
            state.Add(new ToolbarItem("tool.pen", "画笔", ToolbarIconKey.Pen, ToolbarItemRole.Tool, AnnotationKind.Pen));
            state.Add(new ToolbarItem("tool.mosaic", "马赛克", ToolbarIconKey.Mosaic, ToolbarItemRole.Tool, AnnotationKind.Mosaic));
            state.Add(new ToolbarItem("tool.text", "文字", ToolbarIconKey.Text, ToolbarItemRole.Tool, AnnotationKind.Text));
            state.Add(new ToolbarItem("tool.number", "序号", ToolbarIconKey.Number, ToolbarItemRole.Tool, AnnotationKind.Number));
            // 显式分隔线已移除：ToolbarLayout 按语义分组自动补（与 ToolbarState.CreateDefault 一致）

            ToolbarItem undo = new ToolbarItem("action.undo", "撤销", ToolbarIconKey.Undo, ToolbarItemRole.Action, null);
            undo.Enabled = false;
            state.Add(undo);

            state.Add(new ToolbarItem("ext.ai.ocr", "提取文字", ToolbarIconKey.Ocr, ToolbarItemRole.Extension, null));
            state.Add(new ToolbarItem("ext.ai.translate", "翻译", ToolbarIconKey.Translate, ToolbarItemRole.Extension, null));

            state.Add(new ToolbarItem("action.save", "保存", ToolbarIconKey.Save, ToolbarItemRole.Action, null));
            // 与 ToolbarState.CreateDefault 一致：完成按钮用 ✓ 对勾（微信实测形态）
            ToolbarItem copy = new ToolbarItem("action.copy", "完成并复制", ToolbarIconKey.Confirm, ToolbarItemRole.Action, null);
            copy.IsConfirm = true;
            state.Add(copy);
            ToolbarItem close = new ToolbarItem("action.close", "取消", ToolbarIconKey.Close, ToolbarItemRole.Action, null);
            close.IsDanger = true;
            state.Add(close);
            return state;
        }

        private static void PaintBackdrop(Bitmap canvas, int width, int height)
        {
            using (Graphics graphics = Graphics.FromImage(canvas))
            using (LinearGradientBrush background = new LinearGradientBrush(
                new Rectangle(0, 0, width, height),
                Color.FromArgb(0x2B, 0x35, 0x45),
                Color.FromArgb(0x5A, 0x6A, 0x80),
                LinearGradientMode.ForwardDiagonal))
            {
                graphics.SmoothingMode = SmoothingMode.None;
                graphics.FillRectangle(background, 0, 0, width, height);

                // 彩色网格：让马赛克块化效果一眼可见
                int cell = 40;
                Random random = new Random(20261017);
                for (int y = 0; y < height; y += cell)
                {
                    for (int x = 0; x < width; x += cell)
                    {
                        int r = 60 + random.Next(0, 160);
                        int g = 60 + random.Next(0, 160);
                        int b = 60 + random.Next(0, 160);
                        using (SolidBrush brush = new SolidBrush(Color.FromArgb(r, g, b)))
                        {
                            graphics.FillRectangle(brush, x + 2, y + 2, cell - 4, cell - 4);
                        }
                    }
                }
            }
        }
    }
}
