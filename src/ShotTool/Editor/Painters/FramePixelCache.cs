// 帧像素取样缓存：把抓屏帧一次性锁进托管缓冲，供"马赛克预览"在 overlay 上按块取色。
// 会话期间帧位图不变，因此只在帧对象变化时重建一次；不 Dispose 帧本身（所有权归会话）。
#nullable enable
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace ShotTool.Editor.Painters
{
    /// <summary>只读像素取样器（线程：由 UI 线程独占，与 overlay 绘制同线程）。</summary>
    internal sealed class FramePixelCache : IDisposable
    {
        private readonly Bitmap _source;
        private byte[]? _buffer;
        private int _stride;
        private int _width;
        private int _height;
        private bool _snapshotTaken;

        public FramePixelCache(Bitmap source)
        {
            _source = source ?? throw new ArgumentNullException(nameof(source));
            _width = source.Width;
            _height = source.Height;
            TrySnapshot();
        }

        public bool IsAvailable
        {
            get { return _snapshotTaken; }
        }

        /// <summary>取像素；越界或不可用返回 false（调用方跳过该块）。</summary>
        public bool TryGet(int x, int y, out Color color)
        {
            color = Color.Empty;
            if (x < 0 || y < 0 || x >= _width || y >= _height)
            {
                return false;
            }

            byte[]? buffer = _buffer;
            if (buffer != null)
            {
                int offset = y * _stride + x * 4;
                if (offset < 0 || offset + 3 >= buffer.Length)
                {
                    return false;
                }

                // 帧为 32bppPArgb：内存序 B,G,R,A
                color = Color.FromArgb(buffer[offset + 3], buffer[offset + 2], buffer[offset + 1], buffer[offset]);
                return true;
            }

            try
            {
                color = _source.GetPixel(x, y);
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        public void Dispose()
        {
            _buffer = null;
            _snapshotTaken = false;
        }

        private void TrySnapshot()
        {
            try
            {
                Rectangle rect = new Rectangle(0, 0, _width, _height);
                PixelFormat format = _source.PixelFormat;
                if (format != PixelFormat.Format32bppPArgb && format != PixelFormat.Format32bppArgb)
                {
                    // 非 32bpp：退回逐点 GetPixel（慢但正确）
                    return;
                }

                BitmapData data = _source.LockBits(rect, ImageLockMode.ReadOnly, format);
                try
                {
                    if (data.Stride <= 0)
                    {
                        return;
                    }

                    int size = data.Stride * _height;
                    byte[] buffer = new byte[size];
                    Marshal.Copy(data.Scan0, buffer, 0, size);
                    _buffer = buffer;
                    _stride = data.Stride;
                    _snapshotTaken = true;
                }
                finally
                {
                    _source.UnlockBits(data);
                }
            }
            catch (Exception)
            {
                _buffer = null;
                _snapshotTaken = false;
            }
        }
    }
}
