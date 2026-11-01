using System.Text;
using System.Text.RegularExpressions;

namespace LocalModelHub.Core.Services;

/// <summary>工作区里与请求相关的代码线索：路径、命中次数与首行片段。</summary>
public sealed record LocalizedFile(string Path, int Hits, string Snippet, bool IsTest = false)
{
    /// <summary>给模型看的路径标注，测试文件单独提示，避免把实现文件和测试文件混淆。</summary>
    public string Label => IsTest ? $"{Path}（测试文件）" : Path;
}

public sealed record AgentLocalization(IReadOnlyList<LocalizedFile> Files)
{
    public static AgentLocalization Empty { get; } = new(Array.Empty<LocalizedFile>());

    public string Format()
    {
        if (Files.Count == 0) return "";
        var builder = new StringBuilder("【相关代码线索】按请求关键词在工作区中匹配到的文件（仅供参考，仍需读取确认）：\n");
        for (var index = 0; index < Files.Count; index++)
        {
            var file = Files[index];
            builder.AppendLine($"{index + 1}. {file.Label}（命中 {file.Hits} 次）: {file.Snippet}");
        }

        return builder.ToString().TrimEnd();
    }

    public string Summary() => string.Join("、", Files.Take(3).Select(file => file.Path));
}

/// <summary>
/// 规则版代码定位（AutoCodeRover / Agentless 的轻量本地化）：
/// 从请求里抽出文件名、标识符与引号片段，一次遍历工作区按命中次数排序，
/// 把最相关的几个文件放进规划上下文，避免小模型在仓库里盲搜。
/// </summary>
public static class AgentLocalizer
{
    private const int MaxTokens = 8;
    private const int MaxFiles = 5;
    private const int MaxFileBytes = 256 * 1024;
    private const int MaxScannedFiles = 3000;

    private static readonly Regex FileTokenPattern = new(
        @"[A-Za-z0-9_./\\-]+\.(?:cs|ts|tsx|js|jsx|py|go|rs|java|kt|kts|rb|php|md|json|yaml|yml|toml|xml|csproj|sln|html|css|scss|sql|sh|ps1|gradle)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex IdentifierPattern = new(
        @"[A-Za-z_][A-Za-z0-9_]{2,}", RegexOptions.Compiled);

    private static readonly Regex QuotedPattern = new(
        "[\"`']([^\"`'\\n]{2,60})[\"`']", RegexOptions.Compiled);

    private static readonly Regex TestPathPattern = new(
        @"(^|[/._-])(test|tests|spec|specs|__tests__)([/._-]|$)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "the", "and", "for", "with", "this", "that", "from", "into", "code", "file", "files", "test", "tests",
        "add", "fix", "update", "make", "use", "using", "when", "then", "should", "please", "can", "could",
        "would", "all", "any", "must", "need", "change", "implement", "function", "method", "class", "return",
        "import", "public", "private", "void", "string", "var", "let", "const", "def", "self", "new", "true",
        "false", "null", "none", "bug", "issue", "error", "run", "build", "project", "repo", "read", "write"
    };

    public static AgentLocalization Localize(string? root, string? request, int maxFiles = MaxFiles)
    {
        if (string.IsNullOrWhiteSpace(root) || string.IsNullOrWhiteSpace(request)) return AgentLocalization.Empty;
        if (!Directory.Exists(root)) return AgentLocalization.Empty;

        var tokens = ExtractTokens(request);
        if (tokens.Count == 0) return AgentLocalization.Empty;

        var scored = new Dictionary<string, (int Score, int Hits, string Snippet, int Order)>(StringComparer.OrdinalIgnoreCase);
        var scanned = 0;

        foreach (var file in EnumerateFiles(root, 8))
        {
            if (++scanned > MaxScannedFiles) break;

            string text;
            try
            {
                if (new FileInfo(file).Length > MaxFileBytes) continue;
                text = File.ReadAllText(file);
            }
            catch
            {
                continue;
            }

            var score = 0;
            var hits = 0;
            var snippet = "";
            foreach (var token in tokens)
            {
                var count = 0;
                var index = 0;
                string? firstLine = null;
                while (count < 30 && (index = text.IndexOf(token.Text, index, StringComparison.OrdinalIgnoreCase)) >= 0)
                {
                    count++;
                    if (firstLine is null)
                    {
                        var lineStart = text.LastIndexOf('\n', Math.Max(0, index - 1));
                        var lineEnd = text.IndexOf('\n', index);
                        var line = text[(lineStart + 1)..(lineEnd < 0 ? text.Length : lineEnd)].Trim();
                        if (line.Length > 120) line = line[..120] + "…";
                        firstLine = line;
                    }

                    index += token.Text.Length;
                }

                if (count == 0) continue;
                hits += count;
                score += count * token.Weight;
                if (snippet.Length == 0 && firstLine is not null) snippet = firstLine;
            }

            if (score <= 0) continue;

            var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            scored[relative] = (score, hits, snippet, scored.Count);
        }

        if (scored.Count == 0) return AgentLocalization.Empty;

        var files = scored
            .OrderByDescending(pair => pair.Value.Score)
            .ThenByDescending(pair => pair.Value.Hits)
            .ThenBy(pair => pair.Value.Order)
            .Take(Math.Clamp(maxFiles, 1, 8))
            .Select(pair => new LocalizedFile(
                pair.Key, pair.Value.Hits, pair.Value.Snippet, IsTestPath(pair.Key)))
            .ToList();

        return new AgentLocalization(files);
    }

    /// <summary>路径是否指向测试文件（目录 test/tests/spec/specs 或 xxx.test.cs / XxxTests.cs 之类的命名）。</summary>
    public static bool IsTestPath(string path)
    {
        var normalized = (path ?? "").Replace('\\', '/');
        if (TestPathPattern.IsMatch(normalized)) return true;

        var name = Path.GetFileNameWithoutExtension(normalized);
        return name.EndsWith("Tests", StringComparison.Ordinal) ||
               name.EndsWith("Test", StringComparison.Ordinal) ||
               name.EndsWith("Specs", StringComparison.Ordinal) ||
               name.EndsWith("Spec", StringComparison.Ordinal);
    }

    /// <summary>文件名与引号片段权重更高，普通标识符次之；最多取 8 个词。</summary>
    public static IReadOnlyList<SearchToken> ExtractTokens(string? request)
    {
        var text = request ?? "";
        var tokens = new List<SearchToken>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(string value, int weight)
        {
            var token = value.Trim().Trim('"', '\'', '`').Trim();
            if (token.Length < 3 || token.Length > 80) return;
            if (!seen.Add(token)) return;
            tokens.Add(new SearchToken(token, weight));
        }

        var fileTokens = FileTokenPattern.Matches(text)
            .Take(4)
            .Select(match => match.Value)
            .ToList();
        foreach (var fileToken in fileTokens) Add(fileToken, 4);

        foreach (Match match in QuotedPattern.Matches(text).Take(3)) Add(match.Groups[1].Value, 3);
        foreach (Match match in IdentifierPattern.Matches(text))
        {
            if (tokens.Count >= MaxTokens) break;
            if (StopWords.Contains(match.Value)) continue;

            // 文件名里的片段（src / app / cs）不再单独作为搜索词，避免噪音。
            if (fileTokens.Any(file => file.Contains(match.Value, StringComparison.OrdinalIgnoreCase))) continue;
            Add(match.Value, 2);
        }

        return tokens.Take(MaxTokens).ToList();
    }

    private static IEnumerable<string> EnumerateFiles(string root, int maxDepth)
    {
        var rules = WorkspaceRules.ForRoot(root);
        var stack = new Stack<(string Directory, int Depth)>();
        stack.Push((root, 0));

        while (stack.Count > 0)
        {
            var (directory, depth) = stack.Pop();
            IEnumerable<string> entries;
            try
            {
                entries = Directory.EnumerateFileSystemEntries(directory).ToList();
            }
            catch
            {
                continue;
            }

            foreach (var entry in entries)
            {
                try
                {
                    if ((File.GetAttributes(entry) & FileAttributes.ReparsePoint) != 0) continue;
                }
                catch
                {
                    continue;
                }

                if (Directory.Exists(entry))
                {
                    if (depth + 1 > maxDepth) continue;
                    var name = Path.GetFileName(entry);
                    if (rules.IsIgnoredDirectory(name)) continue;
                    stack.Push((entry, depth + 1));
                    continue;
                }

                if (rules.IsBinaryFile(entry)) continue;
                yield return entry;
            }
        }
    }
}

/// <summary>从请求中抽出的搜索词与权重。</summary>
public sealed record SearchToken(string Text, int Weight);
