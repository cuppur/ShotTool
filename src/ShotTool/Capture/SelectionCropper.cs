// ============================================================================
//  Capture/SelectionCropper.cs —— 选区位图合成（抓屏帧 → 可交付位图）
//
//  唯一职责：把「虚拟桌面坐标系里的选区」变成一张**独立所有权的位图**。
//  调用方（overlay / 输出层）拿到后自行 Dispose —— 本文件不缓存、不持有任何句柄。
//
//  正确性要点：
//    * 选区必须夹进虚拟桌面（用户可能把选区拖到边界外半个像素）；
//    * 位图坐标 = 虚拟桌面坐标 - VirtualDesktopBounds.Location（见 CapturedFrame 契约）；
//    * **不缩放**：像素 1:1 拷贝，抓屏已是物理像素；
//    * 输出位图带上所在显示器的 DPI（SetResolution），这样 PNG 的 pHYs / 剪贴板 DPI
//      天然正确，下游 ImageOutputSink 不需要再猜。
// ============================================================================

using System.Drawing;
using System.Drawing.Imaging;
using ShotTool.Core;

namespace ShotTool.Capture;

/// <summary>从抓屏帧裁剪/合成位图（纯函数，无状态）。</summary>
public static class SelectionCropper
{
    /// <summary>
    /// 按虚拟桌面坐标裁剪选区。选区为空或与桌面无交集时返回 null。
    /// 返回的位图所有权归调用方。
    /// </summary>
    public static Bitmap? Crop(CapturedFrame frame, PhysRect selection)
    {
        ArgumentNullException.ThrowIfNull(frame);

        PhysRect clipped = selection.Clamp(frame.VirtualDesktopBounds);
        if (clipped.IsEmpty)
        {
            return null;
        }

        Rectangle sourceRect = frame.ToBitmapRect(clipped);
        if (sourceRect.Width <= 0 || sourceRect.Height <= 0)
        {
            return null;
        }

        var result = new Bitmap(sourceRect.Width, sourceRect.Height, PixelFormat.Format32bppPArgb);
        try
        {
            using (Graphics graphics = Graphics.FromImage(result))
            {
                graphics.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceCopy;
                graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
                graphics.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;
                graphics.DrawImage(
                    frame.Bitmap,
                    new Rectangle(0, 0, sourceRect.Width, sourceRect.Height),
                    sourceRect,
                    GraphicsUnit.Pixel);
            }

            ApplyMonitorDpi(result, frame, clipped);
            return result;
        }
        catch
        {
            result.Dispose();
            throw;
        }
    }

    /// <summary>
    /// 从一张位图里按位图坐标裁一块（编辑器内部用，不涉及虚拟桌面）。
    /// </summary>
    public static Bitmap? CropBitmap(Bitmap source, Rectangle sourceRect)
    {
        ArgumentNullException.ThrowIfNull(source);

        Rectangle bounds = new(0, 0, source.Width, source.Height);
        Rectangle clipped = Rectangle.Intersect(sourceRect, bounds);
        if (clipped.Width <= 0 || clipped.Height <= 0)
        {
            return null;
        }

        var result = new Bitmap(clipped.Width, clipped.Height, PixelFormat.Format32bppPArgb);
        try
        {
            using Graphics graphics = Graphics.FromImage(result);
            graphics.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceCopy;
            graphics.DrawImage(
                source,
                new Rectangle(0, 0, clipped.Width, clipped.Height),
                clipped,
                GraphicsUnit.Pixel);
            return result;
        }
        catch
        {
            result.Dispose();
            throw;
        }
    }

    /// <summary>把选区中心所在显示器的 DPI 写到结果位图上（取不到就保持默认 96）。</summary>
    private static void ApplyMonitorDpi(Bitmap bitmap, CapturedFrame frame, PhysRect selection)
    {
        uint dpi = NativeMetricDpi(frame, selection);
        try
        {
            bitmap.SetResolution(dpi, dpi);
        }
        catch (ArgumentException)
        {
            // 某些 GDI+ 实现对极端 DPI 值拒绝；DPI 元数据不是正确性必需项
        }
    }

    /// <summary>选区中心所属显示器的 DPI；找不到时用 96。</summary>
    private static uint NativeMetricDpi(CapturedFrame frame, PhysRect selection)
    {
        MonitorInfo? monitor = frame.MonitorAt(selection.Center);
        if (monitor is not null)
        {
            return monitor.Dpi;
        }

        return frame.Monitors.Count > 0 ? frame.Monitors[0].Dpi : DpiHelper.DefaultDpi;
    }
}
