using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using LocalModelHub.Core.Models;

namespace LocalModelHub.Core.Services;

/// <summary>长期记忆：保存在本机 memory.json，对话时按关键词召回并注入上下文。</summary>
public sealed class MemoryService
{
    public const int MaxEntries = 200;
    public const int MaxTextLength = 500;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    private static readonly Regex LatinToken = new("[a-z0-9_.-]{2,}", RegexOptions.Compiled);
    private static readonly Regex Whitespace = new(@"\s+", RegexOptions.Compiled);

    private readonly string? _filePath;
    private readonly List<MemoryEntry> _entries = [];

    public MemoryService(string? filePath = null) => _filePath = filePath;

    public string FilePath => _filePath ?? AppPaths.MemoryFile;

    public IReadOnlyList<MemoryEntry> Entries => _entries;

    public IReadOnlyList<MemoryEntry> Load()
    {
        _entries.Clear();
        try
        {
            if (File.Exists(FilePath))
            {
                var loaded = JsonSerializer.Deserialize<List<MemoryEntry>>(
                    File.ReadAllText(FilePath), JsonOptions) ?? [];

                foreach (var entry in loaded
                             .Where(entry => !string.IsNullOrWhiteSpace(entry.Id) && !string.IsNullOrWhiteSpace(entry.Text))
                             .OrderByDescending(entry => entry.CreatedAt)
                             .Take(MaxEntries))
                {
                    if (entry.UpdatedAt == default) entry.UpdatedAt = entry.CreatedAt;
                    _entries.Add(entry);
                }
            }
        }
        catch
        {
            // 记忆文件损坏时按空记忆处理
        }

        return _entries;
    }

    public MemoryEntry Add(string? text, string source = "manual")
    {
        var trimmed = (text ?? "").Trim();
        if (trimmed.Length == 0) throw new ArgumentException("记忆内容不能为空", nameof(text));
        if (trimmed.Length > MaxTextLength) trimmed = trimmed[..MaxTextLength];

        // 去重：内容归一化后相同则更新时间并置顶，不新增条目
        var normalized = Normalize(trimmed);
        var existing = _entries.FirstOrDefault(entry => Normalize(entry.Text) == normalized);
        if (existing is not null)
        {
            existing.UpdatedAt = DateTime.Now;
            _entries.Remove(existing);
            _entries.Insert(0, existing);
            Save();
            return existing;
        }

        var now = DateTime.Now;
        var entry = new MemoryEntry
        {
            Id = Guid.NewGuid().ToString("N")[..10],
            Text = trimmed,
            Source = source,
            CreatedAt = now,
            UpdatedAt = now
        };

        _entries.Insert(0, entry);
        while (_entries.Count > MaxEntries) _entries.RemoveAt(_entries.Count - 1);
        Save();
        return entry;
    }

    public bool Contains(string? text)
    {
        var normalized = Normalize((text ?? "").Trim());
        return normalized.Length > 0 &&
               _entries.Any(entry => Normalize(entry.Text) == normalized);
    }

    public bool TogglePin(string entryId)
    {
        var entry = _entries.FirstOrDefault(item =>
            item.Id.Equals(entryId, StringComparison.OrdinalIgnoreCase));
        if (entry is null) return false;

        entry.Pinned = !entry.Pinned;
        entry.UpdatedAt = DateTime.Now;
        Save();
        return entry.Pinned;
    }

    public bool UpdateText(string entryId, string? text)
    {
        var trimmed = (text ?? "").Trim();
        if (trimmed.Length == 0) return false;
        if (trimmed.Length > MaxTextLength) trimmed = trimmed[..MaxTextLength];

        var entry = _entries.FirstOrDefault(item =>
            item.Id.Equals(entryId, StringComparison.OrdinalIgnoreCase));
        if (entry is null) return false;

        entry.Text = trimmed;
        entry.UpdatedAt = DateTime.Now;
        Save();
        return true;
    }

    public bool Remove(string entryId) =>
        RemoveWhere(entry => entry.Id.Equals(entryId, StringComparison.OrdinalIgnoreCase));

    public void Clear()
    {
        _entries.Clear();
        Save();
    }

    /// <summary>按关键词（英文单词 + 中文二元组）打分召回，最多返回 limit 条。</summary>
    public IReadOnlyList<MemoryEntry> Search(string? query, int limit = 5)
    {
        if (limit <= 0 || _entries.Count == 0) return [];
        if (string.IsNullOrWhiteSpace(query)) return _entries.Take(limit).ToArray();

        return _entries
            .Select(entry => (Entry: entry, Score: Score(entry.Text, query)))
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score)
            .ThenByDescending(x => x.Entry.CreatedAt)
            .Take(limit)
            .Select(x => x.Entry)
            .ToArray();
    }

    /// <summary>构造注入提示词的记忆块：固定记忆优先，其次关键词召回，最后按新旧补足；受字符预算限制。</summary>
    public string BuildContext(string? query, int maxEntries = 6, int maxChars = 800)
    {
        if (_entries.Count == 0) return "";

        var selected = new List<MemoryEntry>();
        void TryAdd(MemoryEntry entry)
        {
            if (selected.Count >= maxEntries) return;
            if (selected.Any(item => item.Id == entry.Id)) return;
            selected.Add(entry);
        }

        foreach (var entry in _entries.Where(entry => entry.Pinned).Take(3)) TryAdd(entry);
        foreach (var entry in Search(query, maxEntries)) TryAdd(entry);
        foreach (var entry in _entries)
        {
            if (selected.Count >= maxEntries) break;
            TryAdd(entry);
        }

        if (selected.Count == 0) return "";

        const string header = "以下是本机保存的用户长期记忆，仅在相关时参考，不要虚构未记录的内容：";
        var builder = new StringBuilder(header).AppendLine();
        var headerLength = builder.Length;

        foreach (var entry in selected)
        {
            var line = $"- {entry.Text}\n";
            if (builder.Length + line.Length > maxChars && builder.Length > headerLength) break;
            builder.Append(line);
        }

        return builder.ToString().TrimEnd();
    }

    private bool RemoveWhere(Func<MemoryEntry, bool> predicate)
    {
        var removed = false;
        for (var index = _entries.Count - 1; index >= 0; index--)
        {
            if (!predicate(_entries[index])) continue;
            _entries.RemoveAt(index);
            removed = true;
        }

        if (removed) Save();
        return removed;
    }

    public void Save()
    {
        AppPaths.EnsureCreated();
        var temp = FilePath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(_entries, JsonOptions));
        File.Move(temp, FilePath, overwrite: true);
    }

    private static string Normalize(string text) =>
        Whitespace.Replace(text.Trim(), " ").ToLowerInvariant();

    private static int Score(string text, string query)
    {
        var haystack = text.ToLowerInvariant();
        var score = 0;

        foreach (Match match in LatinToken.Matches(query.ToLowerInvariant()))
        {
            if (haystack.Contains(match.Value)) score += 3;
        }

        var cjk = query.Where(IsCjk).ToArray();
        for (var index = 0; index + 1 < cjk.Length; index++)
        {
            var gram = new string([cjk[index], cjk[index + 1]]);
            if (haystack.Contains(gram)) score++;
        }

        return score;
    }

    private static bool IsCjk(char value) =>
        value is >= '\u4E00' and <= '\u9FFF' or >= '\u3400' and <= '\u4DBF';
}
