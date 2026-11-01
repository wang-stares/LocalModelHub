using System.Text.Json;
using System.Text.RegularExpressions;

namespace LocalModelHub.Core.Services;

using LocalModelHub.Core.Models;

/// <summary>注入的技能块与本次实际注入的技能 Id（用于回答完成后记账）。</summary>
public sealed record SkillContext(string Text, IReadOnlyList<string> SkillIds)
{
    public static SkillContext Empty { get; } = new("", []);
}

/// <summary>技能库：保存在本机 skills.json，对话时把相关技能注入上下文。</summary>
public sealed class SkillService
{
    public const int MaxEntries = 100;

    /// <summary>注入的最低相关度分数：低于该分数的技能视为无关，宁可不注入（两个中文二元组或一个文件名 + 一个二元组）。</summary>
    public const int DefaultMinScore = 4;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    private static readonly Regex LatinToken = new("[a-z0-9_.-]{2,}", RegexOptions.Compiled);
    private static readonly Regex Whitespace = new(@"\s+", RegexOptions.Compiled);

    private readonly string? _filePath;
    private readonly List<SkillEntry> _entries = [];

    public SkillService(string? filePath = null) => _filePath = filePath;

    public string FilePath => _filePath ?? AppPaths.SkillsFile;

    public IReadOnlyList<SkillEntry> Entries => _entries;

    public IReadOnlyList<SkillEntry> Load()
    {
        _entries.Clear();
        try
        {
            if (File.Exists(FilePath))
            {
                var loaded = JsonSerializer.Deserialize<List<SkillEntry>>(
                    File.ReadAllText(FilePath), JsonOptions) ?? [];

                foreach (var entry in loaded
                             .Where(entry => !string.IsNullOrWhiteSpace(entry.Id) && !string.IsNullOrWhiteSpace(entry.Name))
                             .OrderByDescending(entry => entry.UpdatedAt)
                             .Take(MaxEntries))
                {
                    if (entry.CreatedAt == default) entry.CreatedAt = DateTime.Now;
                    if (entry.UpdatedAt == default) entry.UpdatedAt = entry.CreatedAt;
                    if (entry.Steps.Length > 1200) entry.Steps = entry.Steps[..1200];
                    _entries.Add(entry);
                }
            }
        }
        catch
        {
            // 技能文件损坏时按空库处理
        }

        return _entries;
    }

    public SkillEntry Add(SkillDraft draft, string source = "auto") =>
        Add(draft.Name, draft.Trigger, draft.Steps, source);

    public SkillEntry Add(string? name, string? trigger, string? steps, string source = "auto")
    {
        var title = (name ?? "").Trim();
        if (title.Length == 0) throw new ArgumentException("技能名称不能为空", nameof(name));
        if (title.Length > 60) title = title[..60];

        var when = (trigger ?? "").Trim();
        if (when.Length > 200) when = when[..200];
        var body = (steps ?? "").Trim();
        if (body.Length > 1200) body = body[..1200];

        // 同名技能视为同一条，更新步骤而不是无限堆积。
        var existing = _entries.FirstOrDefault(entry =>
            entry.Name.Equals(title, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            existing.Trigger = when;
            existing.Steps = body;
            existing.UpdatedAt = DateTime.Now;
            _entries.Remove(existing);
            _entries.Insert(0, existing);
            Save();
            return existing;
        }

        var now = DateTime.Now;
        var entry = new SkillEntry
        {
            Id = Guid.NewGuid().ToString("N")[..10],
            Name = title,
            Trigger = when,
            Steps = body,
            Source = source,
            CreatedAt = now,
            UpdatedAt = now
        };

        _entries.Insert(0, entry);
        while (_entries.Count > MaxEntries) _entries.RemoveAt(_entries.Count - 1);
        Save();
        return entry;
    }

    public bool Remove(string id)
    {
        var entry = _entries.FirstOrDefault(item => item.Id == id);
        if (entry is null) return false;
        _entries.Remove(entry);
        Save();
        return true;
    }

    public bool Toggle(string id)
    {
        var entry = _entries.FirstOrDefault(item => item.Id == id);
        if (entry is null) return false;
        entry.Enabled = !entry.Enabled;
        entry.UpdatedAt = DateTime.Now;
        Save();
        return entry.Enabled;
    }

    public void Clear()
    {
        _entries.Clear();
        Save();
    }

    /// <summary>
    /// 构造注入提示词的技能块。纯读操作：不累加 Uses、不写盘；低于 minScore 的无关技能直接跳过。
    /// </summary>
    public string BuildContext(string? query, int maxEntries = 3, int maxChars = 700) =>
        SelectContext(query, maxEntries, maxChars).Text;

    /// <summary>按相关度挑选技能并构造提示词块，同时返回实际注入的技能 Id。</summary>
    public SkillContext SelectContext(
        string? query,
        int maxEntries = 3,
        int maxChars = 700,
        int minScore = DefaultMinScore)
    {
        var enabled = _entries.Where(entry => entry.Enabled).ToList();
        if (enabled.Count == 0) return SkillContext.Empty;

        var scored = enabled
            .Select(entry => (Entry: entry, Score: Score(entry, query)))
            .Where(item => item.Score >= minScore)
            .OrderByDescending(item => item.Score)
            .ThenByDescending(item => item.Entry.Uses)
            .ThenByDescending(item => item.Entry.UpdatedAt)
            .Select(item => item.Entry)
            .Take(Math.Clamp(maxEntries, 1, 6))
            .ToList();

        if (scored.Count == 0) return SkillContext.Empty;

        var builder = new System.Text.StringBuilder();
        builder.AppendLine("【已沉淀技能】以下步骤来自本机之前完成过的类似任务，可参考执行：");
        var used = 0;
        var index = 0;
        var ids = new List<string>();
        foreach (var entry in scored)
        {
            var block = $"{index + 1}. {entry.Name}｜适用：{entry.Trigger}\n   步骤：{entry.Steps}\n";
            if (used + block.Length > maxChars) break;
            builder.Append(block);
            used += block.Length;
            index++;
            ids.Add(entry.Id);
        }

        return index == 0 ? SkillContext.Empty : new SkillContext(builder.ToString().TrimEnd(), ids);
    }

    /// <summary>
    /// 记账：回答确实用上了注入的技能后累加使用次数，并只写一次盘。
    /// 与 <see cref="SelectContext"/> 分离，保证「读上下文」不再产生副作用。
    /// </summary>
    public int MarkUsed(IEnumerable<string>? skillIds)
    {
        if (skillIds is null) return 0;

        var changed = 0;
        foreach (var id in skillIds)
        {
            var entry = _entries.FirstOrDefault(item => item.Id == id);
            if (entry is null) continue;
            entry.Uses++;
            changed++;
        }

        if (changed > 0) Save();
        return changed;
    }

    private static int Score(SkillEntry entry, string? query)
    {
        var text = (query ?? "").Trim();
        if (text.Length == 0) return 0;

        var haystack = $"{entry.Name} {entry.Trigger} {entry.Steps}".ToLowerInvariant();
        var score = 0;
        foreach (Match match in LatinToken.Matches(text.ToLowerInvariant()))
        {
            if (haystack.Contains(match.Value, StringComparison.Ordinal)) score += 3;
        }

        var compact = Whitespace.Replace(text, "");
        for (var i = 0; i + 2 <= compact.Length; i++)
        {
            if (haystack.Contains(compact.Substring(i, 2), StringComparison.Ordinal)) score += 2;
        }

        return score;
    }

    private void Save()
    {
        try
        {
            var directory = Path.GetDirectoryName(FilePath);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(_entries, JsonOptions));
        }
        catch
        {
            // 写入失败不阻断对话
        }
    }
}
