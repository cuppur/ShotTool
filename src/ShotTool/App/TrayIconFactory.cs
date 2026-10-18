// ============================================================================
//  App/TrayIconFactory.cs —— 托盘 / 窗口图标（纯代码绘制，无外部资源文件）
//
//  为什么自绘而不是放一个 .ico 到仓库：
//    * 交付边界要求自研，图标由代码生成 → 无需二进制资源、无需 svg→ico 转换步骤；
//    * 支持任意 DPI：按目标尺寸重绘，225% 下 16px 图标不会糊成一团；
//    * 生成方式是 PNG-in-ICO（Vista+ 支持），Icon 对象持有 MemoryStream，
//      Dispose 即释放 —— 不产生 HICON 句柄泄漏（不需要 DestroyIcon 配对）。
//
//  图形语义：绿色四角框选括号 + 中心十字，一眼可辨"截图工具"。
// ============================================================================

using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;

namespace ShotTool.App;

/// <summary>托盘图标生成器。</summary>
public static class TrayIconFactory
{
    /// <summary>品牌绿（与工具条选中态 #00C375 同源）。</summary>
    private static readonly Color Accent = Color.FromArgb(0xFF, 0x00, 0xC3, 0x75);

    /// <summary>深色描边，保证在浅色任务栏上也有边界。</summary>
    private static readonly Color Outline = Color.FromArgb(0xCC, 0x1B, 0x1B, 0x1F);

    /// <summary>生成多尺寸托盘图标（16/24/32/48 各一张，系统按需取用）。</summary>
    public static Icon Create()
    {
        int[] sizes = { 16, 24, 32, 48 };
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

        float s = size;
        float inset = Math.Max(0.5f, s * 0.035f);
        var tile = new RectangleF(inset, inset, s - inset * 2, s - inset * 2);
        float radius = s * 0.22f;
        using GraphicsPath tilePath = RoundedRectangle(tile, radius);
        using (var shadow = new SolidBrush(Color.FromArgb(90, 0, 0, 0)))
            graphics.FillPath(shadow, tilePath);

        RectangleF face = RectangleF.Inflate(tile, -s * 0.025f, -s * 0.025f);
        using GraphicsPath facePath = RoundedRectangle(face, radius * 0.88f);
        using (var background = new LinearGradientBrush(face, Color.FromArgb(0x13, 0xA9, 0x9B), Color.FromArgb(0x13, 0x4B, 0x65), 45f))
            graphics.FillPath(background, facePath);

        // White capture frame: reads as both crop corners and a tiny screenshot card.
        float stroke = Math.Max(1.35f, s * 0.085f);
        float left = s * 0.23f, top = s * 0.23f, right = s * 0.77f, bottom = s * 0.77f;
        float arm = (right - left) * 0.32f;
        using var frame = new Pen(Color.FromArgb(245, 255, 255, 255), stroke) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        graphics.DrawLine(frame, left, top + arm, left, top); graphics.DrawLine(frame, left, top, left + arm, top);
        graphics.DrawLine(frame, right - arm, top, right, top); graphics.DrawLine(frame, right, top, right, top + arm);
        graphics.DrawLine(frame, left, bottom - arm, left, bottom); graphics.DrawLine(frame, left, bottom, left + arm, bottom);
        graphics.DrawLine(frame, right - arm, bottom, right, bottom); graphics.DrawLine(frame, right, bottom - arm, right, bottom);

        // Camera aperture / cursor target, with a bright mint center.
        float lens = s * 0.19f;
        var lensRect = new RectangleF((s - lens) / 2f, (s - lens) / 2f, lens, lens);
        using (var lensFill = new SolidBrush(Color.FromArgb(220, 9, 37, 47))) graphics.FillEllipse(lensFill, lensRect);
        using (var lensRing = new Pen(Color.FromArgb(245, 255, 255, 255), Math.Max(1f, s * 0.055f))) graphics.DrawEllipse(lensRing, lensRect);
        float core = s * 0.055f;
        using var coreFill = new SolidBrush(Color.FromArgb(0x8B, 0xF5, 0xD4));
        graphics.FillEllipse(coreFill, (s - core) / 2f, (s - core) / 2f, core, core);
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
