using System.Buffers.Binary;
using System.Drawing.Imaging;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Text;
using ShotTool.Core;

namespace ShotTool.Output;

/// <summary>
/// <see cref="IOutputSink"/> 的默认实现：剪贴板 + 文件保存。
///
/// <para>
/// <b>剪贴板</b>：同时写三种格式，覆盖不同宿主：
/// <list type="bullet">
///   <item><c>CF_DIBV5</c>（32bpp BGRA，保留 alpha） —— 现代宿主（Word / Teams / 部分浏览器）优先读取；</item>
///   <item><c>CF_DIB</c>（32bpp，alpha 强制不透明） —— 与微信原生截图一致，兼容只认 DIB 的老宿主；</item>
///   <item><c>"PNG"</c>（注册格式） —— 保留无损透明度，给支持 PNG 粘贴的编辑器。</item>
/// </list>
/// 像素一律自下而上（DIB 约定），行对齐由 32bpp 自然满足。
/// </para>
/// <para>
/// <b>保存</b>：默认目录 <c>Pictures\ShotTool</c>，PNG 默认注入 <c>pHYs</c> DPI 元数据
/// （GDI+ 自己不写 pHYs，高 DPI 屏上打开会被系统按 96dpi 显示而偏小）。
/// </para>
/// <para>
/// <b>线程</b>：<see cref="CopyToClipboardAsync"/> 必须在有消息队列的线程（UI 线程）调用，
/// 实现内部同步完成并通过 <see cref="Task.CompletedTask"/> 返回；
/// <see cref="SaveAsync"/> 在调用线程克隆位图后把编码放到线程池，避免阻塞 UI。
/// </para>
/// </summary>
public sealed class ImageOutputSink : IOutputSink
{
    private const uint CfDib = 8;
    private const uint CfDibV5 = 17;
    private const uint GmemMoveable = 0x0002;
    private const uint GmemZeroInit = 0x0040;

    private const int BitmapInfoHeaderSize = 40;
    private const int BitmapV5HeaderSize = 124;
    private const uint BiRgb = 0;
    private const uint BiBitfields = 3;
    private const uint LcsWindowsColorSpace = 0x73524742; // 'sRGB'
    private const uint LcsGmImages = 4;

    private const string PngClipboardFormat = "PNG";

    private readonly ISettingsStore _settings;
    private readonly IAppLog _log;
    private readonly SynchronizationContext? _uiContext;
    private readonly uint _pngClipboardFormat;

    /// <summary>创建输出通道。应在 UI 线程构造（用于保存对话框的上下文捕获）。</summary>
    public ImageOutputSink(ISettingsStore settings, IAppLog? log = null)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _log = log ?? NullLog.Instance;
        _uiContext = SynchronizationContext.Current;

        try
        {
            _pngClipboardFormat = RegisterClipboardFormat(PngClipboardFormat);
        }
        catch (Exception ex)
        {
            _pngClipboardFormat = 0;
            _log.Warn($"注册 PNG 剪贴板格式失败（PNG 粘贴不可用）：{ex.Message}");
        }
    }

    /// <summary>默认保存目录（<c>Pictures\ShotTool</c>；Pictures 不可用时退回用户目录）。</summary>
    public static string DefaultSaveDirectory
    {
        get
        {
            string pictures = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
            if (string.IsNullOrWhiteSpace(pictures))
            {
                pictures = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    "Pictures");
            }

            return Path.Combine(pictures, "ShotTool");
        }
    }

    // ==================================================================
    // 剪贴板
    // ==================================================================

    /// <inheritdoc />
    public Task CopyToClipboardAsync(Bitmap image, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(image);
        cancellationToken.ThrowIfCancellationRequested();

        if (Thread.CurrentThread.GetApartmentState() != ApartmentState.STA)
        {
            // 不致命：OpenClipboard 在 MTA 上通常也能成功，但数据在进程退出后可能不持久。
            _log.Warn("剪贴板写入不在 STA 线程上执行，兼容性可能下降。");
        }

        WriteClipboard(image);
        _log.Info($"已复制到剪贴板：{image.Width}×{image.Height}");
        return Task.CompletedTask;
    }

    private void WriteClipboard(Bitmap source)
    {
        using Bitmap argb = ToArgb32(source);
        int dpi = SafeDpi(source.HorizontalResolution);

        // CF_DIB：alpha 强制不透明。部分老宿主把 A=0 当全透明，粘贴出来是黑块。
        byte[] dib = BuildDib(argb, v5: false, forceOpaqueAlpha: true, dpi);
        byte[] dibV5 = BuildDib(argb, v5: true, forceOpaqueAlpha: false, dpi);
        byte[]? png = null;
        try
        {
            png = EncodePng(argb, embedDpi: true);
        }
        catch (Exception ex)
        {
            _log.Warn($"PNG 编码失败，仅写 DIB/DIBV5：{ex.Message}");
        }

        const int attempts = 6;
        for (int attempt = 0; attempt < attempts; attempt++)
        {
            if (OpenClipboard(IntPtr.Zero))
            {
                try
                {
                    if (!EmptyClipboard())
                    {
                        throw new InvalidOperationException(
                            $"EmptyClipboard 失败（Win32={Marshal.GetLastWin32Error()}）");
                    }

                    SetClipboardBlock(CfDibV5, dibV5);
                    SetClipboardBlock(CfDib, dib);

                    if (png is not null && _pngClipboardFormat != 0)
                    {
                        SetClipboardBlock(_pngClipboardFormat, png);
                    }

                    return;
                }
                finally
                {
                    CloseClipboard();
                }
            }

            // 剪贴板被其它进程（RDP / 剪贴板管理器）短暂占用是常态，退避重试。
            Thread.Sleep(15 + (attempt * 10));
        }

        throw new InvalidOperationException(
            $"无法打开剪贴板（Win32={Marshal.GetLastWin32Error()}），可能有其它程序正在占用。");
    }

    /// <summary>把字节块放进 HGLOBAL 并交给剪贴板。成功后所有权归系统，不能 GlobalFree。</summary>
    private static void SetClipboardBlock(uint format, byte[] data)
    {
        IntPtr handle = AllocGlobal(data);
        try
        {
            if (SetClipboardData(format, handle) == IntPtr.Zero)
            {
                throw new InvalidOperationException(
                    $"SetClipboardData(0x{format:X}) 失败（Win32={Marshal.GetLastWin32Error()}）");
            }

            // 成功：所有权转移，handle 不再由我们释放。
        }
        catch
        {
            GlobalFree(handle);
            throw;
        }
    }

    private static IntPtr AllocGlobal(byte[] data)
    {
        IntPtr handle = GlobalAlloc(GmemMoveable | GmemZeroInit, (UIntPtr)(uint)data.Length);
        if (handle == IntPtr.Zero)
        {
            throw new OutOfMemoryException($"GlobalAlloc({data.Length}) 失败（Win32={Marshal.GetLastWin32Error()}）");
        }

        IntPtr pointer = GlobalLock(handle);
        if (pointer == IntPtr.Zero)
        {
            GlobalFree(handle);
            throw new OutOfMemoryException($"GlobalLock 失败（Win32={Marshal.GetLastWin32Error()}）");
        }

        try
        {
            Marshal.Copy(data, 0, pointer, data.Length);
        }
        finally
        {
            GlobalUnlock(handle);
        }

        return handle;
    }

    // ==================================================================
    // 保存
    // ==================================================================

    /// <inheritdoc />
    public async Task<string> SaveAsync(Bitmap image, SaveRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(image);
        request ??= new SaveRequest();

        string path = ResolvePath(request);

        if (request.PromptUser)
        {
            string? chosen = InvokeOnUi(() => PromptForPath(path, request.Format));
            if (string.IsNullOrWhiteSpace(chosen))
            {
                throw new OperationCanceledException("用户取消了保存。");
            }

            path = chosen;
        }

        cancellationToken.ThrowIfCancellationRequested();

        // Bitmap 非线程安全：在调用线程克隆快照，之后只碰快照。
        using Bitmap snapshot = ToArgb32(image);
        string target = path;
        ImageFileFormat format = request.Format;
        bool embedDpi = request.EmbedDpi;

        await Task.Run(() => WriteFileAtomically(snapshot, target, format, embedDpi), cancellationToken)
            .ConfigureAwait(true);

        _log.Info($"已保存：{target}");
        return target;
    }

    /// <inheritdoc />
    public string ResolvePath(SaveRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        string directory = request.Directory ?? _settings.App.SaveDirectory;
        if (string.IsNullOrWhiteSpace(directory))
        {
            directory = DefaultSaveDirectory;
        }
        else
        {
            directory = SafeExpand(directory);
        }

        string fileName = string.IsNullOrWhiteSpace(request.FileName)
            ? $"ShotTool_{DateTime.Now:yyyyMMdd_HHmmss_fff}"
            : SanitizeFileName(request.FileName!);

        return Path.Combine(directory, fileName + ExtensionFor(request.Format));
    }

    private static string ExtensionFor(ImageFileFormat format) => format switch
    {
        ImageFileFormat.Jpeg => ".jpg",
        ImageFileFormat.Bmp => ".bmp",
        _ => ".png",
    };

    private static string SanitizeFileName(string name)
    {
        var builder = new StringBuilder(name.Length);
        char[] invalid = Path.GetInvalidFileNameChars();
        foreach (char c in name)
        {
            builder.Append(Array.IndexOf(invalid, c) >= 0 || c is ':' ? '_' : c);
        }

        string cleaned = builder.ToString().Trim();
        return cleaned.Length == 0 ? "ShotTool" : cleaned;
    }

    private string? PromptForPath(string suggestedPath, ImageFileFormat format)
    {
        using var dialog = new SaveFileDialog
        {
            Title = "保存截图",
            FileName = Path.GetFileName(suggestedPath),
            InitialDirectory = Path.GetDirectoryName(suggestedPath) ?? DefaultSaveDirectory,
            AddExtension = true,
            OverwritePrompt = true,
            Filter = format switch
            {
                ImageFileFormat.Jpeg => "JPEG 图片 (*.jpg)|*.jpg|PNG 图片 (*.png)|*.png|位图 (*.bmp)|*.bmp",
                ImageFileFormat.Bmp => "位图 (*.bmp)|*.bmp|PNG 图片 (*.png)|*.png|JPEG 图片 (*.jpg)|*.jpg",
                _ => "PNG 图片 (*.png)|*.png|JPEG 图片 (*.jpg)|*.jpg|位图 (*.bmp)|*.bmp",
            },
        };

        return dialog.ShowDialog() == DialogResult.OK ? dialog.FileName : null;
    }

    private static void WriteFileAtomically(Bitmap bitmap, string path, ImageFileFormat format, bool embedDpi)
    {
        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        string temp = path + ".tmp-" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            switch (format)
            {
                case ImageFileFormat.Png:
                    File.WriteAllBytes(temp, EncodePng(bitmap, embedDpi));
                    break;

                case ImageFileFormat.Jpeg:
                    SaveJpeg(bitmap, temp);
                    break;

                default:
                    bitmap.Save(temp, ImageFormat.Bmp);
                    break;
            }

            File.Move(temp, path, overwrite: true);
        }
        catch
        {
            TryDelete(temp);
            throw;
        }
    }

    private static void SaveJpeg(Bitmap bitmap, string path)
    {
        ImageCodecInfo? codec = ImageCodecInfo.GetImageEncoders()
            .FirstOrDefault(c => c.FormatID == ImageFormat.Jpeg.Guid);

        if (codec is null)
        {
            bitmap.Save(path, ImageFormat.Jpeg);
            return;
        }

        using var parameters = new EncoderParameters(1);
        parameters.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 92L);
        bitmap.Save(path, codec, parameters);
    }

    // ==================================================================
    // DIB 构造
    // ==================================================================

    /// <summary>
    /// 构造 CF_DIB / CF_DIBV5 字节块：header + 自下而上的 32bpp BGRA 像素。
    /// </summary>
    private static byte[] BuildDib(Bitmap argb, bool v5, bool forceOpaqueAlpha, int dpi)
    {
        int width = argb.Width;
        int height = argb.Height;
        int stride = width * 4;
        int headerSize = v5 ? BitmapV5HeaderSize : BitmapInfoHeaderSize;
        int pixelsPerMeter = (int)Math.Round(dpi / 0.0254);

        byte[] buffer = new byte[headerSize + (stride * height)];
        Span<byte> header = buffer.AsSpan(0, headerSize);

        if (v5)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(header[0..], BitmapV5HeaderSize);
            BinaryPrimitives.WriteInt32LittleEndian(header[4..], width);
            BinaryPrimitives.WriteInt32LittleEndian(header[8..], height);
            BinaryPrimitives.WriteUInt16LittleEndian(header[12..], 1);
            BinaryPrimitives.WriteUInt16LittleEndian(header[14..], 32);
            BinaryPrimitives.WriteUInt32LittleEndian(header[16..], BiBitfields);
            BinaryPrimitives.WriteUInt32LittleEndian(header[20..], (uint)(stride * height));
            BinaryPrimitives.WriteInt32LittleEndian(header[24..], pixelsPerMeter);
            BinaryPrimitives.WriteInt32LittleEndian(header[28..], pixelsPerMeter);
            BinaryPrimitives.WriteUInt32LittleEndian(header[32..], 0); // ClrUsed
            BinaryPrimitives.WriteUInt32LittleEndian(header[36..], 0); // ClrImportant
            BinaryPrimitives.WriteUInt32LittleEndian(header[40..], 0x00FF0000); // RedMask
            BinaryPrimitives.WriteUInt32LittleEndian(header[44..], 0x0000FF00); // GreenMask
            BinaryPrimitives.WriteUInt32LittleEndian(header[48..], 0x000000FF); // BlueMask
            BinaryPrimitives.WriteUInt32LittleEndian(header[52..], 0xFF000000); // AlphaMask
            BinaryPrimitives.WriteUInt32LittleEndian(header[56..], LcsWindowsColorSpace);
            // 60..96: CIEXYZTRIPLE endpoints = 0（LCS_sRGB 时不使用）
            BinaryPrimitives.WriteUInt32LittleEndian(header[108..], LcsGmImages); // Intent
            // 112..124: ProfileData / ProfileSize / Reserved = 0
        }
        else
        {
            BinaryPrimitives.WriteUInt32LittleEndian(header[0..], BitmapInfoHeaderSize);
            BinaryPrimitives.WriteInt32LittleEndian(header[4..], width);
            BinaryPrimitives.WriteInt32LittleEndian(header[8..], height);
            BinaryPrimitives.WriteUInt16LittleEndian(header[12..], 1);
            BinaryPrimitives.WriteUInt16LittleEndian(header[14..], 32);
            BinaryPrimitives.WriteUInt32LittleEndian(header[16..], BiRgb);
            BinaryPrimitives.WriteUInt32LittleEndian(header[20..], (uint)(stride * height));
            BinaryPrimitives.WriteInt32LittleEndian(header[24..], pixelsPerMeter);
            BinaryPrimitives.WriteInt32LittleEndian(header[28..], pixelsPerMeter);
            BinaryPrimitives.WriteUInt32LittleEndian(header[32..], 0);
            BinaryPrimitives.WriteUInt32LittleEndian(header[36..], 0);
        }

        BitmapData data = argb.LockBits(
            new Rectangle(0, 0, width, height),
            ImageLockMode.ReadOnly,
            PixelFormat.Format32bppArgb);

        try
        {
            unsafe
            {
                byte* sourceBase = (byte*)data.Scan0;
                fixed (byte* destinationBase = buffer)
                {
                    for (int y = 0; y < height; y++)
                    {
                        byte* sourceRow = sourceBase + ((long)y * data.Stride);
                        byte* destinationRow = destinationBase + headerSize + ((long)(height - 1 - y) * stride);
                        Buffer.MemoryCopy(sourceRow, destinationRow, stride, stride);

                        if (forceOpaqueAlpha)
                        {
                            int* pixel = (int*)destinationRow;
                            for (int x = 0; x < width; x++)
                            {
                                pixel[x] |= unchecked((int)0xFF000000);
                            }
                        }
                    }
                }
            }
        }
        finally
        {
            argb.UnlockBits(data);
        }

        return buffer;
    }

    // ==================================================================
    // PNG 编码与 pHYs（DPI）注入
    // ==================================================================

    private static byte[] EncodePng(Bitmap bitmap, bool embedDpi)
    {
        using var stream = new MemoryStream(Math.Max(4096, bitmap.Width * bitmap.Height / 2));
        bitmap.Save(stream, ImageFormat.Png);
        byte[] png = stream.ToArray();

        if (!embedDpi)
        {
            // GDI+ 的 PNG 编码器本身会写一个 96dpi 的 pHYs，用户要求不写时得显式删掉。
            return PngDpiWriter.RemoveResolution(png);
        }

        try
        {
            return PngDpiWriter.SetResolution(
                png,
                SafeDpi(bitmap.HorizontalResolution),
                SafeDpi(bitmap.VerticalResolution));
        }
        catch
        {
            // pHYs 注入只是体验优化，失败不应导致保存失败。
            return png;
        }
    }

    /// <summary>把位图统一成 32bppArgb（保留 alpha），后续 DIB / PNG 构造只处理这一种格式。</summary>
    private static Bitmap ToArgb32(Bitmap source)
    {
        if (source.PixelFormat == PixelFormat.Format32bppArgb)
        {
            return (Bitmap)source.Clone();
        }

        var copy = new Bitmap(source.Width, source.Height, PixelFormat.Format32bppArgb);
        try
        {
            copy.SetResolution(
                SafeDpi(source.HorizontalResolution),
                SafeDpi(source.VerticalResolution));

            using Graphics graphics = Graphics.FromImage(copy);
            graphics.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceCopy;
            graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
            graphics.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;
            graphics.DrawImage(
                source,
                new Rectangle(0, 0, source.Width, source.Height),
                0,
                0,
                source.Width,
                source.Height,
                GraphicsUnit.Pixel);
            return copy;
        }
        catch
        {
            copy.Dispose();
            throw;
        }
    }

    private static int SafeDpi(float dpi)
    {
        if (float.IsNaN(dpi) || float.IsInfinity(dpi) || dpi < 10f || dpi > 2000f)
        {
            return 96;
        }

        return (int)Math.Round(dpi);
    }

    private static string SafeExpand(string path)
    {
        try
        {
            return Environment.ExpandEnvironmentVariables(path);
        }
        catch
        {
            return path;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // 临时文件清理失败不影响主流程。
        }
    }

    private T InvokeOnUi<T>(Func<T> callback)
    {
        // 已经在 UI 上下文（最常见）或没有捕获上下文（控制台测试）时直接执行。
        if (_uiContext is null || ReferenceEquals(SynchronizationContext.Current, _uiContext))
        {
            return callback();
        }

        T? result = default;
        Exception? failure = null;
        _uiContext.Send(
            _ =>
            {
                try
                {
                    result = callback();
                }
                catch (Exception ex)
                {
                    failure = ex;
                }
            },
            null);

        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }

        return result!;
    }

    // ==================================================================
    // Win32
    // ==================================================================

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenClipboard(IntPtr hWndNewOwner);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EmptyClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetClipboardData(uint uFormat, IntPtr hMem);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint RegisterClipboardFormat(string lpszFormat);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalAlloc(uint uFlags, UIntPtr dwBytes);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalLock(IntPtr hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalUnlock(IntPtr hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalFree(IntPtr hMem);

    private sealed class NullLog : IAppLog
    {
        public static readonly NullLog Instance = new();

        public void Info(string message)
        {
        }

        public void Warn(string message)
        {
        }

        public void Error(string message, Exception? error = null)
        {
        }
    }
}

/// <summary>
/// PNG 的 <c>pHYs</c> chunk 写入器（GDI+ 的 PNG 编码器不写 DPI，需要自己补）。
/// 只处理标准 PNG：签名 + IHDR + …… ；任何异常都向上抛，由调用方决定降级。
/// </summary>
internal static class PngDpiWriter
{
    private static readonly byte[] Signature = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
    private static readonly uint[] CrcTable = BuildCrcTable();

    /// <summary>删除 pHYs chunk（GDI+ 默认会写 96dpi 的 pHYs，EmbedDpi=false 时需要移除）。</summary>
    public static byte[] RemoveResolution(byte[] png)
    {
        if (png.Length < Signature.Length + 12 || !png.AsSpan(0, Signature.Length).SequenceEqual(Signature))
        {
            return png;
        }

        int offset = Signature.Length;
        int physStart = -1;
        int physEnd = -1;

        while (offset + 8 <= png.Length)
        {
            uint length = BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(offset, 4));
            if (length > int.MaxValue)
            {
                return png;
            }

            string type = Encoding.ASCII.GetString(png, offset + 4, 4);
            int chunkTotal = 12 + (int)length;
            if (offset + chunkTotal > png.Length)
            {
                return png;
            }

            if (type == "pHYs")
            {
                physStart = offset;
                physEnd = offset + chunkTotal;
                break;
            }

            if (type == "IEND")
            {
                break;
            }

            offset += chunkTotal;
        }

        if (physStart < 0)
        {
            return png;
        }

        var result = new byte[png.Length - (physEnd - physStart)];
        Buffer.BlockCopy(png, 0, result, 0, physStart);
        Buffer.BlockCopy(png, physEnd, result, physStart, png.Length - physEnd);
        return result;
    }

    /// <summary>插入（或替换）pHYs chunk，返回新数组。像素/其它 chunk 原样保留。</summary>
    public static byte[] SetResolution(byte[] png, int dpiX, int dpiY)    {
        if (png.Length < Signature.Length + 12 || !png.AsSpan(0, Signature.Length).SequenceEqual(Signature))
        {
            return png;
        }

        int pixelsPerMeterX = (int)Math.Round(dpiX / 0.0254);
        int pixelsPerMeterY = (int)Math.Round(dpiY / 0.0254);

        byte[] phys = BuildPhysChunk(pixelsPerMeterX, pixelsPerMeterY);

        int offset = Signature.Length;
        int insertAt = -1;

        while (offset + 8 <= png.Length)
        {
            uint length = BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(offset, 4));
            if (length > int.MaxValue)
            {
                return png;
            }

            string type = Encoding.ASCII.GetString(png, offset + 4, 4);
            int chunkTotal = 12 + (int)length;
            if (offset + chunkTotal > png.Length)
            {
                return png;
            }

            if (type == "pHYs")
            {
                // 替换：内容长度固定 9 字节，直接覆盖 data 区（CRC 后写）。
                Array.Copy(phys, 8, png, offset + 8, 9);
                BinaryPrimitives.WriteUInt32BigEndian(png.AsSpan(offset + 8 + 9, 4), Crc32(png.AsSpan(offset + 4, 4 + 9)));
                return png;
            }

            if (type == "IHDR" && insertAt < 0)
            {
                insertAt = offset + chunkTotal;
            }

            if (type == "IEND")
            {
                break;
            }

            offset += chunkTotal;
        }

        if (insertAt < 0)
        {
            return png;
        }

        var result = new byte[png.Length + phys.Length];
        Buffer.BlockCopy(png, 0, result, 0, insertAt);
        Buffer.BlockCopy(phys, 0, result, insertAt, phys.Length);
        Buffer.BlockCopy(png, insertAt, result, insertAt + phys.Length, png.Length - insertAt);
        return result;
    }

    private static byte[] BuildPhysChunk(int pixelsPerMeterX, int pixelsPerMeterY)
    {
        byte[] chunk = new byte[21];
        BinaryPrimitives.WriteUInt32BigEndian(chunk.AsSpan(0, 4), 9);
        Encoding.ASCII.GetBytes("pHYs", chunk.AsSpan(4, 4));
        BinaryPrimitives.WriteInt32BigEndian(chunk.AsSpan(8, 4), pixelsPerMeterX);
        BinaryPrimitives.WriteInt32BigEndian(chunk.AsSpan(12, 4), pixelsPerMeterY);
        chunk[16] = 1; // unit: 1 = 米
        BinaryPrimitives.WriteUInt32BigEndian(chunk.AsSpan(17, 4), Crc32(chunk.AsSpan(4, 13)));
        return chunk;
    }

    private static uint Crc32(ReadOnlySpan<byte> data)
    {
        uint crc = 0xFFFFFFFFu;
        foreach (byte value in data)
        {
            crc = CrcTable[(crc ^ value) & 0xFF] ^ (crc >> 8);
        }

        return crc ^ 0xFFFFFFFFu;
    }

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            }

            table[n] = c;
        }

        return table;
    }
}
