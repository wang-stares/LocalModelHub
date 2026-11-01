using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using LocalModelHub.Core.Models;
using LocalModelHub.Core.Services.Abstractions;

namespace LocalModelHub.Core.Services;

/// <summary>
/// 单层长期记忆存储：一个文件对应一个层级（全局 / 某个工作区的项目记忆），
/// 对话时按关键词召回并注入上下文。层级约束由构造参数决定，越界写入会直接拒绝。
/// </summary>
public sealed class MemoryService : IMemoryStore
{
    public const int MaxEntries = 200;
    public const int MaxTextLength = 500;
    /// <summary>用户手写来源：默认不过期，也不会被模型自动记录覆盖原话。</summary>
    public const string ManualSource = "manual";
    /// <summary>两条记忆的词元重叠度达到该阈值即视为同一事实的不同表述，合并而不是各存一份。</summary>
    public const double MergeSimilarity = 0.75;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    private static readonly Regex LatinToken = new("[a-z0-9_.-]{2,}", RegexOptions.Compiled);
    private static readonly Regex Whitespace = new(@"\s+", RegexOptions.Compiled);
    private static readonly Regex NegationPattern = new(
        @"不要|不需要|不用|别|禁止|不能|避免|不是|\bnot\b|\bnever\b|\bdon't\b|\bdo not\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private readonly string? _filePath;
    private readonly List<MemoryEntry> _entries = [];
    private readonly MemoryTier? _tier;
    private readonly string _scope;
    private readonly int _maxEntries;
    private readonly int? _ttlDays;

    /// <param name="filePath">记忆文件；为空时使用旧版单文件路径。</param>
    /// <param name="tier">固定层级：全局层只接受用户偏好，项目层只接受项目事实；为空表示混合存储（旧行为）。</param>
    /// <param name="scope">项目层绑定的工作文件夹绝对路径。</param>
    /// <param name="maxEntries">该层允许保留的最大条目数。</param>
    /// <param name="ttlDays">该层默认过期天数；为空表示不自动过期。</param>
    public MemoryService(
        string? filePath = null,
        MemoryTier? tier = null,
        string? scope = null,
        int maxEntries = MaxEntries,
        int? ttlDays = null)
    {
        _filePath = filePath;
        _tier = tier;
        _scope = (scope ?? "").Trim();
        _maxEntries = Math.Max(1, maxEntries);
        _ttlDays = ttlDays;
    }

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
                             .Take(_maxEntries))
                {
                    if (entry.UpdatedAt == default) entry.UpdatedAt = entry.CreatedAt;
                    NormalizeLoaded(entry);
                    _entries.Add(entry);
                }
            }
        }
        catch
        {
            // 记忆文件损坏时按空记忆处理
        }

        PruneExpired();
        return _entries;
    }

    /// <summary>加载时把条目归到本存储层：项目文件里的条目强制为项目事实并绑定当前工作区。</summary>
    private void NormalizeLoaded(MemoryEntry entry)
    {
        if (_tier is null)
        {
            // 旧版本没有 Kind / Scope 字段：按用户偏好、全局作用域兼容读取。
            if (string.IsNullOrWhiteSpace(entry.Kind)) entry.Kind = MemoryEntry.KindUser;
            entry.Scope ??= "";
            return;
        }

        var isProject = _tier == MemoryTier.Project;
        entry.Kind = isProject ? MemoryEntry.KindProject : MemoryEntry.KindUser;
        entry.Scope = isProject ? _scope : "";
    }

    public MemoryEntry Add(string? text, string source = "manual") =>
        Upsert(text, source).Entry;

    public MemoryWriteResult Upsert(
        string? text,
        string source = "manual",
        string kind = MemoryEntry.KindUser,
        string scope = "",
        int? ttlDays = null)
    {
        var trimmed = (text ?? "").Trim();
        if (trimmed.Length == 0) throw new ArgumentException("记忆内容不能为空", nameof(text));
        if (trimmed.Length > MaxTextLength) trimmed = trimmed[..MaxTextLength];

        // 层级隔离：全局层不接收项目事实，项目层不接收用户偏好，也拒绝写入别的工作区。
        if (_tier == MemoryTier.Global && kind == MemoryEntry.KindProject)
        {
            throw new InvalidOperationException("全局记忆层只接收用户偏好；项目事实请写入当前工作区的项目记忆");
        }

        if (_tier == MemoryTier.Project && kind != MemoryEntry.KindProject)
        {
            throw new InvalidOperationException("项目记忆层只接收项目事实；用户偏好请写入全局记忆");
        }

        if (_tier == MemoryTier.Project &&
            !string.IsNullOrWhiteSpace(scope) &&
            !SameScope(scope, _scope))
        {
            throw new InvalidOperationException("项目记忆只能写入它所属的工作文件夹");
        }

        var normalizedKind = _tier switch
        {
            MemoryTier.Global => MemoryEntry.KindUser,
            MemoryTier.Project => MemoryEntry.KindProject,
            _ => kind == MemoryEntry.KindProject ? MemoryEntry.KindProject : MemoryEntry.KindUser
        };
        var normalizedScope = _tier switch
        {
            MemoryTier.Global => "",
            MemoryTier.Project => _scope,
            _ => normalizedKind == MemoryEntry.KindProject ? (scope ?? "").Trim() : ""
        };

        var now = DateTime.Now;
        var expiresAt = ResolveExpiry(ttlDays, source, now);

        // 完全重复：同一类型、同一作用域下内容归一化后相同则更新时间并置顶，不新增条目。
        var normalized = Normalize(trimmed);
        var existing = _entries.FirstOrDefault(entry =>
            string.Equals(entry.Scope ?? "", normalizedScope, StringComparison.OrdinalIgnoreCase) &&
            (entry.Kind ?? MemoryEntry.KindUser) == normalizedKind &&
            Normalize(entry.Text) == normalized);
        if (existing is not null)
        {
            existing.UpdatedAt = now;
            // 用户手写的记忆永久保留，自动写入只刷新时间，不改写它的过期时间。
            if (existing.Source != ManualSource) existing.ExpiresAt = expiresAt;
            _entries.Remove(existing);
            _entries.Insert(0, existing);
            Save();
            return new MemoryWriteResult(existing, false);
        }

        // 近似重复：同一类型、同一作用域下只是换了说法时，用新表述覆盖旧条目，避免记忆膨胀。
        var similar = FindSimilar(normalized, normalizedKind, normalizedScope);
        if (similar is not null)
        {
            // 用户明确写过的事实优先：模型自动记录只能更新时间，不能改写用户的原话。
            if (similar.Source == ManualSource && source != ManualSource)
            {
                similar.UpdatedAt = now;
                Save();
                return new MemoryWriteResult(similar, false);
            }

            similar.Text = trimmed;
            similar.Source = source;
            similar.UpdatedAt = now;
            similar.ExpiresAt = expiresAt;
            _entries.Remove(similar);
            _entries.Insert(0, similar);
            Save();
            return new MemoryWriteResult(similar, false);
        }

        var entry = new MemoryEntry
        {
            Id = Guid.NewGuid().ToString("N")[..10],
            Text = trimmed,
            Source = source,
            Kind = normalizedKind,
            Scope = normalizedScope,
            CreatedAt = now,
            UpdatedAt = now,
            ExpiresAt = expiresAt
        };

        _entries.Insert(0, entry);
        TrimToCapacity();
        Save();
        return new MemoryWriteResult(entry, true);
    }

    /// <summary>
    /// 超出容量时优先淘汰自动写入的旧条目：用户手写和置顶的记忆最后才动，
    /// 避免一轮轮自动提炼把真正重要的偏好挤掉。
    /// </summary>
    private void TrimToCapacity()
    {
        while (_entries.Count > _maxEntries)
        {
            var index = LastIndex(entry => !entry.Pinned && entry.Source != ManualSource);
            if (index < 0) index = LastIndex(entry => !entry.Pinned);
            if (index < 0) index = _entries.Count - 1;
            _entries.RemoveAt(index);
        }
    }

    private int LastIndex(Func<MemoryEntry, bool> predicate)
    {
        for (var index = _entries.Count - 1; index >= 0; index--)
        {
            if (predicate(_entries[index])) return index;
        }

        return -1;
    }

    /// <summary>默认过期策略：显式 TTL 优先；用户手写条目默认永久，模型自动记录按层级 TTL 过期。</summary>
    private DateTime? ResolveExpiry(int? ttlDays, string source, DateTime now)
    {
        if (ttlDays is not null) return ttlDays is > 0 ? now.AddDays(ttlDays.Value) : null;
        if (source == ManualSource) return null;

        var days = _ttlDays;
        return days is > 0 ? now.AddDays(days.Value) : null;
    }

    public MemoryEntry? Get(string? entryId)
    {
        if (string.IsNullOrWhiteSpace(entryId)) return null;
        return _entries.FirstOrDefault(entry =>
            entry.Id.Equals(entryId, StringComparison.OrdinalIgnoreCase));
    }

    public bool Contains(string? text)
    {
        var normalized = Normalize((text ?? "").Trim());
        return normalized.Length > 0 &&
               _entries.Any(entry => Normalize(entry.Text) == normalized);
    }

    public bool TogglePin(string entryId)
    {
        var entry = Get(entryId);
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

        var entry = Get(entryId);
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

    /// <summary>
    /// 按关键词（英文单词 + 中文二元组）打分召回，叠加时间衰减与置顶权重：越新的偏好越优先，
    /// 置顶记忆额外加权；最多返回 limit 条。已过期的条目不会参与召回。
    /// </summary>
    public IReadOnlyList<MemoryEntry> Search(
        string? query,
        int limit = 5,
        DateTime? now = null,
        string? scope = null)
    {
        if (limit <= 0 || _entries.Count == 0) return [];

        var current = now ?? DateTime.Now;
        var visible = VisibleEntries(current, scope);
        if (visible.Count == 0) return [];
        if (string.IsNullOrWhiteSpace(query))
        {
            return visible.OrderByDescending(entry => entry.Pinned)
                .ThenByDescending(entry => entry.UpdatedAt)
                .Take(limit)
                .ToArray();
        }

        return visible
            .Select(entry => (Entry: entry, Score: Score(entry, query, current)))
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score)
            .ThenByDescending(x => x.Entry.UpdatedAt)
            .Take(limit)
            .Select(x => x.Entry)
            .ToArray();
    }

    /// <summary>
    /// 找可能冲突的旧记忆：与候选文本共享至少两个词，但否定语义相反
    /// （如「不要用深色主题」与「使用深色主题」）。界面据此提示用户确认，而不是静默叠加。
    /// </summary>
    public IReadOnlyList<MemoryEntry> FindConflicts(string? text)
    {
        var normalized = Normalize((text ?? "").Trim());
        if (normalized.Length == 0) return [];

        var candidateNegation = NegationPattern.IsMatch(normalized);
        var candidateTokens = Tokens(normalized);
        if (candidateTokens.Count == 0) return [];

        return _entries
            .Select(entry => (Entry: entry, Normalized: Normalize(entry.Text), Tokens: Tokens(Normalize(entry.Text))))
            .Where(pair => NegationPattern.IsMatch(pair.Normalized) != candidateNegation)
            .Select(pair => (pair.Entry, Overlap: Overlap(pair.Tokens, candidateTokens)))
            .Where(pair => pair.Overlap >= 2)
            .OrderByDescending(pair => pair.Overlap)
            .ThenByDescending(pair => pair.Entry.UpdatedAt)
            .Take(3)
            .Select(pair => pair.Entry)
            .ToArray();
    }

    /// <summary>构造注入提示词的记忆块：固定记忆优先，其次关键词召回，最后按新旧补足；受字符预算限制。</summary>
    public string BuildContext(string? query, int maxEntries = 6, int maxChars = 800, string? scope = null)
    {
        var selected = SelectForContext(query, maxEntries, scope: scope);
        if (selected.Count == 0) return "";

        const string header = "以下是本机保存的长期记忆（用户偏好与项目事实），仅在相关时参考，不要虚构未记录的内容：";
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

    /// <summary>
    /// 选取注入上下文的条目：置顶最多 3 条，用户手写的最多 2 条（稳定偏好常驻），
    /// 然后关键词召回；只有 fill=true 时才按新旧补足，避免无关记忆白白占用上下文。
    /// 供单层存储与两层管理器复用的稳定选择策略。
    /// </summary>
    public IReadOnlyList<MemoryEntry> SelectForContext(
        string? query,
        int maxEntries = 6,
        DateTime? now = null,
        string? scope = null,
        bool fill = true,
        bool includeManual = true)
    {
        if (maxEntries <= 0 || _entries.Count == 0) return [];

        var current = now ?? DateTime.Now;
        var visible = VisibleEntries(current, scope);
        if (visible.Count == 0) return [];

        var selected = new List<MemoryEntry>();
        void TryAdd(MemoryEntry entry)
        {
            if (selected.Count >= maxEntries) return;
            if (selected.Any(item => item.Id == entry.Id)) return;
            selected.Add(entry);
        }

        foreach (var entry in visible.Where(entry => entry.Pinned).Take(3)) TryAdd(entry);
        if (includeManual)
        {
            foreach (var entry in visible.Where(entry => entry.Source == ManualSource && !entry.Pinned).Take(2))
            {
                TryAdd(entry);
            }
        }

        foreach (var entry in Search(query, maxEntries, current, scope)) TryAdd(entry);
        if (fill)
        {
            foreach (var entry in visible)
            {
                if (selected.Count >= maxEntries) break;
                TryAdd(entry);
            }
        }

        return selected;
    }

    /// <summary>
    /// 启动时整理历史条目：同层内高度相似（Jaccard ≥ 0.75）的两条只保留一条。
    /// 置顶和用户手写的条目不会被删除；同组里手动优先，其次保留较新的。
    /// 返回合并掉的条数。
    /// </summary>
    public int Consolidate()
    {
        var dropped = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var left = 0; left < _entries.Count; left++)
        {
            var first = _entries[left];
            if (dropped.Contains(first.Id)) continue;

            for (var right = _entries.Count - 1; right > left; right--)
            {
                var second = _entries[right];
                if (dropped.Contains(second.Id)) continue;
                if ((first.Kind ?? MemoryEntry.KindUser) != (second.Kind ?? MemoryEntry.KindUser)) continue;
                if (!SameScope(first.Scope ?? "", second.Scope ?? "")) continue;
                if (Similarity(first.Text, second.Text) < MergeSimilarity) continue;

                // 两条都受保护时保持原样，交给用户自行合并。
                if (IsProtected(first) && IsProtected(second)) continue;

                var keeper = PickKeeper(first, second);
                var loser = ReferenceEquals(keeper, first) ? second : first;
                if (IsProtected(loser)) continue;

                dropped.Add(loser.Id);
                if (ReferenceEquals(loser, first)) break;
            }
        }

        if (dropped.Count == 0) return 0;

        _entries.RemoveAll(entry => dropped.Contains(entry.Id));
        Save();
        return dropped.Count;
    }

    private static bool IsProtected(MemoryEntry entry) =>
        entry.Pinned || entry.Source == ManualSource;

    private static MemoryEntry PickKeeper(MemoryEntry left, MemoryEntry right)
    {
        if (left.Pinned != right.Pinned) return left.Pinned ? left : right;
        if ((left.Source == ManualSource) != (right.Source == ManualSource))
        {
            return left.Source == ManualSource ? left : right;
        }

        return left.UpdatedAt >= right.UpdatedAt ? left : right;
    }

    private static double Similarity(string left, string right)
    {
        var first = Tokens(Normalize(left));
        var second = Tokens(Normalize(right));
        var overlap = Overlap(first, second);
        var union = first.Count + second.Count - overlap;
        return union <= 0 ? 0 : overlap / (double)union;
    }

    /// <summary>清理已过期的非置顶记忆，返回删除条数。</summary>
    public int PruneExpired(DateTime? now = null)
    {
        var current = now ?? DateTime.Now;
        var removed = 0;
        for (var index = _entries.Count - 1; index >= 0; index--)
        {
            if (!_entries[index].IsExpired(current)) continue;
            _entries.RemoveAt(index);
            removed++;
        }

        if (removed > 0) Save();
        return removed;
    }

    private List<MemoryEntry> VisibleEntries(DateTime now, string? scope)
    {
        var visible = _entries.Where(entry => !entry.IsExpired(now));
        if (scope is null) return visible.ToList();
        return visible.Where(entry => IsVisible(entry, scope)).ToList();
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
        var directory = Path.GetDirectoryName(FilePath);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        var temp = FilePath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(_entries, JsonOptions));
        File.Move(temp, FilePath, overwrite: true);
    }

    private static string Normalize(string text) =>
        Whitespace.Replace(text.Trim(), " ").ToLowerInvariant();

    /// <summary>同类型、同作用域下词元重叠度最高的近似条目；没有达到阈值时返回 null。</summary>
    private MemoryEntry? FindSimilar(string normalized, string kind, string scope)
    {
        var tokens = Tokens(normalized);
        if (tokens.Count == 0) return null;

        MemoryEntry? best = null;
        var bestScore = 0d;
        foreach (var entry in _entries)
        {
            if ((entry.Kind ?? MemoryEntry.KindUser) != kind) continue;
            if (!string.Equals(entry.Scope ?? "", scope, StringComparison.OrdinalIgnoreCase)) continue;

            var other = Tokens(Normalize(entry.Text));
            if (other.Count == 0) continue;

            var overlap = Overlap(tokens, other);
            var union = tokens.Count + other.Count - overlap;
            if (union <= 0) continue;

            var similarity = overlap / (double)union;
            if (similarity < MergeSimilarity || similarity <= bestScore) continue;
            best = entry;
            bestScore = similarity;
        }

        return best;
    }

    /// <summary>作用域过滤：全局记忆始终可见；项目记忆只在对应工作区可见。</summary>
    private static bool IsVisible(MemoryEntry entry, string scope) =>
        string.IsNullOrWhiteSpace(entry.Scope) ||
        entry.Scope.Equals(scope, StringComparison.OrdinalIgnoreCase);

    private static bool SameScope(string left, string right) =>
        left.Trim().Equals(right.Trim(), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 关键词基础分 × 时间衰减（<c>1 + 0.5 × exp(-ageDays / 30)</c>）× 置顶加权（1.3）。
    /// 新旧偏好冲突时，新记忆靠衰减自然胜出，而不是靠条目顺序。
    /// </summary>
    public static double Relevance(MemoryEntry entry, string? query, DateTime now)
    {
        var haystack = Normalize(entry.Text);
        double baseScore = 0;
        var text = query ?? "";

        foreach (Match match in LatinToken.Matches(text.ToLowerInvariant()))
        {
            if (haystack.Contains(match.Value)) baseScore += 3;
        }

        var cjk = text.Where(IsCjk).ToArray();
        for (var index = 0; index + 1 < cjk.Length; index++)
        {
            var gram = new string([cjk[index], cjk[index + 1]]);
            if (haystack.Contains(gram)) baseScore++;
        }

        if (baseScore <= 0) return 0;

        var stamp = entry.UpdatedAt == default ? entry.CreatedAt : entry.UpdatedAt;
        var ageDays = stamp == default ? 0 : Math.Max(0, (now - stamp).TotalDays);
        var recency = 1 + 0.5 * Math.Exp(-ageDays / 30);
        return baseScore * recency * (entry.Pinned ? 1.3 : 1.0);
    }

    private static double Score(MemoryEntry entry, string? query, DateTime now) =>
        Relevance(entry, query, now);

    private static HashSet<string> Tokens(string normalized)
    {
        var tokens = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match match in LatinToken.Matches(normalized))
        {
            if (match.Value.Length >= 2) tokens.Add(match.Value);
        }

        var cjk = normalized.Where(IsCjk).ToArray();
        for (var index = 0; index + 1 < cjk.Length; index++)
        {
            tokens.Add(new string([cjk[index], cjk[index + 1]]));
        }

        return tokens;
    }

    private static int Overlap(HashSet<string> left, HashSet<string> right) =>
        left.Count(right.Contains);

    private static bool IsCjk(char value) =>
        value is >= '\u4E00' and <= '\u9FFF' or >= '\u3400' and <= '\u4DBF';
}
