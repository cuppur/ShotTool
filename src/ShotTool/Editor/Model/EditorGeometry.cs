// Editor 内部亚像素几何（double，物理像素单位）。
//
// 分工（与 ShotTool.Core 的唯一几何来源约定一致）：
//   * 矩形 ---- 直接用 ShotTool.Core.PhysRectF（double）与 Core.PhysRect（int），本文件只补扩展方法；
//   * 点/尺寸 ---- Core 只有 int 版 PhysPoint/PhysSize，Editor 需要亚像素版本，故定义 PhysPointF/PhysSizeF；
//   * 导出、裁剪、命中测试的边界一律用 Core.PhysRect（int）——见 ToCoreRect()。
#nullable enable
using System;
using ShotTool.Core;

namespace ShotTool.Editor.Model
{
    /// <summary>亚像素坐标点（物理像素，选区局部坐标；用于画笔平滑、箭头端点等）。</summary>
    public readonly record struct PhysPointF(double X, double Y)
    {
        public static PhysPointF Zero
        {
            get { return new PhysPointF(0, 0); }
        }

        public bool IsFinite
        {
            get
            {
                return !double.IsNaN(X) && !double.IsInfinity(X)
                    && !double.IsNaN(Y) && !double.IsInfinity(Y);
            }
        }

        public PhysPointF Offset(double dx, double dy)
        {
            return new PhysPointF(X + dx, Y + dy);
        }

        public double DistanceTo(PhysPointF other)
        {
            double dx = X - other.X;
            double dy = Y - other.Y;
            return Math.Sqrt(dx * dx + dy * dy);
        }

        public static PhysPointF operator +(PhysPointF a, PhysPointF b)
        {
            return new PhysPointF(a.X + b.X, a.Y + b.Y);
        }

        public static PhysPointF operator -(PhysPointF a, PhysPointF b)
        {
            return new PhysPointF(a.X - b.X, a.Y - b.Y);
        }

        public static PhysPointF operator *(PhysPointF a, double k)
        {
            return new PhysPointF(a.X * k, a.Y * k);
        }

        public static PhysPointF Lerp(PhysPointF a, PhysPointF b, double t)
        {
            return new PhysPointF(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t);
        }

        /// <summary>转契约整数点（四舍五入）。</summary>
        public PhysPoint ToCorePoint()
        {
            return PhysPoint.From(X, Y);
        }

        public override string ToString()
        {
            return string.Format("({0:0.##}, {1:0.##})", X, Y);
        }
    }

    /// <summary>亚像素尺寸（物理像素）。</summary>
    public readonly record struct PhysSizeF(double Width, double Height)
    {
        public static PhysSizeF Empty
        {
            get { return new PhysSizeF(0, 0); }
        }

        public bool IsEmpty
        {
            get { return Width <= 0 || Height <= 0; }
        }

        public PhysSize ToCoreSize()
        {
            return PhysSize.From(Width, Height);
        }

        public override string ToString()
        {
            return string.Format("{0:0.##}x{1:0.##}", Width, Height);
        }
    }

    /// <summary>Editor 内部几何辅助（静态形态）。</summary>
    public static class EditorGeometry
    {
        /// <summary>由任意两个对角点构造矩形（自动归一化，允许反向拖拽）。</summary>
        public static PhysRectF FromEdges(double x1, double y1, double x2, double y2)
        {
            double left = Math.Min(x1, x2);
            double top = Math.Min(y1, y2);
            return new PhysRectF(left, top, Math.Abs(x2 - x1), Math.Abs(y2 - y1));
        }

        /// <summary>并集；空矩形不参与扩展。</summary>
        public static PhysRectF Union(PhysRectF a, PhysRectF b)
        {
            if (a.IsEmpty)
            {
                return b;
            }

            if (b.IsEmpty)
            {
                return a;
            }

            return FromEdges(
                Math.Min(a.Left, b.Left),
                Math.Min(a.Top, b.Top),
                Math.Max(a.Right, b.Right),
                Math.Max(a.Bottom, b.Bottom));
        }

        /// <summary>交集；无交集返回 Empty。</summary>
        public static PhysRectF Intersect(PhysRectF a, PhysRectF b)
        {
            double left = Math.Max(a.Left, b.Left);
            double top = Math.Max(a.Top, b.Top);
            double right = Math.Min(a.Right, b.Right);
            double bottom = Math.Min(a.Bottom, b.Bottom);
            if (right <= left || bottom <= top)
            {
                return PhysRectF.Empty;
            }

            return new PhysRectF(left, top, right - left, bottom - top);
        }

        /// <summary>点到线段的距离（箭头/画笔命中测试）。</summary>
        public static double DistanceToSegment(PhysPointF p, PhysPointF a, PhysPointF b)
        {
            double dx = b.X - a.X;
            double dy = b.Y - a.Y;
            double lengthSquared = dx * dx + dy * dy;
            if (lengthSquared <= 1e-9)
            {
                return p.DistanceTo(a);
            }

            double t = ((p.X - a.X) * dx + (p.Y - a.Y) * dy) / lengthSquared;
            if (t < 0)
            {
                t = 0;
            }
            else if (t > 1)
            {
                t = 1;
            }

            double px = a.X + dx * t;
            double py = a.Y + dy * t;
            double ddx = p.X - px;
            double ddy = p.Y - py;
            return Math.Sqrt(ddx * ddx + ddy * ddy);
        }
    }

    /// <summary>Core.PhysRectF 的 Editor 侧扩展（不改 Core 文件）。</summary>
    public static class PhysRectFExtensions
    {
        /// <summary>负宽高归一化。</summary>
        public static PhysRectF Normalized(this PhysRectF rect)
        {
            if (rect.Width >= 0 && rect.Height >= 0)
            {
                return rect;
            }

            return EditorGeometry.FromEdges(rect.Left, rect.Top, rect.Right, rect.Bottom);
        }

        /// <summary>闭区间点命中（Core 的 Contains(double,double) 是半开区间，绘制命中需要闭区间语义）。</summary>
        public static bool ContainsPoint(this PhysRectF rect, PhysPointF point)
        {
            return point.X >= rect.Left && point.X <= rect.Right
                && point.Y >= rect.Top && point.Y <= rect.Bottom;
        }

        public static PhysPointF CenterPoint(this PhysRectF rect)
        {
            return new PhysPointF(rect.CenterX, rect.CenterY);
        }

        /// <summary>四边各外扩（负数内缩）。</summary>
        public static PhysRectF Inflated(this PhysRectF rect, double delta)
        {
            return rect.Inflate(delta);
        }

        /// <summary>转契约整数矩形（四舍五入）——导出、裁剪、命中边界统一用这个类型。</summary>
        public static PhysRect ToCoreRect(this PhysRectF rect)
        {
            return rect.ToPhysRect();
        }

        /// <summary>四舍五入到物理像素栅格。</summary>
        public static PhysRectF Snapped(this PhysRectF rect)
        {
            return EditorGeometry.FromEdges(
                Math.Round(rect.Left),
                Math.Round(rect.Top),
                Math.Round(rect.Right),
                Math.Round(rect.Bottom));
        }

        public static bool IsFinite(this PhysRectF rect)
        {
            return !double.IsNaN(rect.X) && !double.IsInfinity(rect.X)
                && !double.IsNaN(rect.Y) && !double.IsInfinity(rect.Y)
                && !double.IsNaN(rect.Width) && !double.IsInfinity(rect.Width)
                && !double.IsNaN(rect.Height) && !double.IsInfinity(rect.Height);
        }
    }
}
