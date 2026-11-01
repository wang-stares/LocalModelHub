using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LocalModelHub.Core.Models;
using LocalModelHub.Core.Services.Abstractions;

namespace LocalModelHub.Core.Services;

/// <summary>
/// 两层长期记忆的统一入口：
/// 全局记忆（用户偏好、使用习惯）落在 memory/global.json，任何场景可读写；
/// 项目记忆（当前工作区的技术栈、目录约定、任务进度）落在 memory/projects/&lt;工作区指纹&gt;.json，
/// 只有当前工作区会被加载，从存储层杜绝跨项目读取。切换工作文件夹时只替换项目层，全局层保持常驻。
/// </summary>
public sealed class MemoryManager : IMemoryStore
{
    /// <summary>全局层最多保留的条目数。</summary>
    public const int MaxGlobalEntries = 200;
    /// <summary>单个项目最多保留的条目数。</summary>
    public const int MaxProjectEntries = 120;
    /// <summary>项目记忆超过该天数未更新会自动过期；置顶条目不受影响。</summary>
    public const int ProjectTtlDays = 90;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    private static readonly StringComparison PathComparison =
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private readonly string _directory;
    private readonly string _legacyFile;
    private readonly MemoryService _global;
    private MemoryService? _project;
    private string _workspace = "";

    /// <param name="directory">记忆根目录；为空时使用应用数据目录下的 memory/。</param>
    public MemoryManager(string? directory = null)
    {
        _directory = directory is null ? AppPaths.MemoryDirectory : Path.GetFullPath(directory);
        _legacyFile = directory is null ? AppPaths.MemoryFile : Path.Combine(_directory, "memory.json");
        Directory.CreateDirectory(_directory);
        Directory.CreateDirectory(Path.Combine(_directory, "projects"));
        _global = new MemoryService(
            Path.Combine(_directory, "global.json"),
            MemoryTier.Global,
            scope: null,
            maxEntries: MaxGlobalEntries,
            ttlDays: null);
    }

    /// <summary>全局记忆文件路径（兼容 IMemoryStore）。</summary>
    public string FilePath => _global.FilePath;

    /// <summary>当前项目记忆所属的工作文件夹绝对路径；未设置时为空。</summary>
    public string Workspace => _workspace;

    public MemoryService GlobalStore => _global;

    /// <summary>当前工作区的项目记忆存储；没有工作文件夹时为空。</summary>
    public MemoryService? ProjectStore => _project;

    public IReadOnlyList<MemoryEntry> GlobalEntries => _global.Entries;

    public IReadOnlyList<MemoryEntry> ProjectEntries =>
        _project is null ? Array.Empty<MemoryEntry>() : _project.Entries;

    /// <summary>当前可见的全部记忆（全局 + 当前项目），用于界面展示。</summary>
    public IReadOnlyList<MemoryEntry> Entries
    {
        get
        {
            if (_project is null) return _global.Entries;
            var all = new List<MemoryEntry>(_global.Entries.Count + _project.Entries.Count);
            all.AddRange(_global.Entries);
            all.AddRange(_project.Entries);
            return all;
        }
    }

    /// <summary>加载全局记忆，并把旧版 memory.json 迁移到两层目录结构。</summary>
    public IReadOnlyList<MemoryEntry> Load()
    {
        _global.Load();
        MigrateLegacyFile();
        if (_workspace.Length > 0) UseWorkspace(_workspace);
        return Entries;
    }

    /// <summary>
    /// 切换当前工作区：卸载上一个项目的记忆，加载新项目的记忆并清理过期条目。
    /// 全局记忆不参与切换，始终可访问。</summary>
    public void UseWorkspace(string? root)
    {
        var normalized = NormalizeRoot(root);
        if (_project is not null && SameWorkspace(normalized, _workspace)) return;

        _workspace = normalized;
        _project = null;
        if (normalized.Length == 0) return;

        var store = new MemoryService(
            ProjectFilePath(normalized),
            MemoryTier.Project,
            normalized,
            MaxProjectEntries,
            ProjectTtlDays);
        store.Load();
        _project = store;
    }

    /// <summary>按层级写入一条记忆；项目层要求已设置工作文件夹且不能指定其它工作区。</summary>
    public MemoryWriteResult Write(
        MemoryTier tier,
        string? text,
        string source = "manual",
        int? ttlDays = null) =>
        tier == MemoryTier.Project
            ? Upsert(text, source, MemoryEntry.KindProject, _workspace, ttlDays)
            : Upsert(text, source, MemoryEntry.KindUser, "", ttlDays);

    public MemoryEntry Add(string? text, string source = "manual") =>
        Upsert(text, source).Entry;

    public MemoryWriteResult Upsert(
        string? text,
        string source = "manual",
        string kind = MemoryEntry.KindUser,
        string scope = "",
        int? ttlDays = null)
    {
        if (kind == MemoryEntry.KindProject)
        {
            if (_project is null)
            {
                throw new InvalidOperationException("尚未设置工作文件夹，无法写入项目记忆");
            }

            if (!string.IsNullOrWhiteSpace(scope) && !SameWorkspace(scope, _workspace))
            {
                throw new InvalidOperationException("项目记忆只能写入当前工作文件夹");
            }

            return _project.Upsert(text, source, MemoryEntry.KindProject, _workspace, ttlDays);
        }

        // 用户偏好属于全局层：无论调用方传入什么 scope，都不会落到项目文件。
        return _global.Upsert(text, source, MemoryEntry.KindUser, "", ttlDays);
    }

    public MemoryEntry? Get(string? entryId)
    {
        if (string.IsNullOrWhiteSpace(entryId)) return null;
        return _global.Get(entryId) ?? _project?.Get(entryId);
    }

    public bool Contains(string? text) =>
        _global.Contains(text) || (_project?.Contains(text) ?? false);

    public bool TogglePin(string entryId)
    {
        var entry = Get(entryId);
        if (entry is null) return false;
        return entry.IsProject
            ? _project?.TogglePin(entryId) ?? false
            : _global.TogglePin(entryId);
    }

    public bool UpdateText(string entryId, string? text)
    {
        var entry = Get(entryId);
        if (entry is null) return false;
        return entry.IsProject
            ? _project?.UpdateText(entryId, text) ?? false
            : _global.UpdateText(entryId, text);
    }

    public bool Remove(string entryId)
    {
        var entry = Get(entryId);
        if (entry is null) return false;
        return entry.IsProject
            ? _project?.Remove(entryId) ?? false
            : _global.Remove(entryId);
    }

    /// <summary>清空两层记忆（全局 + 当前项目）；其它项目的文件不受影响。</summary>
    public void Clear()
    {
        _global.Clear();
        _project?.Clear();
    }

    /// <summary>只清空指定层级；项目层没有工作区时不做任何事。</summary>
    public void ClearTier(MemoryTier tier)
    {
        if (tier == MemoryTier.Project) _project?.Clear();
        else _global.Clear();
    }

    /// <param name="scope">null = 全局 + 当前项目；空串或其它工作区 = 只读全局，跨项目不可见。</param>
    public IReadOnlyList<MemoryEntry> Search(
        string? query,
        int limit = 5,
        DateTime? now = null,
        string? scope = null)
    {
        if (limit <= 0) return [];

        var current = now ?? DateTime.Now;
        var candidates = VisibleStores(scope)
            .SelectMany(store => store.Entries)
            .Where(entry => !entry.IsExpired(current));
        if (string.IsNullOrWhiteSpace(query))
        {
            return candidates
                .OrderByDescending(entry => entry.Pinned)
                .ThenByDescending(entry => entry.UpdatedAt)
                .Take(limit)
                .ToArray();
        }

        return candidates
            .Select(entry => (Entry: entry, Score: MemoryService.Relevance(entry, query, current)))
            .Where(pair => pair.Score > 0)
            .OrderByDescending(pair => pair.Score)
            .ThenByDescending(pair => pair.Entry.UpdatedAt)
            .Take(limit)
            .Select(pair => pair.Entry)
            .ToArray();
    }

    public IReadOnlyList<MemoryEntry> FindConflicts(string? text)
    {
        var conflicts = new List<MemoryEntry>(_global.FindConflicts(text));
        if (_project is not null) conflicts.AddRange(_project.FindConflicts(text));
        return conflicts.DistinctBy(entry => entry.Id).Take(3).ToArray();
    }

    /// <summary>
    /// 构造注入提示词的记忆块：全局层与项目层分开标注，让模型明确哪些偏好跨项目通用、
    /// 哪些事实只属于当前工作文件夹。有 query 时只注入置顶、手写偏好与命中条目；
    /// 无 query 时才按新旧补足。exposeRecallHint=true 时提示还有多少条可按需查询。
    /// </summary>
    public string BuildContext(
        string? query,
        int maxEntries = 6,
        int maxChars = 800,
        string? scope = null,
        bool exposeRecallHint = false)
    {
        var current = DateTime.Now;
        var includeProject = ShouldIncludeProject(scope);
        var globalBudget = includeProject ? Math.Max(2, maxEntries - 2) : maxEntries;
        var hasQuery = !string.IsNullOrWhiteSpace(query);

        var globalEntries = _global.SelectForContext(
            query, globalBudget, current, scope: null, fill: !hasQuery, includeManual: true);
        var projectEntries = includeProject && _project is not null
            ? _project.SelectForContext(
                query, Math.Min(4, maxEntries), current, scope: null, fill: !hasQuery, includeManual: false)
            : Array.Empty<MemoryEntry>();

        var visible = VisibleStores(scope).SelectMany(store => store.Entries)
            .Count(entry => !entry.IsExpired(current));

        if (globalEntries.Count == 0 && projectEntries.Count == 0)
        {
            return exposeRecallHint && visible > 0
                ? $"（本机有 {visible} 条长期记忆，需要时用 recall_memory 按关键词查询）"
                : "";
        }

        var builder = new StringBuilder();
        builder.AppendLine("以下是本机保存的长期记忆，分为全局与项目两层；仅在相关时参考，不要虚构未记录的内容：");
        var bodyBudget = Math.Max(120, maxChars - (exposeRecallHint ? 40 : 0));
        AppendSection(builder, "【全局记忆 · 用户偏好与通用习惯】", globalEntries, bodyBudget);
        if (projectEntries.Count > 0)
        {
            AppendSection(builder, $"【项目记忆 · 当前工作文件夹 {_workspace}】", projectEntries, bodyBudget);
        }

        var injected = globalEntries.Count + projectEntries.Count;
        if (exposeRecallHint && visible > injected)
        {
            builder.AppendLine($"（另有 {visible - injected} 条记忆未展开，需要时用 recall_memory 按关键词查询）");
        }

        return builder.ToString().TrimEnd();
    }

    /// <summary>接口版本不带 recall 提示：普通调用方只拿注入内容。</summary>
    string IMemoryStore.BuildContext(string? query, int maxEntries, int maxChars, string? scope) =>
        BuildContext(query, maxEntries, maxChars, scope);

    /// <summary>清理两层记忆中的过期条目，返回删除条数。</summary>
    public int PruneExpired(DateTime? now = null) =>
        _global.PruneExpired(now) + (_project?.PruneExpired(now) ?? 0);

    /// <summary>启动时整理两层记忆：先清理过期，再合并高度相似的历史条目，返回处理条数。</summary>
    public int Consolidate(DateTime? now = null) =>
        PruneExpired(now) + _global.Consolidate() + (_project?.Consolidate() ?? 0);

    public void Save()
    {
        _global.Save();
        _project?.Save();
    }

    /// <summary>当前作用域能看到哪些存储：全局始终可见，项目层仅在工作区匹配时可见。</summary>
    private List<MemoryService> VisibleStores(string? scope)
    {
        var stores = new List<MemoryService>(2) { _global };
        if (_project is null) return stores;
        if (scope is null || SameWorkspace(scope, _workspace)) stores.Add(_project);
        return stores;
    }

    private bool ShouldIncludeProject(string? scope)
    {
        if (_project is null) return false;
        if (scope is null) return true;
        var value = scope.Trim();
        return value.Length > 0 && SameWorkspace(value, _workspace);
    }

    private static void AppendSection(
        StringBuilder builder,
        string header,
        IReadOnlyList<MemoryEntry> entries,
        int maxChars)
    {
        if (entries.Count == 0) return;
        if (builder.Length + header.Length + 1 > maxChars) return;

        builder.AppendLine(header);
        foreach (var entry in entries)
        {
            var line = $"- {entry.Text}\n";
            if (builder.Length + line.Length > maxChars) break;
            builder.Append(line);
        }
    }

    /// <summary>旧版单文件记忆迁移：按 Kind / Scope 拆分到全局与各项目文件，原文件改名为 .migrated 备份。</summary>
    private void MigrateLegacyFile()
    {
        if (!File.Exists(_legacyFile)) return;

        try
        {
            var entries = JsonSerializer.Deserialize<List<MemoryEntry>>(
                File.ReadAllText(_legacyFile), JsonOptions) ?? [];
            foreach (var entry in entries)
            {
                if (string.IsNullOrWhiteSpace(entry.Text)) continue;
                if (entry.IsProject && !string.IsNullOrWhiteSpace(entry.Scope))
                {
                    var store = OpenProjectStore(entry.Scope);
                    var written = store.Upsert(entry.Text, entry.Source, MemoryEntry.KindProject, entry.Scope);
                    if (entry.Pinned) store.TogglePin(written.Entry.Id);
                }
                else
                {
                    var written = _global.Upsert(entry.Text, entry.Source, MemoryEntry.KindUser, "");
                    if (entry.Pinned) _global.TogglePin(written.Entry.Id);
                }
            }

            File.Move(_legacyFile, _legacyFile + ".migrated", overwrite: true);
        }
        catch
        {
            // 迁移失败时保留原文件，下次启动重试；不影响新记忆读写。
        }
    }

    private MemoryService OpenProjectStore(string workspace)
    {
        var normalized = NormalizeRoot(workspace);
        var store = new MemoryService(
            ProjectFilePath(normalized),
            MemoryTier.Project,
            normalized,
            MaxProjectEntries,
            ProjectTtlDays);
        store.Load();
        return store;
    }

    private string ProjectFilePath(string workspace) =>
        Path.Combine(_directory, "projects", ProjectKey(workspace) + ".json");

    /// <summary>工作区路径指纹：同一路径无论大小写或分隔符如何书写，都落到同一个项目文件。</summary>
    private static string ProjectKey(string workspace)
    {
        var normalized = workspace.Replace('\\', '/').ToLowerInvariant();
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        return Convert.ToHexString(hash)[..16].ToLowerInvariant();
    }

    private static string NormalizeRoot(string? root)
    {
        if (string.IsNullOrWhiteSpace(root)) return "";
        try
        {
            return Path.GetFullPath(root.Trim());
        }
        catch
        {
            return "";
        }
    }

    private static bool SameWorkspace(string left, string right) =>
        NormalizeRoot(left).Equals(NormalizeRoot(right), PathComparison);
}
