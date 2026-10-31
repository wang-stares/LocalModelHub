using System.Text.Json;
using System.Text.RegularExpressions;

namespace LocalModelHub.Core.Services;

using LocalModelHub.Core.Models;

/// <summary>技能库：保存在本机 skills.json，对话时把相关技能注入上下文。</summary>
public sealed class SkillService
{
    public const int MaxEntries = 100;

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
    /// 构造注入提示词的技能块：启用中的技能按关键词命中优先，其次按最近使用/更新时间补足。
    /// </summary>
    public string BuildContext(string? query, int maxEntries = 3, int maxChars = 700)
    {
        var enabled = _entries.Where(entry => entry.Enabled).ToList();
        if (enabled.Count == 0) return "";

        var scored = enabled
            .Select(entry => (Entry: entry, Score: Score(entry, query)))
            .OrderByDescending(item => item.Score)
            .ThenByDescending(item => item.Entry.Uses)
            .ThenByDescending(item => item.Entry.UpdatedAt)
            .Select(item => item.Entry)
            .Take(Math.Clamp(maxEntries, 1, 6))
            .ToList();

        var builder = new System.Text.StringBuilder();
        builder.AppendLine("【已沉淀技能】以下步骤来自本机之前完成过的类似任务，可参考执行：");
        var used = 0;
        var index = 0;
        foreach (var entry in scored)
        {
            var block = $"{index + 1}. {entry.Name}｜适用：{entry.Trigger}\n   步骤：{entry.Steps}\n";
            if (used + block.Length > maxChars) break;
            builder.Append(block);
            used += block.Length;
            index++;
            entry.Uses++;
        }

        if (index > 0) Save();
        return index == 0 ? "" : builder.ToString().TrimEnd();
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
