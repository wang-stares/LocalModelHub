using LocalModelHub.Core.Models;

namespace LocalModelHub.Core.Services.Abstractions;

/// <summary>本机长期记忆的存储契约：条目上限与落盘细节由实现负责。</summary>
public interface IMemoryStore
{
    string FilePath { get; }

    IReadOnlyList<MemoryEntry> Entries { get; }

    IReadOnlyList<MemoryEntry> Load();

    MemoryEntry Add(string? text, string source = "manual");

    bool Contains(string? text);

    bool TogglePin(string entryId);

    bool UpdateText(string entryId, string? text);

    bool Remove(string entryId);

    void Clear();

    IReadOnlyList<MemoryEntry> Search(string? query, int limit = 5, DateTime? now = null);

    IReadOnlyList<MemoryEntry> FindConflicts(string? text);

    string BuildContext(string? query, int maxEntries = 6, int maxChars = 800);

    void Save();
}
