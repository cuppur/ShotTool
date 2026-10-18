using System.Text.Json;
using System.Text.Json.Serialization;
using ShotTool.Core;

namespace ShotTool.Settings;

/// <summary>
/// settings.json 的 <see cref="ISettingsStore"/> 实现。
///
/// <para>
/// 落盘位置：<c>%LOCALAPPDATA%\ShotTool\settings.json</c>（契约注释约定）。
/// 目录可被覆盖（便携模式/测试用构造函数参数）。
/// </para>
/// <para>
/// 文件结构（人可读、可手改）：
/// <code>
/// {
///   "SchemaVersion": 1,
///   "App": { ...ShotToolOptions 全部字段... },
///   "Sections": {
///     "editor": { ... dev-editor 自己的 POCO ... },
///     "ai":     { "TranslationBackend": "off", "NllbModelDirectory": "%LOCALAPPDATA%\\...", "OcrBackend": "auto" }
///   }
/// }
/// </code>
/// </para>
/// <para>
/// <b>容错原则</b>：配置文件损坏 / 未知枚举 / 未知字段一律不抛到调用方。
/// 整份文件无法解析时备份为 <c>settings.json.bad-yyyyMMdd-HHmmss</c> 并用默认值重建。
/// </para>
/// </summary>
public sealed class JsonSettingsStore : ISettingsStore, IDisposable
{
    /// <summary>当前文件结构版本。</summary>
    public const int CurrentSchemaVersion = 1;

    /// <summary>设置目录（<c>%LOCALAPPDATA%\ShotTool</c>）。</summary>
    public static string DefaultDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ShotTool");

    /// <summary>默认配置文件完整路径。</summary>
    public static string DefaultFilePath => Path.Combine(DefaultDirectory, "settings.json");

    /// <summary>
    /// 全项目共享的 JSON 选项。WriteIndented 便于用户手改；
    /// 枚举以字符串保存（`"Png"` 而不是 `0`），读时大小写不敏感。
    /// </summary>
    public static readonly JsonSerializerOptions SharedJsonOptions = CreateJsonOptions();

    private readonly object _gate = new();
    private readonly IAppLog _log;
    private readonly string _path;
    private readonly Dictionary<string, JsonElement> _sections = new(StringComparer.OrdinalIgnoreCase);

    private ShotToolOptions _app = new();
    private FileSystemWatcher? _watcher;
    private System.Threading.Timer? _reloadTimer;
    private DateTime _suppressWatcherUntilUtc = DateTime.MinValue;
    private bool _disposed;

    /// <summary>创建并立即加载。</summary>
    /// <param name="path">配置文件路径；null = <see cref="DefaultFilePath"/>。</param>
    /// <param name="log">可选日志（损坏回退会写 warn）。</param>
    /// <param name="watch">是否监听外部改写（热重载）。默认 true。</param>
    public JsonSettingsStore(string? path = null, IAppLog? log = null, bool watch = true)
    {
        _path = Path.GetFullPath(path ?? DefaultFilePath);
        _log = log ?? NullAppLog.Instance;

        LoadCore();
        EnsureAiSection();
        SaveCore(); // 首次运行 / 升级后立即落盘，让用户看得到默认值

        if (watch)
        {
            TryStartWatcher();
        }
    }

    /// <inheritdoc />
    public string FilePath => _path;

    /// <inheritdoc />
    public ShotToolOptions App
    {
        get
        {
            lock (_gate)
            {
                return _app;
            }
        }
    }

    /// <inheritdoc />
    public event EventHandler? Changed;

    /// <inheritdoc />
    public string? GetSection(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        lock (_gate)
        {
            return _sections.TryGetValue(name, out JsonElement element)
                ? element.GetRawText()
                : null;
        }
    }

    /// <inheritdoc />
    public void SetSection(string name, string? json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        lock (_gate)
        {
            if (json is null)
            {
                _sections.Remove(name);
                SaveCore();
                return;
            }

            // 校验是合法 JSON 才写入；非法输入直接忽略（并记录），不让坏数据进文件。
            try
            {
                using JsonDocument document = JsonDocument.Parse(json, new JsonDocumentOptions
                {
                    AllowTrailingCommas = true,
                    CommentHandling = JsonCommentHandling.Skip,
                });
                _sections[name] = document.RootElement.Clone();
            }
            catch (JsonException ex)
            {
                _log.Warn($"设置段 \"{name}\" 不是合法 JSON，已忽略：{ex.Message}");
                return;
            }

            SaveCore();
        }
    }

    /// <summary>立即落盘（节流合并后仍有未写入内容时调用）。</summary>
    public void Save()
    {
        lock (_gate)
        {
            SaveCore();
        }
    }

    /// <summary>从磁盘重新加载（外部改写后手动刷新用）。解析失败时保留内存值。</summary>
    public void Reload()
    {
        lock (_gate)
        {
            if (!TryReadFile(out SettingsDocument? document))
            {
                return;
            }

            ApplyDocument(document!);
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            SaveCore();
        }

        _reloadTimer?.Dispose();
        _reloadTimer = null;

        if (_watcher is not null)
        {
            _watcher.EnableRaisingEvents = false;
            _watcher.Dispose();
            _watcher = null;
        }
    }

    // ------------------------------------------------------------------
    // 读写核心
    // ------------------------------------------------------------------

    private void LoadCore()
    {
        if (!File.Exists(_path))
        {
            _log.Info($"配置文件不存在，使用默认值：{_path}");
            _app = new ShotToolOptions();
            _sections.Clear();
            return;
        }

        if (TryReadFile(out SettingsDocument? document) && document is not null)
        {
            ApplyDocument(document!);
            return;
        }

        // 损坏：备份 + 重建默认，不打断启动。
        string backup = $"{_path}.bad-{DateTime.Now:yyyyMMdd-HHmmss}";
        try
        {
            File.Copy(_path, backup, overwrite: true);
            _log.Warn($"配置文件无法解析，已备份为 {backup}，将用默认值重建。");
        }
        catch (Exception ex)
        {
            _log.Warn($"配置文件无法解析，且备份失败：{ex.Message}");
        }

        _app = new ShotToolOptions();
        _sections.Clear();
    }

    private bool TryReadFile(out SettingsDocument? document)
    {
        document = null;
        try
        {
            string text = File.ReadAllText(_path);
            if (string.IsNullOrWhiteSpace(text))
            {
                return false;
            }

            document = JsonSerializer.Deserialize<SettingsDocument>(text, SharedJsonOptions);
            return document is not null;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            _log.Warn($"读取配置失败：{ex.Message}");
            return false;
        }
    }

    private void ApplyDocument(SettingsDocument document)
    {
        _app = document.App ?? new ShotToolOptions();
        _sections.Clear();

        if (document.Sections is not null)
        {
            foreach ((string key, JsonElement value) in document.Sections)
            {
                if (string.IsNullOrWhiteSpace(key))
                {
                    continue;
                }

                _sections[key] = value.Clone();
            }
        }
    }

    private void EnsureAiSection()
    {
        if (_sections.ContainsKey(AiOptions.SectionName))
        {
            return;
        }

        // 首次运行就把三个键写进文件，用户打开 settings.json 能直接看到/修改。
        _sections[AiOptions.SectionName] = ToElement(new AiOptions().ToJson());
    }

    private void SaveCore()
    {
        try
        {
            string? directory = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var sections = new Dictionary<string, JsonElement>(_sections.Count, StringComparer.Ordinal);
            foreach ((string key, JsonElement value) in _sections)
            {
                sections[key] = value;
            }

            var document = new SettingsDocument
            {
                SchemaVersion = CurrentSchemaVersion,
                App = _app,
                Sections = sections,
            };

            string json = JsonSerializer.Serialize(document, SharedJsonOptions);

            // 原子写：临时文件 + 覆盖式 Move（同卷内近似原子），避免半截文件。
            string temp = _path + ".tmp";
            File.WriteAllText(temp, json);
            File.Move(temp, _path, overwrite: true);

            _suppressWatcherUntilUtc = DateTime.UtcNow.AddMilliseconds(800);
        }
        catch (Exception ex)
        {
            _log.Error($"保存配置失败：{ex.Message}", ex);
        }
    }

    private static JsonElement ToElement(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    // ------------------------------------------------------------------
    // 外部改写热重载（防抖；自写不触发）
    // ------------------------------------------------------------------

    private void TryStartWatcher()
    {
        try
        {
            string? directory = Path.GetDirectoryName(_path);
            if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
            {
                return;
            }

            _watcher = new FileSystemWatcher(directory, Path.GetFileName(_path))
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
                EnableRaisingEvents = true,
            };

            _watcher.Changed += OnFileTouched;
            _watcher.Created += OnFileTouched;
            _watcher.Renamed += OnFileTouched;
            _watcher.Error += (_, args) => _log.Warn($"配置监听出错：{args.GetException()?.Message}");
        }
        catch (Exception ex)
        {
            _log.Warn($"无法监听配置文件变化（热重载关闭）：{ex.Message}");
        }
    }

    private void OnFileTouched(object? sender, FileSystemEventArgs e)
    {
        lock (_gate)
        {
            if (_disposed || DateTime.UtcNow < _suppressWatcherUntilUtc)
            {
                return;
            }

            // 防抖：多个事件合并成一次重载。
            _reloadTimer ??= new System.Threading.Timer(_ => ReloadSafely(), null, Timeout.Infinite, Timeout.Infinite);
            _reloadTimer.Change(300, Timeout.Infinite);
        }
    }

    private void ReloadSafely()
    {
        lock (_gate)
        {
            if (_disposed || DateTime.UtcNow < _suppressWatcherUntilUtc)
            {
                return;
            }

            if (!TryReadFile(out SettingsDocument? document) || document is null)
            {
                return; // 外部半截写入：保留内存值，等下一次事件
            }

            ApplyDocument(document);
            EnsureAiSection();
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    // ------------------------------------------------------------------
    // 文档模型与 JSON 选项
    // ------------------------------------------------------------------

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true,
            AllowTrailingCommas = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };

        options.Converters.Add(new HotkeyGestureJsonConverter());
        // CamelCase：文件里写 "off" / "windows" / "png"，与 README 和手改习惯一致
        // （JsonStringEnumConverter 的读取本身不区分大小写，手写 "Off" 也能读回）。
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }

    /// <summary>settings.json 顶层结构。</summary>
    private sealed class SettingsDocument
    {
        public int SchemaVersion { get; set; } = CurrentSchemaVersion;

        public ShotToolOptions? App { get; set; } = new();

        /// <summary>各层自己的段落，原样保存，不解释字段含义。</summary>
        public Dictionary<string, JsonElement>? Sections { get; set; }
    }

    /// <summary>
    /// 热键 <c>"Alt+A"</c> ⇄ <see cref="HotkeyGesture"/>。
    /// 空字符串/缺失 = 未设置；无法解析 = 未设置（不抛）。
    /// </summary>
    private sealed class HotkeyGestureJsonConverter : JsonConverter<HotkeyGesture>
    {
        public override HotkeyGesture Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.Null)
            {
                return HotkeyGesture.None;
            }

            if (reader.TokenType != JsonTokenType.String)
            {
                reader.Skip();
                return HotkeyGesture.None;
            }

            string? text = reader.GetString();
            return HotkeyGesture.TryParse(text, out HotkeyGesture gesture) ? gesture : HotkeyGesture.None;
        }

        public override void Write(Utf8JsonWriter writer, HotkeyGesture value, JsonSerializerOptions options)
        {
            writer.WriteStringValue(value.IsEmpty ? string.Empty : value.ToString());
        }
    }

    /// <summary>空日志实现，避免 <see cref="IAppLog"/> 依赖顺序问题。</summary>
    private sealed class NullAppLog : IAppLog
    {
        public static readonly NullAppLog Instance = new();

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
