using System.Collections.Concurrent;
using System.Diagnostics;

namespace LocalModelHub.Core.Services;

/// <summary>
/// 工作区遍历规则的唯一来源：忽略目录、二进制后缀。
/// <see cref="WorkspaceService"/> / <see cref="AgentLocalizer"/> / <see cref="WorkspaceDiff"/> 共用同一份清单，
/// 并可从根目录的 .gitignore 派生的目录级规则（仅取「以 / 结尾、无通配符」的模式，忽略否定规则）。
/// </summary>
public sealed class WorkspaceRules
{
    /// <summary>19 项常规忽略目录：VCS 元数据、构建产物与包缓存。</summary>
    public static readonly IReadOnlyList<string> DefaultIgnoredDirectories =
    [
        ".git", ".svn", ".hg", "node_modules", "bin", "obj", "dist", "build", "target", "vendor",
        ".venv", "venv", "__pycache__", ".idea", ".vs", "packages", "coverage", ".next", ".cache"
    ];

    /// <summary>25 项二进制后缀：不进索引、不参与差异比较与检索。</summary>
    public static readonly IReadOnlyList<string> DefaultBinaryExtensions =
    [
        ".png", ".jpg", ".jpeg", ".gif", ".webp", ".ico", ".bmp", ".pdf", ".zip", ".gz", ".7z", ".rar",
        ".exe", ".dll", ".so", ".dylib", ".pdb", ".class", ".jar", ".gguf", ".bin", ".mp4", ".mov", ".woff", ".woff2"
    ];

    /// <summary>.gitignore 派生规则的条目上限，避免异常文件把忽略集合撑大。</summary>
    public const int MaxGitIgnoreEntries = 200;

    private static readonly ConcurrentDictionary<string, CachedRules> Cache =
        new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    /// <summary>不含 .gitignore 派生规则的默认集合。</summary>
    public static WorkspaceRules Default { get; } = new(DefaultIgnoredDirectories, []);

    private readonly HashSet<string> _ignoredDirectories;
    private readonly HashSet<string> _binaryExtensions;

    private WorkspaceRules(IEnumerable<string> ignoredDirectories, IEnumerable<string> extraIgnoredDirectories)
    {
        _ignoredDirectories = new HashSet<string>(ignoredDirectories, StringComparer.OrdinalIgnoreCase);
        _ignoredDirectories.UnionWith(extraIgnoredDirectories);
        _binaryExtensions = new HashSet<string>(DefaultBinaryExtensions, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>忽略目录清单（含 .gitignore 派生项），供诊断与测试查看。</summary>
    public IReadOnlyCollection<string> IgnoredDirectories => _ignoredDirectories;

    public bool IsIgnoredDirectory(string name) => _ignoredDirectories.Contains(name);

    public bool IsBinaryFile(string path) =>
        _binaryExtensions.Contains(Path.GetExtension(path));

    /// <summary>
    /// 取某个根目录适用的规则；.gitignore 存在时按修改时间缓存，改动后自动重建。
    /// 根目录为空或读取失败时退回 <see cref="Default"/>。
    /// </summary>
    public static WorkspaceRules ForRoot(string? root)
    {
        if (string.IsNullOrWhiteSpace(root)) return Default;

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(root);
        }
        catch (Exception ex) when (ex is ArgumentException or PathTooLongException or NotSupportedException)
        {
            Debug.WriteLine($"[WorkspaceRules] 根目录无效：{ex.Message}");
            return Default;
        }

        var stamp = GitIgnoreStamp(fullPath);
        if (Cache.TryGetValue(fullPath, out var cached) && cached.Stamp == stamp) return cached.Rules;

        var extra = stamp is null ? [] : ReadGitIgnoreDirectories(Path.Combine(fullPath, ".gitignore"));
        var rules = extra.Count == 0 ? Default : new WorkspaceRules(DefaultIgnoredDirectories, extra);
        Cache[fullPath] = new CachedRules(stamp, rules);
        return rules;
    }

    /// <summary>解析 .gitignore 行，只保留可判定为「目录名」的模式。</summary>
    public static IReadOnlyList<string> ParseGitIgnoreDirectories(IEnumerable<string> lines)
    {
        var result = new List<string>();
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            // 否定规则无法用目录名单表达，直接跳过（保持默认的「全部忽略」语义）。
            if (line.StartsWith('!')) continue;
            if (!line.EndsWith('/')) continue;

            var name = line[..^1];
            // 「**/foo/」等价于任意层级下的 foo 目录，可直接退化成目录名。
            if (name.StartsWith("**/", StringComparison.Ordinal)) name = name[3..];
            if (name.StartsWith('/')) name = name[1..];
            if (name.Length == 0 || name.Contains('/') || name.Contains('\\')) continue;
            if (name.IndexOfAny(['*', '?', '[', ']']) >= 0) continue;

            result.Add(name);
            if (result.Count >= MaxGitIgnoreEntries) break;
        }

        return result;
    }

    private static IReadOnlyList<string> ReadGitIgnoreDirectories(string path)
    {
        try
        {
            return ParseGitIgnoreDirectories(File.ReadAllLines(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Debug.WriteLine($"[WorkspaceRules] 读取 .gitignore 失败：{ex.Message}");
            return [];
        }
    }

    private static string? GitIgnoreStamp(string root)
    {
        try
        {
            var path = Path.Combine(root, ".gitignore");
            if (!File.Exists(path)) return null;
            var info = new FileInfo(path);
            return $"{info.LastWriteTimeUtc.Ticks}:{info.Length}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Debug.WriteLine($"[WorkspaceRules] 读取 .gitignore 状态失败：{ex.Message}");
            return null;
        }
    }

    private sealed record CachedRules(string? Stamp, WorkspaceRules Rules);
}
