// 工具条自绘图标的 GDI+ 实现（零第三方库、零位图资源）。
//
// 图标按 24×24 网格自绘；0.4.0 依据本机原生截图测量重新对齐轮廓与视觉尺寸。
// Assets/icons/*.svg 保留早期设计参考，当前实际图元以本文件为准。
// 描边宽度为 1.4/24，随目标矩形缩放，最小 1 物理像素。
#nullable enable
using System;
using System.Drawing;
using System.Drawing.Drawing2D;

namespace ShotTool.Editor.Toolbar
{
    /// <summary>图标 key（与 Assets/icons/manifest.json 的 key 一一对应）。</summary>
    public enum ToolbarIconKey
    {
        None = 0,
        Rect = 1,
        Ellipse = 2,
        Arrow = 3,
        Pen = 4,
        Mosaic = 5,
        Text = 6,
        Number = 7,
        Undo = 8,
        Redo = 9,
        Ocr = 10,
        Save = 11,
        Copy = 12,
        Close = 13,
        Confirm = 14,
        Pin = 15,
        Translate = 16,
        BrushThin = 20,
        BrushMiddle = 21,
        BrushThick = 22,
        BrushFill = 23,
        TextSizeSmall = 30,
        TextSizeMiddle = 31,
        TextSizeLarge = 32,
    }

    /// <summary>GDI+ 图标绘制器。所有方法都是纯绘制（无状态、无线程要求）。</summary>
    public static class ToolbarIcons
    {
        /// <summary>设计网格边长（与 Assets/icons 的 viewBox 一致）。</summary>
        public const float GridSize = 24f;

        /// <summary>在给定矩形内绘制图标（矩形为物理像素，正方形；非正方形按短边缩放）。</summary>
        public static void Draw(Graphics graphics, ToolbarIconKey key, RectangleF box, Color color, float strokeScale = 1f)
        {
            if (graphics == null)
            {
                throw new ArgumentNullException(nameof(graphics));
            }

            if (box.Width <= 0 || box.Height <= 0 || key == ToolbarIconKey.None)
            {
                return;
            }

            SmoothingMode previous = graphics.SmoothingMode;
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (IconCanvas canvas = new IconCanvas(graphics, box, color, strokeScale))
            {
                DrawCore(canvas, key);
            }

            graphics.SmoothingMode = previous;
        }

        private static void DrawCore(IconCanvas c, ToolbarIconKey key)
        {
            switch (key)
            {
                case ToolbarIconKey.Rect:
                    c.Rect(3.4, 3.4, 17.2, 17.2, 0.8);
                    break;

                case ToolbarIconKey.Ellipse:
                    c.Ellipse(12, 12, 9.5, 9.5);
                    break;

                case ToolbarIconKey.Arrow:
                    c.Line(5, 19, 18.4, 5.6);
                    c.Poly(false, 11.6, 5.2, 18.8, 5.2, 18.8, 12.4);
                    break;

                case ToolbarIconKey.Pen:
                    // 笔杆多边形 + 笔头圆角（用二次贝塞尔近似 SVG 的 a2.6 2.6 0 0 1 3.7 3.7）
                    c.Poly(false, 4.6, 19.4, 7.4, 12.6, 16.2, 3.8);
                    c.Quad(16.2, 3.8, 19.9, 3.8, 19.9, 7.5);
                    c.Poly(false, 19.9, 7.5, 11.1, 16.3, 4.6, 19.4);
                    c.Line(7.4, 12.6, 11.1, 16.3);
                    break;

                case ToolbarIconKey.Mosaic:
                    c.Rect(3.4, 3.4, 17.2, 17.2, 0.6);
                    for (int row = 0; row < 4; row++)
                        for (int col = 0; col < 4; col++)
                            if ((row + col) % 2 == 0)
                                c.FillRounded(5.3 + col * 3.35, 5.3 + row * 3.35, 3.35, 3.35, 0);
                    break;

                case ToolbarIconKey.Text:
                    c.Rect(3.4, 3.4, 17.2, 17.2, 0.6);
                    c.Line(7.2, 7.5, 16.8, 7.5);
                    c.Line(12, 7.5, 12, 17);
                    break;

                case ToolbarIconKey.Number:
                    c.Rect(3.8, 3.8, 16.4, 16.4, 3);
                    c.Poly(false, 10.6, 9.6, 12.6, 8.2, 12.6, 15.8);
                    break;

                case ToolbarIconKey.Undo:
                    c.Poly(false, 8.8, 5.5, 4.5, 9, 8.8, 12.5);
                    c.Line(4.5, 9, 13.5, 9);
                    c.Quad(13.5, 9, 20, 9, 20, 13.5);
                    c.Quad(20, 13.5, 20, 18.5, 13.5, 18.5);
                    c.Line(13.5, 18.5, 5, 18.5);
                    break;

                case ToolbarIconKey.Redo:
                    c.Poly(false, 14.8, 6, 19.8, 11, 14.8, 16);
                    c.Line(19.8, 11, 10.6, 11);
                    c.Arc(5.8, 11, 9.6, 9.6, 90, 180);
                    c.Line(10.6, 20.6, 14.2, 20.6);
                    break;

                case ToolbarIconKey.Ocr:
                    // 四角取景框
                    c.Poly(false, 4.2, 8.4, 4.2, 5.2, 5.2, 4.2, 8.4, 4.2);
                    c.Poly(false, 15.6, 4.2, 18.8, 4.2, 19.8, 5.2, 19.8, 8.4);
                    c.Poly(false, 19.8, 15.6, 19.8, 18.8, 18.8, 19.8, 15.6, 19.8);
                    c.Poly(false, 8.4, 19.8, 5.2, 19.8, 4.2, 18.8, 4.2, 15.6);
                    c.Line(12, 7, 12, 9);
                    c.Line(7.8, 9, 16.2, 9);
                    c.Quad(14.5, 9, 13.5, 14.5, 7.8, 16.8);
                    c.Quad(9.4, 10.5, 11, 14.9, 16.2, 16.8);
                    break;

                case ToolbarIconKey.Save:
                    c.Line(12, 2.5, 12, 14.6);
                    c.Poly(false, 7.6, 10.4, 12, 14.8, 16.4, 10.4);
                    c.Poly(false, 7.5, 5.5, 4.7, 5.5, 4.7, 21.5, 19.3, 21.5, 19.3, 5.5, 16.5, 5.5);
                    break;

                case ToolbarIconKey.Copy:
                    c.Rect(8.6, 3.6, 11.8, 11.8, 2);
                    c.Poly(false, 15.4, 20.4, 5.6, 20.4, 3.6, 18.4, 3.6, 8.6);
                    break;

                case ToolbarIconKey.Close:
                    c.Line(5.8, 5.8, 18.2, 18.2);
                    c.Line(18.2, 5.8, 5.8, 18.2);
                    break;

                case ToolbarIconKey.Confirm:
                    c.Poly(false, 4, 12.3, 9.4, 17.4, 20.4, 5.4);
                    break;

                case ToolbarIconKey.Pin:
                    c.Poly(true, 8, 3, 16, 3, 14.8, 8.5, 18, 14.5, 6, 14.5, 9.2, 8.5);
                    c.Line(12, 14.5, 12, 21.2);
                    break;

                case ToolbarIconKey.Translate:
                    c.Rect(10.8, 2.5, 10.4, 10.4, 1);
                    c.Rect(2.8, 10.5, 10.4, 10.4, 1);
                    c.Poly(false, 13.6, 10, 16, 5.2, 18.4, 10);
                    c.Line(14.5, 8.5, 17.5, 8.5);
                    c.Line(8, 12.4, 8, 14);
                    c.Line(5.1, 14, 10.9, 14);
                    c.Quad(9.5, 14, 9, 17.4, 5.2, 18.9);
                    c.Quad(6.5, 15, 7.5, 17.6, 10.8, 18.9);
                    break;

                case ToolbarIconKey.BrushThin:
                    c.FillCircle(12, 12, 2.2);
                    break;

                case ToolbarIconKey.BrushMiddle:
                    c.FillCircle(12, 12, 3.6);
                    break;

                case ToolbarIconKey.BrushThick:
                    c.FillCircle(12, 12, 5.2);
                    break;

                case ToolbarIconKey.BrushFill:
                    c.Rect(3.8, 3.8, 16.4, 16.4, 2.4);
                    c.FillRounded(7.4, 7.4, 9.2, 9.2, 1.4);
                    break;

                case ToolbarIconKey.TextSizeSmall:
                    c.Poly(false, 9.4, 17.6, 12, 9.6, 14.6, 17.6);
                    c.Line(10.6, 14.6, 13.4, 14.6);
                    break;

                case ToolbarIconKey.TextSizeMiddle:
                    c.Poly(false, 8, 18, 12, 7.6, 16, 18);
                    c.Line(9.9, 14.2, 14.1, 14.2);
                    break;

                case ToolbarIconKey.TextSizeLarge:
                    c.Poly(false, 6.4, 18.6, 12, 5.4, 17.6, 18.6);
                    c.Line(9, 13.4, 15, 13.4);
                    break;
            }
        }

        /// <summary>
        /// 极简 24 网格绘制画布：所有坐标都以 Assets/icons 的 SVG 数值直接书写，
        /// 由本类统一映射到目标矩形（物理像素）。
        /// </summary>
        private sealed class IconCanvas : IDisposable
        {
            private readonly Graphics _graphics;
            private readonly Pen _pen;
            private readonly SolidBrush _brush;
            private readonly float _offsetX;
            private readonly float _offsetY;
            private readonly float _scale;

            public IconCanvas(Graphics graphics, RectangleF box, Color color, float strokeScale)
            {
                _graphics = graphics;
                float side = Math.Min(box.Width, box.Height);
                _scale = side / GridSize;
                _offsetX = box.X + (box.Width - side) * 0.5f;
                _offsetY = box.Y + (box.Height - side) * 0.5f;

                float penWidth = Math.Max(1f, 1.4f * _scale * (strokeScale <= 0f ? 1f : strokeScale));
                _pen = new Pen(color, penWidth)
                {
                    StartCap = LineCap.Round,
                    EndCap = LineCap.Round,
                    LineJoin = LineJoin.Round,
                };
                _brush = new SolidBrush(color);
            }

            private float X(double x)
            {
                return _offsetX + (float)x * _scale;
            }

            private float Y(double y)
            {
                return _offsetY + (float)y * _scale;
            }

            private float S(double v)
            {
                return (float)v * _scale;
            }

            public void Line(double x1, double y1, double x2, double y2)
            {
                _graphics.DrawLine(_pen, X(x1), Y(y1), X(x2), Y(y2));
            }

            /// <summary>折线：coords 为 x1,y1,x2,y2,... </summary>
            public void Poly(bool close, params double[] coords)
            {
                if (coords == null || coords.Length < 4)
                {
                    return;
                }

                int count = coords.Length / 2;
                PointF[] points = new PointF[count];
                for (int i = 0; i < count; i++)
                {
                    points[i] = new PointF(X(coords[i * 2]), Y(coords[i * 2 + 1]));
                }

                if (close)
                {
                    _graphics.DrawPolygon(_pen, points);
                }
                else
                {
                    _graphics.DrawLines(_pen, points);
                }
            }

            public void Rect(double x, double y, double w, double h, double radius)
            {
                using (GraphicsPath path = BuildRounded(X(x), Y(y), S(w), S(h), S(radius)))
                {
                    _graphics.DrawPath(_pen, path);
                }
            }

            public void FillRounded(double x, double y, double w, double h, double radius)
            {
                using (GraphicsPath path = BuildRounded(X(x), Y(y), S(w), S(h), S(radius)))
                {
                    _graphics.FillPath(_brush, path);
                }
            }

            public void Ellipse(double cx, double cy, double rx, double ry)
            {
                _graphics.DrawEllipse(_pen, X(cx) - S(rx), Y(cy) - S(ry), S(rx) * 2f, S(ry) * 2f);
            }

            public void FillCircle(double cx, double cy, double r)
            {
                _graphics.FillEllipse(_brush, X(cx) - S(r), Y(cy) - S(r), S(r) * 2f, S(r) * 2f);
            }

            /// <summary>椭圆弧：left/top/width/height 为椭圆外接矩形（24 网格坐标），角度为度。</summary>
            public void Arc(double left, double top, double width, double height, float startAngle, float sweepAngle)
            {
                _graphics.DrawArc(_pen, X(left), Y(top), S(width), S(height), startAngle, sweepAngle);
            }

            /// <summary>二次贝塞尔（控制点 cx,cy）。</summary>
            public void Quad(double x1, double y1, double cx, double cy, double x2, double y2)
            {
                // 二次转三次：c1 = p0 + 2/3(c - p0)，c2 = p1 + 2/3(c - p1)
                double c1x = x1 + (cx - x1) * 2.0 / 3.0;
                double c1y = y1 + (cy - y1) * 2.0 / 3.0;
                double c2x = x2 + (cx - x2) * 2.0 / 3.0;
                double c2y = y2 + (cy - y2) * 2.0 / 3.0;
                _graphics.DrawBezier(_pen, X(x1), Y(y1), X(c1x), Y(c1y), X(c2x), Y(c2y), X(x2), Y(y2));
            }

            public void Dispose()
            {
                _pen.Dispose();
                _brush.Dispose();
            }

            private static GraphicsPath BuildRounded(float x, float y, float w, float h, float radius)
            {
                GraphicsPath path = new GraphicsPath();
                if (w <= 0f || h <= 0f)
                {
                    return path;
                }

                float r = Math.Min(radius, Math.Min(w, h) * 0.5f);
                if (r <= 0.1f)
                {
                    path.AddRectangle(new RectangleF(x, y, w, h));
                    return path;
                }

                float d = r * 2f;
                path.AddArc(x, y, d, d, 180, 90);
                path.AddArc(x + w - d, y, d, d, 270, 90);
                path.AddArc(x + w - d, y + h - d, d, d, 0, 90);
                path.AddArc(x, y + h - d, d, d, 90, 90);
                path.CloseFigure();
                return path;
            }
        }
    }
}
