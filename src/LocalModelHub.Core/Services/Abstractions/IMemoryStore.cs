using LocalModelHub.Core.Models;

namespace LocalModelHub.Core.Services.Abstractions;

/// <summary>长期记忆的标准化读写契约：新增 / 查询 / 更新 / 删除 / 过期清理，落盘细节由实现负责。</summary>
public interface IMemoryStore
{
    string FilePath { get; }

    IReadOnlyList<MemoryEntry> Entries { get; }

    IReadOnlyList<MemoryEntry> Load();

    MemoryEntry Add(string? text, string source = "manual");

    /// <summary>
    /// 写入一条记忆并返回是否新增：内容完全相同时更新时间，近似重复时用新表述覆盖旧条目。
    /// </summary>
    /// <param name="ttlDays">可选过期天数；为空时使用存储层默认策略（项目记忆默认 90 天，全局记忆不过期）。</param>
    MemoryWriteResult Upsert(
        string? text,
        string source = "manual",
        string kind = MemoryEntry.KindUser,
        string scope = "",
        int? ttlDays = null);

    MemoryEntry? Get(string? entryId);

    bool Contains(string? text);

    bool TogglePin(string entryId);

    bool UpdateText(string entryId, string? text);

    bool Remove(string entryId);

    void Clear();

    /// <param name="scope">为空表示不做作用域过滤；传入工作区路径时额外包含全局记忆。</param>
    IReadOnlyList<MemoryEntry> Search(string? query, int limit = 5, DateTime? now = null, string? scope = null);

    IReadOnlyList<MemoryEntry> FindConflicts(string? text);

    /// <param name="scope">为空表示不做作用域过滤；传入工作区路径时额外包含全局记忆。</param>
    string BuildContext(string? query, int maxEntries = 6, int maxChars = 800, string? scope = null);

    /// <summary>清理已过期的非置顶记忆，返回删除条数。</summary>
    int PruneExpired(DateTime? now = null);

    void Save();
}
