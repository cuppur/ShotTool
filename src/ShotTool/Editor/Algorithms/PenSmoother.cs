// 画笔轨迹采样与平滑：纯几何实现（不依赖 System.Drawing），输出密集折线供 GDI+/WPF 两条渲染路径共用。
#nullable enable
using System;
using System.Collections.Generic;
using ShotTool.Editor.Model;

namespace ShotTool.Editor.Algorithms
{
    /// <summary>画笔轨迹处理。</summary>
    public static class PenSmoother
    {
        /// <summary>最小采样间距（物理像素）：过近的点丢弃，避免噪声抖动。</summary>
        public const double MinSampleDistancePhys = 1.5;

        /// <summary>
        /// 追加采样点（距离阈值过滤）。返回 true 表示已加入。
        /// 采样坐标必须是物理像素；NaN/Infinity 一律丢弃。
        /// </summary>
        public static bool Append(List<PhysPointF> points, PhysPointF sample, double minDistancePhys)
        {
            if (points == null)
            {
                throw new ArgumentNullException(nameof(points));
            }

            if (!sample.IsFinite)
            {
                return false;
            }

            double min = minDistancePhys > 0 ? minDistancePhys : MinSampleDistancePhys;
            if (points.Count > 0 && points[points.Count - 1].DistanceTo(sample) < min)
            {
                return false;
            }

            points.Add(sample);
            return true;
        }

        /// <summary>
        /// 把控制点序列展平为密集折线：起点 → 每段以"相邻点中点为终点、当前点为控制点"的二次贝塞尔 → 末点。
        /// 这是截图工具画笔的标准平滑方式（观感接近原生，且不会过冲）。
        /// </summary>
        /// <param name="controlPoints">原始采样点（物理像素）。</param>
        /// <param name="subdivisions">每段细分数；&lt;= 0 表示按段长自适应（2..12）。</param>
        public static List<PhysPointF> Flatten(IReadOnlyList<PhysPointF> controlPoints, int subdivisions)
        {
            List<PhysPointF> result = new List<PhysPointF>();
            if (controlPoints == null || controlPoints.Count == 0)
            {
                return result;
            }

            if (controlPoints.Count == 1)
            {
                result.Add(controlPoints[0]);
                return result;
            }

            result.Add(controlPoints[0]);
            for (int i = 1; i < controlPoints.Count - 1; i++)
            {
                PhysPointF control = controlPoints[i];
                PhysPointF end = PhysPointF.Lerp(controlPoints[i], controlPoints[i + 1], 0.5);
                PhysPointF start = result[result.Count - 1];
                int steps = subdivisions > 0 ? subdivisions : AutoSubdivisions(start, control, end);
                QuadraticTo(result, start, control, end, steps);
            }

            result.Add(controlPoints[controlPoints.Count - 1]);
            return result;
        }

        /// <summary>单点轨迹（应画成圆点而不是折线）。</summary>
        public static bool IsDot(IReadOnlyList<PhysPointF> points)
        {
            return points != null && points.Count == 1;
        }

        private static void QuadraticTo(List<PhysPointF> output, PhysPointF p0, PhysPointF control, PhysPointF p1, int steps)
        {
            if (steps < 1)
            {
                steps = 1;
            }

            for (int s = 1; s <= steps; s++)
            {
                double t = (double)s / steps;
                double mt = 1.0 - t;
                double x = mt * mt * p0.X + 2.0 * mt * t * control.X + t * t * p1.X;
                double y = mt * mt * p0.Y + 2.0 * mt * t * control.Y + t * t * p1.Y;
                output.Add(new PhysPointF(x, y));
            }
        }

        private static int AutoSubdivisions(PhysPointF p0, PhysPointF control, PhysPointF p1)
        {
            double length = p0.DistanceTo(control) + control.DistanceTo(p1);
            int steps = (int)Math.Ceiling(length / 4.0);
            if (steps < 2)
            {
                steps = 2;
            }

            if (steps > 12)
            {
                steps = 12;
            }

            return steps;
        }
    }
}
