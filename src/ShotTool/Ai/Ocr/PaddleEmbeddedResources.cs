using System.Reflection;

namespace ShotTool.Ai.Ocr;

// ============================================================================
//  PaddleEmbeddedResources.cs —— PaddleOCR 内嵌模型资源的访问层
//
//  发布形态：det/rec/keys 作为**嵌入资源**打进 exe（约 +14.91 MB），
//  与 NLLB 的「外部目录 + 三级查找」是两套不同来源，不要混用同一套 locator：
//    * PaddleOCR = 内嵌资源（随包安装，用户不可单独删除）
//    * NLLB      = 外部目录（随包放 dist\models\ 或用户自下，见 NllbModelLocator）
//
//  ★ 资源名规则（architect 实测确认，普通 build 与 PublishSingleFile 都通过）：
//      ShotTool.Assets.Models.PaddleOcr.det.onnx
//      ShotTool.Assets.Models.PaddleOcr.rec.onnx
//      ShotTool.Assets.Models.PaddleOcr.cls.onnx
//      ShotTool.Assets.Models.PaddleOcr.keys.txt
//
//  ★ 硬约束（写崩过一次就会记住）：`Assets/Models/paddle-ocr/` 下必须
//    **扁平放置、文件名唯一**。csproj 的 LogicalName 用 %(Filename)%(Extension)，
//    子目录会被扁平化：paddle-ocr/det/model.onnx → ...PaddleOcr.model.onnx
//    （不是 ...PaddleOcr.det.model.onnx）。同名文件只有一个生效且**构建不报错**，
//    只在运行时表现为「少一个资源」—— 静默失败，很阴。
//
//  ★ 目录名必须带连字符 `paddle-ocr`。写成 `paddle_ocr` 会让内嵌规则失配，
//    文件被 Assets\Models\** 的 Never 挡住 → 资源不存在 → IsAvailable=false。
// ============================================================================

/// <summary>PaddleOCR 内嵌资源的只读访问。所有方法只查资源名/读流，不加载 ONNX。</summary>
internal static class PaddleEmbeddedResources
{
    private const string ResourcePrefix = "ShotTool.Assets.Models.PaddleOcr.";

    /// <summary>det 模型文件名（相对 paddle-ocr 目录）。</summary>
    public const string DetFileName = "det.onnx";

    /// <summary>rec 模型文件名。</summary>
    public const string RecFileName = "rec.onnx";

    /// <summary>CTC 字典文件名（6623 行，rec 解码必需）。</summary>
    public const string KeysFileName = "keys.txt";

    /// <summary>方向分类模型（可选；当前产品不做 180° 矫正，缺省不影响可用性）。</summary>
    public const string ClsFileName = "cls.onnx";

    private static readonly Lazy<Assembly> Asm = new(() => typeof(PaddleEmbeddedResources).Assembly);

    /// <summary>
    /// 打开一个内嵌资源流。调用方负责 Dispose。
    /// 优先精确名；失败再退化为「资源名包含 PaddleOcr 且以 fileName 结尾」——
    /// 后者用于容忍将来重构命名空间导致的资源名前缀变化。
    /// </summary>
    public static Stream? Open(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return null;
        }

        var assembly = Asm.Value;

        // 1) 精确名（正常路径，零猜测）
        var exact = ResourcePrefix + fileName;
        var stream = assembly.GetManifestResourceStream(exact);
        if (stream is not null)
        {
            return stream;
        }

        // 2) 退化：按后缀匹配，容忍命名空间变化
        string? fallbackName = null;
        try
        {
            fallbackName = assembly.GetManifestResourceNames().FirstOrDefault(n =>
                n.Contains("PaddleOcr", StringComparison.OrdinalIgnoreCase) &&
                n.EndsWith(fileName, StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            // GetManifestResourceNames 在动态程序集上可能抛；按缺失处理
        }

        return fallbackName is null ? null : assembly.GetManifestResourceStream(fallbackName);
    }

    /// <summary>
    /// 把内嵌资源读成字节数组（供 <c>InferenceSession(byte[])</c> 使用）。
    /// 15 MB 一次性读入，比先落临时文件再加载更快且不污染磁盘。
    /// </summary>
    public static byte[]? ReadAllBytes(string fileName)
    {
        using var stream = Open(fileName);
        if (stream is null)
        {
            return null;
        }

        using var ms = new MemoryStream(checked((int)stream.Length));
        stream.CopyTo(ms);
        return ms.ToArray();
    }

    /// <summary>
    /// 就绪探测：**不加载 ONNX**，只查资源在不在。
    /// 按 ADR 约定，`ITextRecognizer.IsAvailable` 必须是纯探测，不能触发 15 MB 加载。
    /// </summary>
    public static bool IsAvailable
    {
        get
        {
            try
            {
                using var det = Open(DetFileName);
                using var rec = Open(RecFileName);
                using var keys = Open(KeysFileName);
                return det is not null && rec is not null && keys is not null;
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>
    /// 缺失资源的可操作诊断（哪些缺、应该叫什么名字、放哪个目录）。
    /// 用于「内嵌资源损坏/构建规则失配」时给开发与用户都看得懂的提示。
    /// </summary>
    public static string DescribeMissing()
    {
        var missing = new List<string>(3);
        foreach (var name in new[] { DetFileName, RecFileName, KeysFileName })
        {
            using var s = Open(name);
            if (s is null)
            {
                missing.Add(name);
            }
        }

        if (missing.Count == 0)
        {
            return string.Empty;
        }

        return "PaddleOCR 内嵌资源缺失：" + string.Join("、", missing) + "。\n" +
               "这是随包资源（不是用户可下载的模型），缺失说明构建/打包规则失配。\n" +
               "排查要点：\n" +
               "  1) 文件必须位于 Assets\\Models\\paddle-ocr\\（目录名带连字符）；\n" +
               "  2) 该目录下文件必须扁平放置且文件名唯一（子目录会被扁平化，同名会被静默覆盖）；\n" +
               "  3) 期望的资源名形如 ShotTool.Assets.Models.PaddleOcr.det.onnx。\n" +
               "如为安装包损坏，请重新安装本程序。";
    }

    /// <summary>列出所有与 PaddleOcr 相关的资源名（诊断用）。</summary>
    public static IReadOnlyList<string> ListResourceNames()
    {
        try
        {
            return Asm.Value.GetManifestResourceNames()
                .Where(n => n.Contains("PaddleOcr", StringComparison.OrdinalIgnoreCase))
                .OrderBy(n => n, StringComparer.Ordinal)
                .ToArray();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }
}
