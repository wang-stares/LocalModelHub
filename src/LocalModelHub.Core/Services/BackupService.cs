using System.Text.Json;
using System.Text.Json.Nodes;
using LocalModelHub.Core.Models;

namespace LocalModelHub.Core.Services;

/// <summary>一次导入的结果统计，用于向用户提示导入了哪些数据。</summary>
public sealed record BackupImportResult(
    bool SettingsImported,
    int CustomModels,
    int Memories,
    int Sessions,
    int KnowledgeDocuments,
    bool MachineBoundReset = false,
    int ResetFields = 0);

/// <summary>
/// 配置与会话备份：把 settings.json、custom-models.json、memory.json、sessions.json、knowledge.json
/// 打包成单个 JSON 文件，便于迁移或回滚。
/// </summary>
public sealed class BackupService
{
    public const string FormatName = "LocalModelHub.Backup";
    public const int FormatVersion = 1;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    private readonly string _root;

    public BackupService(string? rootDirectory = null)
    {
        _root = rootDirectory ?? AppPaths.RootDirectory;
        SettingsFile = Path.Combine(_root, "settings.json");
        CustomModelsFile = Path.Combine(_root, "custom-models.json");
        MemoryFile = Path.Combine(_root, "memory.json");
        SessionsFile = Path.Combine(_root, "sessions.json");
        KnowledgeFile = Path.Combine(_root, "knowledge.json");
    }

    public string SettingsFile { get; }
    public string CustomModelsFile { get; }
    public string MemoryFile { get; }
    public string SessionsFile { get; }
    public string KnowledgeFile { get; }

    /// <summary>导出为单个 JSON 备份文件；损坏的单个分节会被跳过，不影响其余数据。</summary>
    public void Export(string backupPath, string appVersion = "", string machineFingerprint = "")
    {
        if (string.IsNullOrWhiteSpace(backupPath))
        {
            throw new ArgumentException("备份路径不能为空", nameof(backupPath));
        }

        var bundle = new JsonObject
        {
            ["format"] = FormatName,
            ["version"] = FormatVersion,
            ["schemaVersion"] = FormatVersion,
            ["machineFingerprint"] = machineFingerprint,
            ["exportedAt"] = DateTimeOffset.Now.ToString("O"),
            ["appVersion"] = appVersion,
            ["settings"] = ReadNode(SettingsFile),
            ["customModels"] = ReadNode(CustomModelsFile),
            ["memory"] = ReadNode(MemoryFile),
            ["sessions"] = ReadNode(SessionsFile),
            ["knowledge"] = ReadNode(KnowledgeFile)
        };

        var fullPath = Path.GetFullPath(backupPath);
        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        var temp = fullPath + ".tmp";
        File.WriteAllText(temp, bundle.ToJsonString(JsonOptions));
        File.Move(temp, fullPath, overwrite: true);
    }

    /// <summary>
    /// 导入备份：先校验所有分节，再统一写回四个文件，任何一步失败都不会留下半份数据。
    /// 导入后需重启应用才会让设置与内存数据生效。
    /// 备份来自另一台设备（指纹不一致）时，硬件绑定项重置为默认，而不是原样套用。
    /// </summary>
    public BackupImportResult Import(string backupPath, string currentFingerprint = "")
    {
        if (string.IsNullOrWhiteSpace(backupPath))
        {
            throw new ArgumentException("备份路径不能为空", nameof(backupPath));
        }

        if (!File.Exists(backupPath))
        {
            throw new FileNotFoundException("备份文件不存在", backupPath);
        }

        var bundle = ParseBundle(backupPath);
        var settingsNode = Section(bundle, "settings");
        var customModelsNode = Section(bundle, "customModels");
        var memoryNode = Section(bundle, "memory");
        var sessionsNode = Section(bundle, "sessions");
        var knowledgeNode = Section(bundle, "knowledge");

        if (settingsNode is null && customModelsNode is null && memoryNode is null &&
            sessionsNode is null && knowledgeNode is null)
        {
            throw new InvalidDataException("备份文件中没有可导入的数据。");
        }

        var writes = new List<(string Target, string Content)>();
        var settingsImported = false;
        var customModels = 0;
        var memories = 0;
        var sessions = 0;
        var knowledgeDocuments = 0;
        var machineBoundReset = false;
        var resetFields = 0;

        if (settingsNode is not null)
        {
            var settings = Deserialize<AppSettings>(settingsNode, "设置");
            var exportedFingerprint = TryGetString(bundle, "machineFingerprint", out var fingerprint)
                ? fingerprint ?? ""
                : "";
            if (exportedFingerprint.Length > 0 &&
                currentFingerprint.Length > 0 &&
                !exportedFingerprint.Equals(currentFingerprint, StringComparison.Ordinal))
            {
                resetFields = ResetMachineBound(settings);
                machineBoundReset = resetFields > 0;
            }

            writes.Add((SettingsFile, JsonSerializer.Serialize(settings, JsonOptions)));
            settingsImported = true;
        }

        if (customModelsNode is not null)
        {
            var models = Deserialize<List<ModelEntry>>(customModelsNode, "自定义模型")
                .Where(model => !string.IsNullOrWhiteSpace(model.Id) && model.Quants is { Length: > 0 })
                .ToList();
            writes.Add((CustomModelsFile, JsonSerializer.Serialize(models, JsonOptions)));
            customModels = models.Count;
        }

        if (memoryNode is not null)
        {
            var entries = Deserialize<List<MemoryEntry>>(memoryNode, "记忆")
                .Where(entry => !string.IsNullOrWhiteSpace(entry.Id) && !string.IsNullOrWhiteSpace(entry.Text))
                .Take(MemoryService.MaxEntries)
                .ToList();
            writes.Add((MemoryFile, JsonSerializer.Serialize(entries, JsonOptions)));
            memories = entries.Count;
        }

        if (sessionsNode is not null)
        {
            var loaded = Deserialize<List<ChatSession>>(sessionsNode, "会话")
                .Where(session => !string.IsNullOrWhiteSpace(session.Id))
                .Take(ChatSessionStore.MaxSessions)
                .ToList();
            writes.Add((SessionsFile, JsonSerializer.Serialize(loaded, JsonOptions)));
            sessions = loaded.Count;
        }

        if (knowledgeNode is not null)
        {
            var documents = Deserialize<List<KnowledgeDocument>>(knowledgeNode, "知识库")
                .Where(document => !string.IsNullOrWhiteSpace(document.Id) &&
                                   !string.IsNullOrWhiteSpace(document.Name))
                .Take(KnowledgeService.MaxDocuments)
                .ToList();
            writes.Add((KnowledgeFile, JsonSerializer.Serialize(documents, JsonOptions)));
            knowledgeDocuments = documents.Count;
        }

        WriteAll(writes);

        return new BackupImportResult(
            settingsImported, customModels, memories, sessions, knowledgeDocuments,
            machineBoundReset, resetFields);
    }

    /// <summary>
    /// 硬件绑定项重置为默认：这些值只对导出时那台机器有意义（端口、线程、显存规划、路径）。
    /// 返回被重置的字段数，便于提示「已按当前设备重新评估」。
    /// </summary>
    private static int ResetMachineBound(AppSettings settings)
    {
        var defaults = new AppSettings();
        var reset = 0;

        void Reset<T>(T current, T fallback, Action<T> apply)
        {
            if (EqualityComparer<T>.Default.Equals(current, fallback)) return;
            apply(fallback);
            reset++;
        }

        Reset(settings.ContextSize, defaults.ContextSize, value => settings.ContextSize = value);
        Reset(settings.Threads, defaults.Threads, value => settings.Threads = value);
        Reset(settings.ServerPort, defaults.ServerPort, value => settings.ServerPort = value);
        Reset(settings.KvCacheType, defaults.KvCacheType, value => settings.KvCacheType = value);
        Reset(settings.FlashAttention, defaults.FlashAttention, value => settings.FlashAttention = value);
        Reset(settings.CpuMoeLayers, defaults.CpuMoeLayers, value => settings.CpuMoeLayers = value);
        Reset(settings.ParallelSlots, defaults.ParallelSlots, value => settings.ParallelSlots = value);
        Reset(settings.LoadMode, defaults.LoadMode, value => settings.LoadMode = value);
        Reset(settings.RuntimePath, defaults.RuntimePath, value => settings.RuntimePath = value);
        Reset(settings.RuntimeVariant, defaults.RuntimeVariant, value => settings.RuntimeVariant = value);
        Reset(settings.ModelsDirectory, defaults.ModelsDirectory, value => settings.ModelsDirectory = value);
        Reset(settings.WorkspaceDirectory, defaults.WorkspaceDirectory, value => settings.WorkspaceDirectory = value);
        Reset(settings.LastModelPath, defaults.LastModelPath, value => settings.LastModelPath = value);
        return reset;
    }

    private static JsonObject ParseBundle(string backupPath)
    {
        JsonNode? node;
        try
        {
            node = JsonNode.Parse(File.ReadAllText(backupPath));
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("备份文件不是有效的 JSON。", ex);
        }

        if (node is not JsonObject bundle)
        {
            throw new InvalidDataException("备份文件不是有效的 JSON 对象。");
        }

        if (!TryGetString(bundle, "format", out var format) || format != FormatName)
        {
            throw new InvalidDataException("这不是 Local Model Hub 的备份文件。");
        }

        var version = ReadVersion(bundle);
        if (version > FormatVersion)
        {
            throw new InvalidDataException($"备份文件来自更新的版本（格式 {version}），当前仅支持 {FormatVersion}。");
        }

        return bundle;
    }

    private static int ReadVersion(JsonObject bundle)
    {
        if (bundle["version"] is not JsonValue value || !value.TryGetValue<int>(out var version) || version <= 0)
        {
            throw new InvalidDataException("备份文件的格式版本无效。");
        }

        return version;
    }

    private static bool TryGetString(JsonObject bundle, string name, out string? value)
    {
        if (bundle[name] is JsonValue node && node.TryGetValue<string>(out var text))
        {
            value = text;
            return true;
        }

        value = null;
        return false;
    }

    private static JsonNode? Section(JsonObject bundle, string name)
    {
        var node = bundle[name];
        return node is null || node.GetValueKind() == JsonValueKind.Null ? null : node;
    }

    private static T Deserialize<T>(JsonNode node, string label) where T : class
    {
        try
        {
            return node.Deserialize<T>(JsonOptions)
                   ?? throw new InvalidDataException($"备份中的{label}为空。");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"备份中的{label}格式不正确。", ex);
        }
    }

    private static JsonNode? ReadNode(string path)
    {
        try
        {
            return File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path)) : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static void WriteAll(List<(string Target, string Content)> writes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(writes[0].Target)!);

        var staged = new List<(string Temp, string Target)>();
        try
        {
            foreach (var (target, content) in writes)
            {
                var temp = target + ".import.tmp";
                File.WriteAllText(temp, content);
                staged.Add((temp, target));
            }

            foreach (var (temp, target) in staged)
            {
                File.Move(temp, target, overwrite: true);
            }
        }
        finally
        {
            foreach (var (temp, _) in staged)
            {
                if (File.Exists(temp)) File.Delete(temp);
            }
        }
    }
}
