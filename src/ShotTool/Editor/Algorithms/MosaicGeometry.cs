// 马赛克几何：把"拖拽矩形 / 轨迹"量化成对齐网格的块。
// 纯几何（Core.PhysRectF 亚像素矩形 + Editor 的 PhysPointF），不触碰像素（像素化见 MosaicProcessor.cs）。
#nullable enable
using System;
using System.Collections.Generic;
using ShotTool.Core;
using ShotTool.Editor.Model;

namespace ShotTool.Editor.Algorithms
{
    /// <summary>马赛克块量化。</summary>
    public static class MosaicGeometry
    {
        /// <summary>单次标注的块上限（防御性上限，避免误操作产生海量块导致卡顿）。</summary>
        public const int MaxBlocks = 200000;

        /// <summary>
        /// 矩形 → 对齐到 blockSize 网格的块列表。
        /// 网格原点固定为 (0,0)（= 选区左上角），因此不同时间画的两个马赛克标注块边界不会错位。
        /// 块可能略微超出拖拽矩形（网格对齐语义，与原生观感一致）；像素化时再裁进图像边界。
        /// </summary>
        public static List<PhysRectF> QuantizeRect(PhysRectF region, int blockSize)
        {
            List<PhysRectF> blocks = new List<PhysRectF>();
            PhysRectF r = region.Normalized();
            if (r.IsEmpty || blockSize < 2)
            {
                return blocks;
            }

            int bx0 = (int)Math.Floor(r.Left / blockSize);
            int by0 = (int)Math.Floor(r.Top / blockSize);
            int bx1 = (int)Math.Floor((r.Right - 1e-6) / blockSize);
            int by1 = (int)Math.Floor((r.Bottom - 1e-6) / blockSize);
            if (bx1 < bx0 || by1 < by0)
            {
                return blocks;
            }

            for (int by = by0; by <= by1; by++)
            {
                for (int bx = bx0; bx <= bx1; bx++)
                {
                    if (blocks.Count >= MaxBlocks)
                    {
                        return blocks;
                    }

                    blocks.Add(new PhysRectF(bx * (double)blockSize, by * (double)blockSize, blockSize, blockSize));
                }
            }

            return blocks;
        }

        /// <summary>
        /// 扫描式（mosaic_saomiao 语义）：把移动轨迹量化成被刷过的块，自动去重。
        /// brushRadiusPhys 为笔刷半径；轨迹点之间会按 blockSize/2 插值，避免快速拖动出现断块。
        /// </summary>
        public static List<PhysRectF> QuantizeStroke(
            IReadOnlyList<PhysPointF> path,
            int blockSize,
            double brushRadiusPhys)
        {
            List<PhysRectF> blocks = new List<PhysRectF>();
            if (path == null || path.Count == 0 || blockSize < 2)
            {
                return blocks;
            }

            double radius = brushRadiusPhys > 0 ? brushRadiusPhys : blockSize * 0.5;
            if (radius < 1.0)
            {
                radius = 1.0;
            }

            HashSet<long> seen = new HashSet<long>();
            double step = Math.Max(1.0, blockSize * 0.5);

            for (int i = 0; i < path.Count; i++)
            {
                if (i == 0)
                {
                    AddBlocksAround(blocks, seen, path[0], radius, blockSize);
                }
                else
                {
                    PhysPointF a = path[i - 1];
                    PhysPointF b = path[i];
                    double distance = a.DistanceTo(b);
                    int steps = (int)Math.Ceiling(distance / step);
                    if (steps < 1)
                    {
                        steps = 1;
                    }

                    if (steps > 256)
                    {
                        // 防御：异常大的跳变（拖动丢帧）不让插值爆炸
                        steps = 256;
                    }

                    for (int s = 1; s <= steps; s++)
                    {
                        AddBlocksAround(blocks, seen, PhysPointF.Lerp(a, b, (double)s / steps), radius, blockSize);
                    }
                }

                if (blocks.Count >= MaxBlocks)
                {
                    break;
                }
            }

            return blocks;
        }

        /// <summary>块中心点（像素化时按中心取色）。</summary>
        public static PhysPointF BlockCenter(PhysRectF block)
        {
            return new PhysPointF(block.Left + block.Width * 0.5, block.Top + block.Height * 0.5);
        }

        private static void AddBlocksAround(
            List<PhysRectF> blocks,
            HashSet<long> seen,
            PhysPointF center,
            double radius,
            int blockSize)
        {
            int bx0 = (int)Math.Floor((center.X - radius) / blockSize);
            int by0 = (int)Math.Floor((center.Y - radius) / blockSize);
            int bx1 = (int)Math.Floor((center.X + radius) / blockSize);
            int by1 = (int)Math.Floor((center.Y + radius) / blockSize);

            for (int by = by0; by <= by1; by++)
            {
                for (int bx = bx0; bx <= bx1; bx++)
                {
                    long key = ((long)bx << 32) ^ (uint)by;
                    if (!seen.Add(key))
                    {
                        continue;
                    }

                    if (blocks.Count >= MaxBlocks)
                    {
                        return;
                    }

                    blocks.Add(new PhysRectF(bx * (double)blockSize, by * (double)blockSize, blockSize, blockSize));
                }
            }
        }
    }
}
