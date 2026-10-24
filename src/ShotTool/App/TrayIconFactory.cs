// ============================================================================
//  App/TrayIconFactory.cs —— 托盘 / 窗口图标（纯代码绘制，无外部资源文件）
//
//  自研几何绘制，--export-icon 从同一绘制源生成 exe 使用的多分辨率 ICO。
//    * 支持任意 DPI：按目标尺寸重绘，225% 下 16px 图标不会糊成一团；
//    * 生成方式是 PNG-in-ICO（Vista+ 支持），Icon 对象持有 MemoryStream，
//      Dispose 即释放 —— 不产生 HICON 句柄泄漏（不需要 DestroyIcon 配对）。
//
//  图形语义：蓝紫底、白色框选角标、薄荷绿裁切笔画。
// ============================================================================

using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;

namespace ShotTool.App;

/// <summary>托盘图标生成器。</summary>
public static class TrayIconFactory
{
    internal static void Export(string iconPath, string previewPath)
    {
        var images = new[] { 16, 20, 24, 32, 48, 64, 128, 256 }
            .Select(size => (size, RenderPng(size))).ToList();
        File.WriteAllBytes(iconPath, BuildIco(images));
        using var preview = CreateBitmap(256);
        preview.Save(previewPath, ImageFormat.Png);
    }
    /// <summary>生成 16–256 像素多尺寸图标，系统按需取用。</summary>
    public static Icon Create()
    {
        int[] sizes = { 16, 20, 24, 32, 48, 64, 128, 256 };
        var images = new List<(int Size, byte[] Png)>(sizes.Length);
        foreach (int size in sizes)
        {
            images.Add((size, RenderPng(size)));
        }

        byte[] ico = BuildIco(images);
        return new Icon(new MemoryStream(ico, writable: false));
    }

    /// <summary>渲染指定尺寸的图标位图（诊断/自测也用它验证绘制代码）。</summary>
    public static Bitmap CreateBitmap(int size)
    {
        var bitmap = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using Graphics graphics = Graphics.FromImage(bitmap);
        DrawGlyph(graphics, size);
        return bitmap;
    }

    private static byte[] RenderPng(int size)
    {
        using Bitmap bitmap = CreateBitmap(size);
        using var stream = new MemoryStream();
        bitmap.Save(stream, ImageFormat.Png);
        return stream.ToArray();
    }

    /// <summary>绘制图形本体（尺寸无关，按 size 等比缩放）。</summary>
    private static void DrawGlyph(Graphics graphics, int size)
    {
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.Clear(Color.Transparent);
        graphics.ScaleTransform(size / 128f, size / 128f);
        var tile = new RectangleF(5, 5, 118, 118);
        using var shape = RoundedRectangle(tile, 28);
        using (var fill = new LinearGradientBrush(tile, Color.FromArgb(95, 104, 244), Color.FromArgb(49, 46, 133), 55f))
            graphics.FillPath(fill, shape);
        using (var rim = new Pen(Color.FromArgb(42, 255, 255, 255), 1.5f)) graphics.DrawPath(rim, shape);
        // Optical simplification at small tray sizes keeps the four crop corners legible.
        using var frame = new Pen(Color.FromArgb(250, 255, 255, 255), 8) {
            StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        graphics.DrawLines(frame, new PointF[] { new(33, 51), new(33, 33), new(51, 33) });
        graphics.DrawLines(frame, new PointF[] { new(77, 33), new(95, 33), new(95, 51) });
        graphics.DrawLines(frame, new PointF[] { new(33, 77), new(33, 95), new(51, 95) });
        if (size >= 32)
        {
            using var glass = new SolidBrush(Color.FromArgb(32, 255, 255, 255));
            graphics.FillRectangle(glass, 46, 46, 36, 36);
        }
        using var mint = new Pen(Color.FromArgb(126, 246, 207), 9) {
            StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        graphics.DrawLines(mint, new PointF[] { new(76, 95), new(95, 95), new(95, 76) });
        graphics.DrawLine(mint, 76, 76, 95, 95);
        graphics.ResetTransform();
    }
    private static GraphicsPath RoundedRectangle(RectangleF rectangle, float radius)
    {
        var path = new GraphicsPath();
        float diameter = radius * 2;
        path.AddArc(rectangle.X, rectangle.Y, diameter, diameter, 180, 90);
        path.AddArc(rectangle.Right - diameter, rectangle.Y, diameter, diameter, 270, 90);
        path.AddArc(rectangle.Right - diameter, rectangle.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(rectangle.X, rectangle.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }

    /// <summary>
    /// 组装 PNG-in-ICO 容器：ICONDIR(6) + ICONDIRENTRY(16 × n) + PNG 数据。
    /// </summary>
    private static byte[] BuildIco(IReadOnlyList<(int Size, byte[] Png)> images)
    {
        const int headerSize = 6;
        const int entrySize = 16;
        int offset = headerSize + (entrySize * images.Count);

        using var stream = new MemoryStream(offset + images.Sum(i => i.Png.Length));
        using var writer = new BinaryWriter(stream);

        // ICONDIR
        writer.Write((ushort)0);                       // reserved
        writer.Write((ushort)1);                       // type = icon
        writer.Write((ushort)images.Count);            // count

        // ICONDIRENTRY × n
        foreach ((int size, byte[] png) in images)
        {
            writer.Write((byte)(size >= 256 ? 0 : size)); // width（0 表示 256）
            writer.Write((byte)(size >= 256 ? 0 : size)); // height
            writer.Write((byte)0);                        // 调色板颜色数（32bpp 无调色板）
            writer.Write((byte)0);                        // reserved
            writer.Write((ushort)1);                      // planes
            writer.Write((ushort)32);                     // bit count
            writer.Write(png.Length);                     // 数据字节数
            writer.Write(offset);                         // 数据偏移
            offset += png.Length;
        }

        // 图像数据
        foreach ((int _, byte[] png) in images)
        {
            writer.Write(png);
        }

        writer.Flush();
        return stream.ToArray();
    }
}
