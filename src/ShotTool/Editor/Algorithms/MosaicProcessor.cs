// 马赛克像素化：纯 System.Drawing（Bitmap/LockBits/Marshal.Copy），不用 unsafe、不引第三方库。
// 对外签名只出现 Editor 的亚像素类型与 Core.PhysRect(int)；System.Drawing.Rectangle 仅在内部对接 GDI+。
#nullable enable
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using ShotTool.Core;
using ShotTool.Editor.Model;

namespace ShotTool.Editor.Algorithms
{
    /// <summary>马赛克像素化。</summary>
    public static class MosaicProcessor
    {
        /// <summary>
        /// 就地像素化给定块（每块取中心像素填充整块）。返回实际处理的块数。
        /// 只锁定 blocks 的并集区域，避免整幅图来回拷贝。
        /// </summary>
        public static int Apply(Bitmap bitmap, IReadOnlyList<PhysRectF> blocks)
        {
            if (bitmap == null)
            {
                throw new ArgumentNullException(nameof(bitmap));
            }

            if (blocks == null || blocks.Count == 0)
            {
                return 0;
            }

            if (bitmap.PixelFormat != PixelFormat.Format32bppPArgb && bitmap.PixelFormat != PixelFormat.Format32bppArgb)
            {
                throw new ArgumentException("马赛克只支持 32bppPArgb / 32bppArgb 位图", nameof(bitmap));
            }

            PhysRectF union = PhysRectF.Empty;
            for (int i = 0; i < blocks.Count; i++)
            {
                union = EditorGeometry.Union(union, blocks[i]);
            }

            PhysRect area = union.ToCoreRect().Intersect(new PhysRect(0, 0, bitmap.Width, bitmap.Height));
            if (area.IsEmpty)
            {
                return 0;
            }

            Rectangle lockRect = area.ToRectangle();
            BitmapData data = bitmap.LockBits(lockRect, ImageLockMode.ReadWrite, bitmap.PixelFormat);
            try
            {
                int stride = data.Stride;
                if (stride < 0)
                {
                    // 自下而上的位图（少见）：本工具位图均来自截图/新建，恒为正 stride。
                    throw new NotSupportedException("不支持负 stride 位图");
                }

                int byteCount = stride * area.Height;
                byte[] buffer = new byte[byteCount];
                Marshal.Copy(data.Scan0, buffer, 0, byteCount);
                int applied = PixelateBlocks(buffer, stride, area, blocks);
                Marshal.Copy(buffer, 0, data.Scan0, byteCount);
                return applied;
            }
            finally
            {
                bitmap.UnlockBits(data);
            }
        }

        /// <summary>便捷版：给定区域与块尺寸，内部量化后像素化（Block 模式）。</summary>
        public static int ApplyRegion(Bitmap bitmap, PhysRectF region, int blockSize)
        {
            if (bitmap == null)
            {
                throw new ArgumentNullException(nameof(bitmap));
            }

            List<PhysRectF> blocks = MosaicGeometry.QuantizeRect(region, blockSize);
            return Apply(bitmap, blocks);
        }

        /// <summary>
        /// 在锁定缓冲上逐块填充。area 为缓冲对应的图像区域（缓冲坐标 = 图像坐标 - area 左上角）。
        /// 性能：块内按行"首像素 + 长度倍增复制"，3840×2160 全屏块化约 10~20 ms。
        /// </summary>
        private static int PixelateBlocks(byte[] buffer, int stride, PhysRect area, IReadOnlyList<PhysRectF> blocks)
        {
            int applied = 0;
            for (int i = 0; i < blocks.Count; i++)
            {
                PhysRect block = blocks[i].ToCoreRect();
                int x0 = Math.Max(block.Left, area.Left);
                int x1 = Math.Min(block.Right, area.Right);
                int y0 = Math.Max(block.Top, area.Top);
                int y1 = Math.Min(block.Bottom, area.Bottom);
                if (x1 <= x0 || y1 <= y0)
                {
                    continue;
                }

                // 取块中心像素（基于整块而非裁剪后的交集，保证同一块颜色稳定）
                int cx = block.Left + Math.Max(0, (block.Width - 1) / 2);
                int cy = block.Top + Math.Max(0, (block.Height - 1) / 2);
                if (cx < x0)
                {
                    cx = x0;
                }
                else if (cx > x1 - 1)
                {
                    cx = x1 - 1;
                }

                if (cy < y0)
                {
                    cy = y0;
                }
                else if (cy > y1 - 1)
                {
                    cy = y1 - 1;
                }

                // 3×3 采样取**平均**：单点取色会把源图里的细缝隙/高光放大成整块，观感杂乱；
                // 原生马赛克是块内平均，这里对齐它。
                int pad = Math.Max(1, Math.Min(x1 - x0, y1 - y0) / 4);
                long acc0 = 0;
                long acc1 = 0;
                long acc2 = 0;
                long acc3 = 0;
                int samples = 0;
                for (int oy = -1; oy <= 1; oy++)
                {
                    for (int ox = -1; ox <= 1; ox++)
                    {
                        int px = cx + ox * pad;
                        int py = cy + oy * pad;
                        if (px < x0)
                        {
                            px = x0;
                        }
                        else if (px > x1 - 1)
                        {
                            px = x1 - 1;
                        }

                        if (py < y0)
                        {
                            py = y0;
                        }
                        else if (py > y1 - 1)
                        {
                            py = y1 - 1;
                        }

                        int sampleOffset = (py - area.Top) * stride + (px - area.Left) * 4;
                        if (sampleOffset < 0 || sampleOffset + 3 >= buffer.Length)
                        {
                            continue;
                        }

                        acc0 += buffer[sampleOffset];
                        acc1 += buffer[sampleOffset + 1];
                        acc2 += buffer[sampleOffset + 2];
                        acc3 += buffer[sampleOffset + 3];
                        samples++;
                    }
                }

                if (samples == 0)
                {
                    continue;
                }

                byte s0 = (byte)(acc0 / samples);
                byte s1 = (byte)(acc1 / samples);
                byte s2 = (byte)(acc2 / samples);
                byte s3 = (byte)(acc3 / samples);

                int localX0 = x0 - area.Left;
                int widthBytes = (x1 - x0) * 4;
                for (int y = y0; y < y1; y++)
                {
                    int rowStart = (y - area.Top) * stride + localX0 * 4;
                    if (rowStart < 0 || rowStart + widthBytes > buffer.Length)
                    {
                        continue;
                    }

                    Span<byte> row = buffer.AsSpan(rowStart, widthBytes);
                    row[0] = s0;
                    row[1] = s1;
                    row[2] = s2;
                    row[3] = s3;

                    int filled = 4;
                    while (filled < widthBytes)
                    {
                        int chunk = Math.Min(filled, widthBytes - filled);
                        row.Slice(0, chunk).CopyTo(row.Slice(filled));
                        filled += chunk;
                    }
                }

                applied++;
            }

            return applied;
        }
    }
}
