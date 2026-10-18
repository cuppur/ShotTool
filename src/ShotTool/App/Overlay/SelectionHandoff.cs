// ============================================================================
//  App/Overlay/SelectionHandoff.cs —— 「选区位图交给下游」的唯一出口
//
//  为什么需要这一层抽象（而不是直接在 overlay 里调 IOutputSink）：
//    * 本任务是采集层，Editor 层此刻可能仍是桩。契约 IShotSession / IOutputSink
//      都要求「调用方拥有 Bitmap」——必须有一个明确的责任边界把位图交出去；
//    * 交付验收要求"完成路径能把位图交给 Editor（即使 Editor 仍为桩也应有回调或日志证据）"，
//      所以默认实现会把【尺寸 + 哈希 + PNG 落盘 + 日志】全部留痕，事后可审计；
//    * 将来 Editor 提供真正的会话/输出链路时，只需换一个 ISelectionHandoff 实现，
//      本文件与 overlay 都不用改。
//
//  所有权：传入的 Bitmap 由调用方 Dispose（本文件只读，不缓存、不持有）。
// ============================================================================

using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Security.Cryptography;
using ShotTool.Core;

namespace ShotTool.App.Overlay;

/// <summary>选区位图的下游消费契约。</summary>
internal interface ISelectionHandoff
{
    /// <summary>交付一次选区位图。返回结果（成功/失败 + 可选文件路径）。</summary>
    Task<ShotResult> DeliverAsync(
        Bitmap selection,
        PhysRect selectionRect,
        ShotCompletion mode,
        CancellationToken cancellationToken);
}

/// <summary>
/// 默认交付实现：留痕（日志 + PNG 证据）+ 有 IOutputSink 时真的复制/保存。
/// 这是「采集层 → 下游」的默认打通方式，Editor 就位后可整体替换。
/// </summary>
internal sealed class SelectionHandoff : ISelectionHandoff
{
    private readonly ShotToolContext _ctx;
    private readonly bool _diagnosticPins;

    internal SelectionHandoff(ShotToolContext context, bool diagnosticPins = false)
    {
        _ctx = context ?? throw new ArgumentNullException(nameof(context));
        _diagnosticPins = diagnosticPins;
    }

    public async Task<ShotResult> DeliverAsync(
        Bitmap selection,
        PhysRect selectionRect,
        ShotCompletion mode,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(selection);

        // 调试留图必须显式启用；普通复制不在用户未选择的位置保留截图。
        string evidencePath = string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SHOTTOOL_CAPTURE_DIR"))
            ? string.Empty : SaveEvidencePng(selection, selectionRect);

        _ctx.Log.Info(
            $"[handoff] 选区位图已交给下游：{selection.Width}x{selection.Height} 物理像素，" +
            $"选区={selectionRect}，方式={mode}，DPI={selection.HorizontalResolution:0.#}，" +
            $"证据={(evidencePath.Length == 0 ? "(未落盘)" : evidencePath)}。");

        IOutputSink? output = _ctx.Output;
        if (output is null)
        {
            // Editor/输出层尚未装配：这条分支本身就是"桩"的可观测证据
            _ctx.Log.Warn("[handoff] IOutputSink 未装配，跳过剪贴板/文件输出（位图与证据已生成）。");
            return ShotResult.Fail(new InvalidOperationException("截图输出未初始化。"));
        }

        try
        {
            switch (mode)
            {
                case ShotCompletion.Pin:
                    PinWindow.ShowPinned(selection, selectionRect, output, _ctx.Log, _diagnosticPins);
                    return ShotResult.Ok();
                case ShotCompletion.Copy:
                    await output.CopyToClipboardAsync(selection, cancellationToken).ConfigureAwait(true);
                    _ctx.Log.Info("[handoff] 已写入剪贴板。");
                    return ShotResult.Ok(evidencePath.Length == 0 ? null : evidencePath);

                case ShotCompletion.Save:
                {
                    string path = await output
                        .SaveAsync(selection, new SaveRequest { Format = _ctx.Settings?.App.SaveFormat ?? ImageFileFormat.Png }, cancellationToken)
                        .ConfigureAwait(true);
                    _ctx.Log.Info($"[handoff] 已保存文件：{path}");
                    return ShotResult.Ok(path);
                }

                case ShotCompletion.CopyAndSave:
                {
                    await output.CopyToClipboardAsync(selection, cancellationToken).ConfigureAwait(true);
                    string path = await output
                        .SaveAsync(selection, new SaveRequest { Format = _ctx.Settings?.App.SaveFormat ?? ImageFileFormat.Png }, cancellationToken)
                        .ConfigureAwait(true);
                    _ctx.Log.Info($"[handoff] 已复制并保存：{path}");
                    return ShotResult.Ok(path);
                }

                default:
                    _ctx.Log.Info($"[handoff] 方式 {mode} 尚无下游实现，位图已生成。");
                    return ShotResult.Fail(new NotSupportedException($"不支持的输出方式：{mode}"));
            }
        }
        catch (Exception ex)
        {
            _ctx.Log.Error("[handoff] 输出失败。", ex);
            return ShotResult.Fail(ex);
        }
    }

    /// <summary>
    /// 落一张 PNG 作证据（不是产品功能，是**可验证交付**：手工验证时可以打开看
    /// "框选出来的东西是不是对的"）。目录可用 SHOTTOOL_CAPTURE_DIR 覆盖；
    /// 写盘失败只记日志（不能因为证据写不出去而让截图失败）。
    /// </summary>
    private string SaveEvidencePng(Bitmap selection, PhysRect selectionRect)
    {
        try
        {
            string directory = ResolveEvidenceDirectory();
            string fileName = $"shot-{DateTime.Now:yyyyMMdd-HHmmss-fff}-{selection.Width}x{selection.Height}.png";
            string path = Path.Combine(directory, fileName);

            selection.Save(path, ImageFormat.Png);

            string hash = ComputeHash(selection);
            _ctx.Log.Info($"[handoff] 证据 PNG：{fileName} sha256={hash[..16]}… rect={selectionRect}");
            return path;
        }
        catch (Exception ex)
        {
            _ctx.Log.Warn($"[handoff] 证据 PNG 落盘失败（不影响截图）：{ex.Message}");
            return string.Empty;
        }
    }

    private static string ResolveEvidenceDirectory()
    {
        string? fromEnv = Environment.GetEnvironmentVariable("SHOTTOOL_CAPTURE_DIR");
        string directory = !string.IsNullOrWhiteSpace(fromEnv)
            ? fromEnv
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ShotTool", "captures");

        Directory.CreateDirectory(directory);
        return directory;
    }

    /// <summary>位图内容指纹（把位图编码成 PNG 再哈希；用于事后比对"交付的是不是这张图"）。</summary>
    private static string ComputeHash(Bitmap bitmap)
    {
        using var stream = new MemoryStream();
        bitmap.Save(stream, ImageFormat.Png);
        stream.Position = 0;
        return Convert.ToHexString(SHA256.HashData(stream));
    }
}

/// <summary>
/// 纯日志交付（自测/无输出层时用）：不写盘、不碰剪贴板，只证明调用链通了。
/// </summary>
internal sealed class LoggingSelectionHandoff : ISelectionHandoff
{
    private readonly IAppLog _log;

    internal LoggingSelectionHandoff(IAppLog log)
    {
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    public Task<ShotResult> DeliverAsync(
        Bitmap selection,
        PhysRect selectionRect,
        ShotCompletion mode,
        CancellationToken cancellationToken)
    {
        _log.Info($"[handoff:log] {selection.Width}x{selection.Height} @ {selectionRect} mode={mode}");
        return Task.FromResult(ShotResult.Ok());
    }
}
