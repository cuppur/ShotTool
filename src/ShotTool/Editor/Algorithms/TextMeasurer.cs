// 文字测量与字体解析：GDI+（System.Drawing.Text）。测量结果写回 TextAnnotation 的实测包围盒，
// 绘制与导出使用同一套 StringFormat，保证"所见即所得"。
#nullable enable
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Text;
using ShotTool.Core;
using ShotTool.Editor.Model;

namespace ShotTool.Editor.Algorithms
{
    /// <summary>文字测量（物理像素）。线程：非线程安全，应由 UI 线程调用。</summary>
    public static class TextMeasurer
    {
        private static readonly object Gate = new object();
        private static readonly Dictionary<string, string> s_familyCache =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        private static HashSet<string>? s_installedFamilies;

        /// <summary>首选字体串（中英混排）：微软雅黑 UI → 微软雅黑 → Segoe UI。</summary>
        public static string PreferredFontList
        {
            get { return TextAnnotation.DefaultFontFamily; }
        }

        /// <summary>
        /// 把"候选字体串（逗号分隔）"解析成系统上真实存在的一个字体名；
        /// 全部缺失时回退到通用无衬线字体。
        /// </summary>
        public static string ResolveFontFamily(string? requested)
        {
            string list = string.IsNullOrWhiteSpace(requested) ? PreferredFontList : requested;
            lock (Gate)
            {
                if (s_familyCache.TryGetValue(list, out string? cached) && cached != null)
                {
                    return cached;
                }

                if (s_installedFamilies == null)
                {
                    HashSet<string> installed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    using (InstalledFontCollection collection = new InstalledFontCollection())
                    {
                        FontFamily[] families = collection.Families;
                        for (int i = 0; i < families.Length; i++)
                        {
                            installed.Add(families[i].Name);
                        }
                    }

                    s_installedFamilies = installed;
                }

                string resolved = FontFamily.GenericSansSerif.Name;
                string[] candidates = list.Split(',');
                for (int i = 0; i < candidates.Length; i++)
                {
                    string candidate = candidates[i].Trim();
                    if (candidate.Length > 0 && s_installedFamilies.Contains(candidate))
                    {
                        resolved = candidate;
                        break;
                    }
                }

                s_familyCache[list] = resolved;
                return resolved;
            }
        }

        /// <summary>创建物理像素字号的字体（调用方负责 Dispose）。</summary>
        public static Font CreateFont(string? fontFamily, double fontSizePhys)
        {
            double size = fontSizePhys > 1 ? fontSizePhys : 12;
            return new Font(ResolveFontFamily(fontFamily), (float)size, FontStyle.Regular, GraphicsUnit.Pixel);
        }

        /// <summary>测量文本（支持 \n 换行）。返回向上取整的物理像素尺寸。</summary>
        public static PhysSizeF Measure(string? text, double fontSizePhys, string? fontFamily)
        {
            if (string.IsNullOrEmpty(text))
            {
                return PhysSizeF.Empty;
            }

            using (Bitmap probe = new Bitmap(1, 1))
            using (Graphics graphics = Graphics.FromImage(probe))
            using (Font font = CreateFont(fontFamily, fontSizePhys))
            using (StringFormat format = CreateFormat())
            {
                graphics.TextRenderingHint = TextRenderingHint.AntiAlias;
                SizeF size = graphics.MeasureString(text, font, new PointF(0f, 0f), format);
                double width = Math.Ceiling(size.Width);
                double height = Math.Ceiling(size.Height);
                return new PhysSizeF(width > 1 ? width : 1, height > 1 ? height : 1);
            }
        }

        /// <summary>测量并把实测包围盒回填到标注（绘制/导出前调用）。</summary>
        public static void Fill(TextAnnotation annotation)
        {
            if (annotation == null || annotation.IsEmptyText)
            {
                return;
            }

            PhysSizeF size = Measure(annotation.Text, annotation.FontSizePhys, annotation.FontFamily);
            annotation.SetMeasuredBounds(
                new PhysRectF(annotation.TopLeft.X, annotation.TopLeft.Y, size.Width, size.Height));
        }

        /// <summary>绘制与测量共用的 StringFormat（调用方负责 Dispose）。</summary>
        public static StringFormat CreateFormat()
        {
            StringFormat format = (StringFormat)StringFormat.GenericTypographic.Clone();
            format.FormatFlags |= StringFormatFlags.MeasureTrailingSpaces | StringFormatFlags.NoClip;
            format.Trimming = StringTrimming.None;
            return format;
        }
    }
}
