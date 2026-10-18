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
        // 括号长度 / 线宽按比例，16px 下也是 3px 线宽，肉眼可辨
        float stroke = Math.Max(1.5f, s * 0.11f);
        float inset = s * 0.13f;
        float armRatio = 0.42f;

        float left = inset;
        float top = inset;
        float right = s - inset;
        float bottom = s - inset;
        float armX = (right - left) * armRatio;
        float armY = (bottom - top) * armRatio;

        using var penOutline = new Pen(Outline, stroke + Math.Max(0.6f, s * 0.05f))
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
        };
        using var penAccent = new Pen(Accent, stroke)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
        };

        // 先画深色底再画绿色，形成浅色任务栏上的描边
        foreach (Pen pen in new[] { penOutline, penAccent })
        {
            // 四角括号（NW / NE / SW / SE 各两段）
            graphics.DrawLine(pen, left, top, left + armX, top);
            graphics.DrawLine(pen, left, top, left, top + armY);

            graphics.DrawLine(pen, right - armX, top, right, top);
            graphics.DrawLine(pen, right, top, right, top + armY);

            graphics.DrawLine(pen, left, bottom - armY, left, bottom);
            graphics.DrawLine(pen, left, bottom, left + armX, bottom);

            graphics.DrawLine(pen, right - armX, bottom, right, bottom);
            graphics.DrawLine(pen, right, bottom - armY, right, bottom);
        }

        // 中心十字（截图准星）
        float centerX = s / 2f;
        float centerY = s / 2f;
        float crossArm = s * 0.16f;
        using var penCrossOutline = new Pen(Outline, Math.Max(1.4f, s * 0.10f)) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        using var penCross = new Pen(Color.White, Math.Max(1f, s * 0.07f)) { StartCap = LineCap.Round, EndCap = LineCap.Round };

        foreach (Pen pen in new[] { penCrossOutline, penCross })
        {
            graphics.DrawLine(pen, centerX - crossArm, centerY, centerX + crossArm, centerY);
            graphics.DrawLine(pen, centerX, centerY - crossArm, centerX, centerY + crossArm);
        }
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
