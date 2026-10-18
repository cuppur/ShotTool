using System.Text;

namespace ShotTool.Ai.Translation;

// ============================================================================
//  NllbModelLocator.cs —— 模型定位与校验实现
//
//  约定目录（captain 定稿）:
//    %LOCALAPPDATA%\ShotTool\models\nllb-200-distilled-600M-int8\
//
//  ★ 路径口径：必须用 LocalAppData，不能用 Roaming。
//    模型 1.08 GiB，放 Roaming 会被域环境（企业/学校账号）同步到漫游服务器，
//    每次登录拖网速、可能撑爆漫游配额 —— 这是设计事故，已纠正。
//    历史位置 %APPDATA%\WeChatShot\models\... 仍做探测（见 InspectLegacy），
//    以免早期用户白下 1.08 GiB，但新安装不再写入那里。
//
//  约定文件（int8 档，来自 tfchan-lab/nllb-200-distilled-600M-onnx）:
//    encoder_model_int8.onnx        415,378,190 字节
//    decoder_model_int8.onnx        728,792,767 字节
//    tokenizer.json                  17,331,224 字节（另有官方 sentencepiece.bpe.model 可选）
//    config.json                              可选（读 eos / decoder_start 真值）
//    MODEL_CARD.txt                           可选但建议（记录来源/许可/署名/量化方式）
//    合计约 1.07 GiB
//
//  兼容性：不同导出/量化产物的文件名不一致，这里用候选名列表逐个尝试，
//  并额外搜索子目录 onnx\（官方 optimum 导出把 int8 放在 onnx\ 下）。
//
//  安全约束：本类**只做文件系统探测**，绝不加载 ONNX、不联网、不执行任何模型代码。
// ============================================================================

/// <summary>NLLB 模型定位器：纯文件系统探测，给出可操作诊断。</summary>
public sealed class NllbModelLocator : INllbModelLocator
{
    /// <summary>int8 档已知产物的精确字节数（用于校验与 UI 展示）。</summary>
    private static readonly (string FileName, long Bytes)[] KnownInt8Files =
    {
        ("encoder_model_int8.onnx", 415_378_190L),
        ("decoder_model_int8.onnx", 728_792_767L),
    };

    /// <summary>
    /// encoder 候选文件名，按优先级排列。
    /// 纯 encoder 优先；不含 merged 概念（encoder 不受 KV cache 影响）。
    /// </summary>
    private static readonly string[] EncoderCandidates =
    {
        "encoder_model_int8.onnx",
        "encoder_model.onnx",
        "encoder_int8.onnx",
        "encoder.onnx",
    };

    /// <summary>
    /// decoder 候选文件名，按优先级排列。
    /// ★ 重要：<c>decoder_model_merged*.onnx</c> 放在**最后**。optimum 的 merged 导出
    /// 额外需要 <c>past_key_values</c> 与 <c>use_cache_branch</c> 输入，本实现的贪心解码
    /// （use_cache=false 全量重跑）不提供 past，若只有 merged 文件会在 Run 时报缺输入。
    /// 优先选纯 decoder 才能保证开箱可用。
    /// </summary>
    private static readonly string[] DecoderCandidates =
    {
        "decoder_model_int8.onnx",
        "decoder_model.onnx",
        "decoder_int8.onnx",
        "decoder.onnx",
        "decoder_model_merged_int8.onnx",
        "decoder_model_merged.onnx",
    };

    private static readonly string[] TokenizerCandidates =
    {
        "tokenizer.json",
    };

    private static readonly string[] ConfigCandidates =
    {
        "config.json",
    };

    /// <summary>额外搜索的子目录（官方 optimum 导出把 int8 权重放在 onnx\ 下）。</summary>
    private static readonly string[] SubDirectories =
    {
        string.Empty,
        "onnx",
    };

    /// <summary>用户自定义目录；空串表示未覆盖，使用默认目录。</summary>
    private string _directoryOverride;

    public NllbModelLocator(string? directoryOverride = null, string? legacyDirectoryOverride = null)
    {
        _directoryOverride = string.IsNullOrWhiteSpace(directoryOverride)
            ? string.Empty : Environment.ExpandEnvironmentVariables(directoryOverride.Trim());
        // 设置页的默认目录不应遮住随包模型；只有真正自定义的路径才强制覆盖。
        if (string.Equals(_directoryOverride.TrimEnd(Path.DirectorySeparatorChar),
            DefaultModelDirectory.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
            _directoryOverride = string.Empty;
        if (!string.IsNullOrWhiteSpace(legacyDirectoryOverride))
        {
            LegacyModelDirectory = legacyDirectoryOverride;
        }
    }

    /// <summary>
    /// 优先级 1：随包自带（exe 同目录 models\...）。
    /// 用 AppContext.BaseDirectory 而不是 CurrentDirectory —— 后者取决于启动时的工作目录，
    /// 从快捷方式/命令行/资源管理器启动会得到不同结果。
    /// </summary>
    public string BundledModelDirectory { get; } = Path.Combine(
        AppContext.BaseDirectory,
        "models",
        "nllb-200-distilled-600M-int8");

    /// <inheritdoc />
    public string DefaultModelDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ShotTool",
        "models",
        "nllb-200-distilled-600M-int8");

    /// <inheritdoc />
    /// <remarks>
    /// 早期版本把模型放在 Roaming，1.08 GiB 会被域环境同步到漫游服务器（设计事故）。
    /// 保留探测仅为了让已下载的用户不必重下。
    /// </remarks>
    public string LegacyModelDirectory { get; private set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "WeChatShot",
        "models",
        "nllb-200-distilled-600M-int8");

    /// <inheritdoc />
    public string ActiveDirectory => ResolveActive().Directory;

    /// <inheritdoc />
    public NllbModelSource ActiveSource => ResolveActive().Source;

    /// <summary>
    /// 三级解析：自定义覆盖 &gt; 随包 &gt; 用户目录 &gt; 历史位置。
    /// 返回第一个**就绪**的；都不就绪时返回随包目录（引导用户往那里放），来源标 None。
    ///
    /// 为什么"就绪"而不是"存在"：目录可能只是个空壳（用户建了目录没放文件），
    /// 那种情况不该抢占优先级，否则会把真正可用的用户目录挡住。
    /// </summary>
    private (NllbModelSource Source, string Directory) ResolveActive()
    {
        // 显式覆盖优先（设置页手动指定）
        if (!string.IsNullOrWhiteSpace(_directoryOverride))
        {
            return (NllbModelSource.CustomOverride, _directoryOverride);
        }

        if (IsReadyAt(BundledModelDirectory)) return (NllbModelSource.Bundled, BundledModelDirectory);
        if (IsReadyAt(DefaultModelDirectory)) return (NllbModelSource.UserLocal, DefaultModelDirectory);
        if (IsReadyAt(LegacyModelDirectory)) return (NllbModelSource.Legacy, LegacyModelDirectory);

        // 都不就绪：指向随包目录（发布形态下用户的默认期待位置）
        return (NllbModelSource.None, BundledModelDirectory);
    }

    /// <summary>目录里三个必需文件是否齐备（只看存在与非空，不校验字节数 —— 那是 Inspect 的事）。</summary>
    private static bool IsReadyAt(string directory)
        => Directory.Exists(directory)
           && FindFile(directory, EncoderCandidates, out _) is not null
           && FindFile(directory, DecoderCandidates, out _) is not null
           && FindFile(directory, TokenizerCandidates, out _) is not null;

    /// <inheritdoc />
    public void SetDirectoryOverride(string? directory)
    {
        _directoryOverride = directory ?? string.Empty;
    }

    /// <inheritdoc />
    public NllbModelCheck Inspect()
    {
        var (source, dir) = ResolveActive();
        return InspectAt(dir, source);
    }

    /// <inheritdoc />
    public NllbModelCheck Inspect(string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory))
        {
            return Inspect();
        }

        return InspectAt(directory, NllbModelSource.CustomOverride);
    }

    /// <summary>在指定目录上做完整校验，并标注来源。</summary>
    private NllbModelCheck InspectAt(string dir, NllbModelSource source)
    {
        if (!Directory.Exists(dir))
        {
            return new NllbModelCheck(
                false, dir, null, null, null, null, "unknown", 0,
                new[]
                {
                    new NllbModelProblem("missing-dir", $"模型目录不存在：{dir}", dir, 0, 0),
                },
                source);
        }

        var problems = new List<NllbModelProblem>(4);

        // ---- encoder ----
        var encoder = FindFile(dir, EncoderCandidates, out var encoderWarnings);
        problems.AddRange(encoderWarnings);
        if (encoder is null)
        {
            problems.Add(new NllbModelProblem(
                "missing-file",
                $"缺少编码器权重（期望 {EncoderCandidates[0]}）",
                Path.Combine(dir, EncoderCandidates[0]),
                KnownBytesOf(EncoderCandidates[0]),
                0));
        }

        // ---- decoder ----
        var decoder = FindFile(dir, DecoderCandidates, out var decoderWarnings);
        problems.AddRange(decoderWarnings);
        if (decoder is null)
        {
            problems.Add(new NllbModelProblem(
                "missing-file",
                $"缺少解码器权重（期望 {DecoderCandidates[0]}）",
                Path.Combine(dir, DecoderCandidates[0]),
                KnownBytesOf(DecoderCandidates[0]),
                0));
        }

        // ---- tokenizer（硬性：没有它无法把文本变成 token）----
        var tokenizer = FindFile(dir, TokenizerCandidates, out var tokWarnings);
        problems.AddRange(tokWarnings);
        if (tokenizer is null)
        {
            problems.Add(new NllbModelProblem(
                "missing-file",
                "缺少 tokenizer.json（语言码 token 也从它解析，必需）",
                Path.Combine(dir, TokenizerCandidates[0]),
                KnownBytesOf(TokenizerCandidates[0]),
                0));
        }

        // ---- config.json（可选）----
        var config = FindFile(dir, ConfigCandidates, out _);

        var ready = problems.All(p => !p.IsBlocking);
        var totalBytes = (encoder?.Length ?? 0) + (decoder?.Length ?? 0) + (tokenizer?.Length ?? 0);

        return new NllbModelCheck(
            ready,
            dir,
            encoder?.FullName,
            decoder?.FullName,
            tokenizer?.FullName,
            config?.FullName,
            InferPrecision(encoder, decoder),
            totalBytes,
            problems,
            source);
    }

    /// <inheritdoc />
    public NllbModelCheck? InspectLegacy()
    {
        if (string.IsNullOrWhiteSpace(LegacyModelDirectory) || !Directory.Exists(LegacyModelDirectory))
        {
            return null;
        }

        // 与当前生效目录相同则无需报告「旧位置」
        if (string.Equals(
                Path.GetFullPath(LegacyModelDirectory).TrimEnd('\\'),
                Path.GetFullPath(ActiveDirectory).TrimEnd('\\'),
                StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var check = Inspect(LegacyModelDirectory);

        // 只要旧位置里能找到任一权重文件就值得提示迁移 —— 哪怕是半份下载（用户可能已下了几百 MB），
        // 严格要求 IsReady 会让这些人白下载。完整性由迁移后的 Inspect 再验。
        var hasAnyWeight = check.EncoderPath is not null || check.DecoderPath is not null;
        return hasAnyWeight ? check : null;
    }

    /// <inheritdoc />
    public bool TryMigrateLegacy(out string message)
    {
        var target = ActiveDirectory;

        if (Inspect(target).IsReady)
        {
            message = $"新位置已有可用模型，无需迁移：{target}";
            return false;
        }

        var legacy = InspectLegacy();
        if (legacy is null)
        {
            message = $"旧位置没有可用模型：{LegacyModelDirectory}";
            return false;
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(target) ?? target);

            if (!Directory.Exists(target))
            {
                // 同盘 Directory.Move 只是元数据改名，不重写 1 GB 数据
                Directory.Move(LegacyModelDirectory, target);
                message = $"已迁移模型（同盘移动，秒级完成）：{LegacyModelDirectory} → {target}";
                return true;
            }

            // 目标目录已存在（可能是空壳或半份文件）→ 逐文件搬并覆盖
            var moved = 0;
            foreach (var src in Directory.EnumerateFiles(LegacyModelDirectory))
            {
                var dst = Path.Combine(target, Path.GetFileName(src));
                File.Move(src, dst, overwrite: true);
                moved++;
            }

            // 连子目录（onnx\）一起搬
            foreach (var dir in Directory.EnumerateDirectories(LegacyModelDirectory))
            {
                var dstDir = Path.Combine(target, Path.GetFileName(dir));
                Directory.CreateDirectory(dstDir);
                foreach (var src in Directory.EnumerateFiles(dir))
                {
                    File.Move(src, Path.Combine(dstDir, Path.GetFileName(src)), overwrite: true);
                    moved++;
                }

                Directory.Delete(dir, recursive: false);
            }

            // 旧目录清空后删掉，避免留下 1 GB 空壳目录让用户困惑
            if (!Directory.EnumerateFileSystemEntries(LegacyModelDirectory).Any())
            {
                Directory.Delete(LegacyModelDirectory, recursive: false);
            }

            message = $"已迁移 {moved} 个文件：{LegacyModelDirectory} → {target}";
            return moved > 0;
        }
        catch (Exception ex)
        {
            message = $"迁移失败（{ex.GetType().Name}: {ex.Message}）。可手动把整个目录移动到：{target}";
            return false;
        }
    }

    /// <summary>
    /// 在约定目录（含 onnx\ 子目录）里按候选名找第一个存在的文件。
    /// 同时对「已知产物的精确字节数」做校验：不一致只产生**非阻断**提示，
    /// 因为用户可能用的是别的量化档（fp16 / 不同社区的 int8），仍然可用。
    /// </summary>
    private static FileInfo? FindFile(string dir, string[] candidates, out List<NllbModelProblem> warnings)
    {
        warnings = new List<NllbModelProblem>(0);

        foreach (var sub in SubDirectories)
        {
            var searchDir = string.IsNullOrEmpty(sub) ? dir : Path.Combine(dir, sub);
            if (!Directory.Exists(searchDir))
            {
                continue;
            }

            foreach (var name in candidates)
            {
                var path = Path.Combine(searchDir, name);
                if (!File.Exists(path))
                {
                    continue;
                }

                FileInfo info;
                try
                {
                    info = new FileInfo(path);
                    if (info.Length == 0)
                    {
                        warnings.Add(new NllbModelProblem(
                            "unreadable",
                            $"文件为空（0 字节），可能下载未完成：{name}",
                            path,
                            0,
                            0));
                        continue;
                    }
                }
                catch (Exception ex)
                {
                    warnings.Add(new NllbModelProblem(
                        "unreadable",
                        $"无法读取文件信息：{name}（{ex.GetType().Name}）",
                        path,
                        0,
                        0));
                    continue;
                }

                var expected = KnownBytesOf(name);
                if (expected > 0 && info.Length != expected)
                {
                    warnings.Add(new NllbModelProblem(
                        "size-mismatch",
                        $"{name} 字节数与已知产物不同（可能是其它量化档，仍允许尝试加载）",
                        path,
                        expected,
                        info.Length,
                        isBlocking: false));
                }

                return info;
            }
        }

        return null;
    }

    private static long KnownBytesOf(string fileName)
    {
        foreach (var (name, bytes) in KnownInt8Files)
        {
            if (string.Equals(name, fileName, StringComparison.OrdinalIgnoreCase))
            {
                return bytes;
            }
        }

        return fileName switch
        {
            "tokenizer.json" => 17_331_224L,
            _ => 0,
        };
    }

    /// <summary>从文件名推断精度档，仅用于 UI 展示。</summary>
    private static string InferPrecision(FileInfo? encoder, FileInfo? decoder)
    {
        var name = (encoder?.Name ?? string.Empty) + "|" + (decoder?.Name ?? string.Empty);
        if (name.Contains("int8", StringComparison.OrdinalIgnoreCase))
        {
            return "int8";
        }

        if (name.Contains("int4", StringComparison.OrdinalIgnoreCase))
        {
            return "int4";
        }

        if (name.Contains("fp16", StringComparison.OrdinalIgnoreCase))
        {
            return "fp16";
        }

        return encoder is null ? "unknown" : "fp32";
    }

    /// <summary>许可与署名声明。UI 设置页与 README 必须原样呈现，不得删改或简化为「可商用」。</summary>
    public const string LicenseNotice =
        "模型：NLLB-200-distilled-600M（Meta AI）\n" +
        "许可：CC-BY-NC-4.0 —— 仅限非商业用途，不得用于商业产品。\n" +
        "署名：Model card 与文档须保留 “NLLB-200 distilled 600M by Meta AI Research” 的署名。\n" +
        "ONNX 量化产物：tfchan-lab/nllb-200-distilled-600M-onnx（同为 CC-BY-NC-4.0）。\n" +
        "商用替代（如需商业授权）：OPUS-MT（CC-BY）或 Hunyuan-MT。本工具不内置这些模型。";

    /// <inheritdoc />
    public string BuildInstallGuide(NllbModelCheck check)
    {
        var dir = check.ModelDirectory;
        var sb = new StringBuilder(1024);

        sb.AppendLine("【NLLB 离线翻译模型 —— 安装引导】");
        sb.AppendLine();

        if (check.IsReady)
        {
            sb.AppendLine($"状态：已就绪（{check.PrecisionLabel}，{NllbModelCheck.FormatBytes(check.TotalBytes)}）");
            if (check.Problems.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("提示（不影响使用）：");
                foreach (var p in check.Problems)
                {
                    sb.AppendLine("  - " + p);
                }
            }

            return sb.ToString();
        }

        sb.AppendLine($"目标目录：{dir}");
        sb.AppendLine();

        // 旧位置有可用模型时优先提示迁移，而不是让用户重下 1.08 GiB
        var legacy = InspectLegacy();
        if (legacy is not null)
        {
            sb.AppendLine("⚠ 检测到旧位置的模型（早期版本放在 Roaming，大文件会被域环境同步，已改到 Local）：");
            sb.AppendLine($"  {legacy.ModelDirectory}");
            sb.AppendLine($"  可直接迁移过来（同盘移动是秒级元数据操作，不会重写 1.08 GiB）：");
            sb.AppendLine("  设置页点「从旧位置迁移」，或手动把整个目录移动到上面「目标目录」。");
            sb.AppendLine();
        }

        sb.AppendLine("把以下文件放进该目录（缺一不可）：");
        sb.AppendLine("  1) encoder_model_int8.onnx    415,378,190 字节");
        sb.AppendLine("  2) decoder_model_int8.onnx    728,792,767 字节");
        sb.AppendLine("  3) tokenizer.json              17,331,224 字节");
        sb.AppendLine();
        sb.AppendLine($"合计约 {NllbModelCheck.FormatBytes(1_144_170_957L + 17_331_224L)}（int8 动态量化档）。");
        sb.AppendLine("  fp32 全精度档约 2.4 GB，也可用，但首次加载更慢、内存占用更高。");
        sb.AppendLine();
        sb.AppendLine("获取方式（Hugging Face 官方仓库，需自备网络）：");
        sb.AppendLine("  https://huggingface.co/tfchan-lab/nllb-200-distilled-600M-onnx");
        sb.AppendLine("  文件名对应：encoder_model_int8.onnx / decoder_model_int8.onnx / tokenizer.json");
        sb.AppendLine();

        if (check.Problems.Count > 0)
        {
            sb.AppendLine("当前检测到的问题：");
            foreach (var p in check.Problems)
            {
                var level = p.IsBlocking ? "必须修复" : "提示";
                sb.AppendLine($"  [{level}] {p}");
            }

            sb.AppendLine();
        }

        sb.AppendLine("许可（务必阅读）：");
        foreach (var line in LicenseNotice.Split('\n'))
        {
            sb.AppendLine("  " + line);
        }

        return sb.ToString();
    }
}
