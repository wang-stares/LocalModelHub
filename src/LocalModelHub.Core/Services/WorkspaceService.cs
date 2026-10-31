using System.Text;
using System.Text.RegularExpressions;
using LocalModelHub.Core.Models;

namespace LocalModelHub.Core.Services;

/// <summary>工作文件夹沙箱：模型的文件操作只能落在这个根目录内。</summary>
public sealed class WorkspaceService
{
    private const long MaxReadBytes = 256 * 1024;
    private const long MaxWriteBytes = 512 * 1024;
    private const int MaxListEntries = 300;
    private const int MaxListDepth = 3;
    private const int DefaultReadLines = 200;
    private const int MaxReadWindow = 400;
    private const int MaxSearchResults = 40;
    private const int MaxSymbolResults = 40;
    private const int MaxContextLines = 3;
    private const int MaxContextResults = 12;
    private const int MaxPatchBytes = 1024 * 1024;
    private const int MaxPatchFiles = 8;
    private const int MaxPatchHunks = 200;
    /// <summary>搜索索引的文件数上限；超过后结果会显式告知模型，而不是静默截断。</summary>
    private const int MaxIndexFiles = 4000;
    /// <summary>索引占用内存上限：超出预算的文件不进缓存，仍按次读盘参与搜索。</summary>
    private const long MaxIndexBytes = 64 * 1024 * 1024;

    private static readonly string[] IgnoredDirectories =
    [
        ".git", ".svn", ".hg", "node_modules", "bin", "obj", "dist", "build", "target", "vendor",
        ".venv", "venv", "__pycache__", ".idea", ".vs", "packages", "coverage", ".next", ".cache"
    ];

    private static readonly string[] BinaryExtensions =
    [
        ".png", ".jpg", ".jpeg", ".gif", ".webp", ".ico", ".bmp", ".pdf", ".zip", ".gz", ".7z", ".rar",
        ".exe", ".dll", ".so", ".dylib", ".pdb", ".class", ".jar", ".gguf", ".bin", ".mp4", ".mov", ".woff", ".woff2"
    ];

    private static readonly StringComparison PathComparison =
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private static readonly Regex HunkHeaderPattern = new(
        @"^@@\s+-(\d+)(?:,(\d+))?\s+\+(\d+)(?:,(\d+))?\s+@@", RegexOptions.Compiled);

    /// <summary>轻量符号定义识别：覆盖 C# / Java / Kotlin / TS / JS / Python / Go / Rust 的常见声明形态。</summary>
    private static readonly Regex[] SymbolPatterns =
    [
        new(@"^(?:[A-Za-z_@][A-Za-z0-9_]*\s+)*(?:class|interface|record|struct|enum|trait|object|module|namespace)\s+([A-Za-z_][A-Za-z0-9_]*)",
            RegexOptions.Compiled),
        new(@"^(?:[A-Za-z_][A-Za-z0-9_]*\s+)*(?:async\s+)?(?:function|def|func|fn)\s+([A-Za-z_][A-Za-z0-9_]*)",
            RegexOptions.Compiled),
        new(@"^(?:export\s+)?(?:const|let|var)\s+([A-Za-z_][A-Za-z0-9_]*)\s*=\s*(?:async\s*)?(?:\([^)]*\)|[A-Za-z_][A-Za-z0-9_]*)\s*=>",
            RegexOptions.Compiled),
        new(@"^(?:[A-Za-z_][A-Za-z0-9_<>\[\],.? ]{0,60})\s+([A-Za-z_][A-Za-z0-9_]*)\s*\(",
            RegexOptions.Compiled)
    ];

    private string? _root;

    // 检索索引：路径 → 「修改时间 + 大小 + 行内容」。目录只枚举一次，未变的文件不重复读盘。
    private readonly object _indexLock = new();
    private readonly Dictionary<string, IndexedFile> _index = new(StringComparer.OrdinalIgnoreCase);
    private string? _indexRoot;
    private long _indexBytes;

    public string? Root => _root;

    public bool IsConfigured => _root is not null && Directory.Exists(_root);

    public void SetRoot(string? path)
    {
        _root = null;
        if (!string.IsNullOrWhiteSpace(path))
        {
            try
            {
                _root = Path.GetFullPath(path.Trim());
            }
            catch
            {
                _root = null;
            }
        }

        // 换工作区时旧索引全部作废，避免把上一个目录的命中带进来。
        lock (_indexLock)
        {
            _index.Clear();
            _indexBytes = 0;
            _indexRoot = null;
        }
    }

    public WorkspaceResult ListFiles(string? relativePath)
    {
        if (!TryResolve(relativePath ?? ".", out var full, out var error)) return new(false, error);

        try
        {
            if (File.Exists(full)) return new(true, $"{Relative(full)}（文件）");
            if (!Directory.Exists(full)) return new(false, $"目录不存在：{Relative(full)}");

            var lines = new List<string>();
            Walk(full, 0, lines);
            return new(true, lines.Count == 0 ? "（空目录）" : string.Join('\n', lines));
        }
        catch (Exception ex)
        {
            return new(false, $"列出目录失败：{ex.Message}");
        }
    }

    public WorkspaceResult ReadFile(string? relativePath) => ReadFile(relativePath, null, null);

    /// <summary>
    /// 按行窗口读取（SWE-agent 式 ACI）：默认只给 200 行并带行号，
    /// 避免一次把长文件灌进小模型的上下文，也方便后续用行号定位修改。
    /// </summary>
    public WorkspaceResult ReadFile(string? relativePath, int? startLine, int? endLine)
    {
        if (!TryResolve(relativePath, out var full, out var error)) return new(false, error);
        if (!File.Exists(full)) return new(false, $"文件不存在：{Relative(full)}");

        var info = new FileInfo(full);
        if (info.Length > MaxReadBytes)
            return new(false, $"文件过大（{Formatters.Bytes(info.Length)}），上限 256 KB");

        string text;
        try
        {
            var bytes = File.ReadAllBytes(full);
            if (Array.IndexOf(bytes, (byte)0) >= 0) return new(false, "二进制文件不支持读取");
            (text, _, _) = DecodeText(bytes);
        }
        catch (Exception ex)
        {
            return new(false, $"读取失败：{ex.Message}");
        }

        var lines = text.Split('\n');
        var total = lines.Length;

        var from = startLine ?? 1;
        if (from > total) return new(false, $"文件只有 {total} 行，start_line 超出范围");

        var to = endLine ?? from + DefaultReadLines - 1;
        if (to < from) to = from;
        to = Math.Min(to, Math.Min(total, from + MaxReadWindow - 1));

        var builder = new StringBuilder();
        for (var index = from; index <= to; index++)
        {
            builder.Append(index).Append('|').Append(' ').AppendLine(lines[index - 1].TrimEnd('\r'));
        }

        var heading = $"{Relative(full)}（第 {from}-{to} 行 / 共 {total} 行）";
        var result = new StringBuilder();
        result.AppendLine(heading).AppendLine("```").Append(builder).AppendLine("```");
        if (to < total)
        {
            result.AppendLine($"（已截断，可指定 start_line / end_line 继续读取，单次最多 {MaxReadWindow} 行）");
        }

        return new(true, result.ToString().TrimEnd());
    }

    /// <summary>
    /// 代码搜索（AutoCodeRover / CodeAgent 式定位）：正则或字面量，返回 路径:行号:片段，
    /// 结果封顶，避免一次搜索灌满上下文。contextLines 可选地在命中行前后展开上下文，
    /// 让模型一次看到足够的局部结构，减少反复 read_file。
    /// </summary>
    public WorkspaceResult Search(string? query, string? include, int maxResults = 24, int contextLines = 0)
    {
        if (!IsConfigured) return new(false, "尚未设置工作文件夹");
        var pattern = (query ?? "").Trim();
        if (pattern.Length == 0) return new(false, "query 不能为空");
        if (pattern.Length > 200) return new(false, "query 过长（上限 200 字符）");

        Regex regex;
        try
        {
            regex = new Regex(pattern, RegexOptions.IgnoreCase, TimeSpan.FromSeconds(2));
        }
        catch (ArgumentException)
        {
            regex = new Regex(Regex.Escape(pattern), RegexOptions.IgnoreCase, TimeSpan.FromSeconds(2));
        }

        var includePattern = NormalizeInclude(include);
        var context = Math.Clamp(contextLines, 0, MaxContextLines);
        var limit = context > 0
            ? Math.Clamp(maxResults, 1, MaxContextResults)
            : Math.Clamp(maxResults, 1, MaxSearchResults);
        var hits = new List<string>();
        var truncated = false;
        var (files, indexTruncated) = SnapshotSourceFiles();

        foreach (var (file, cachedLines) in files)
        {
            if (includePattern is not null &&
                !System.IO.Enumeration.FileSystemName.MatchesSimpleExpression(
                    includePattern, Path.GetFileName(file), ignoreCase: true))
            {
                continue;
            }

            var lines = cachedLines ?? TryReadLines(file);
            if (lines is null) continue;

            for (var index = 0; index < lines.Length; index++)
            {
                bool matched;
                try
                {
                    matched = regex.IsMatch(lines[index]);
                }
                catch (RegexMatchTimeoutException)
                {
                    return new(false, "搜索超时，请缩小 query 范围");
                }

                if (!matched) continue;
                if (hits.Count >= limit)
                {
                    truncated = true;
                    break;
                }

                hits.Add(context > 0
                    ? FormatContextHit(Relative(file), lines, index, context)
                    : FormatHit(Relative(file), lines, index));
            }

            if (truncated) break;
        }

        var notes = BuildSearchNotes(truncated ? $"（已达到 {limit} 条上限，可能还有更多匹配）" : null, indexTruncated);
        if (hits.Count == 0)
        {
            return new(true, notes.Length == 0 ? "没有匹配结果" : $"没有匹配结果\n{notes}");
        }

        var output = string.Join('\n', hits);
        return new(true, notes.Length == 0 ? output : $"{output}\n{notes}");
    }

    /// <summary>搜索 / 符号查找的尾部提示：结果条数上限与文件扫描上限都要显式告知模型。</summary>
    private static string BuildSearchNotes(string? resultLimitNote, bool scanTruncated)
    {
        var notes = new List<string>();
        if (resultLimitNote is not null) notes.Add(resultLimitNote);
        if (scanTruncated) notes.Add($"（已扫描 {MaxIndexFiles} 个文件，超过上限的结果未包含）");
        return string.Join('\n', notes);
    }

    private static string[]? TryReadLines(string fullPath)
    {
        try
        {
            return new FileInfo(fullPath).Length > MaxReadBytes
                ? null
                : File.ReadAllLines(fullPath, new UTF8Encoding(false, false));
        }
        catch
        {
            return null;
        }
    }

    private static string FormatHit(string path, string[] lines, int index)
    {
        var snippet = lines[index].Trim();
        if (snippet.Length > 140) snippet = snippet[..140] + "…";
        return $"{path}:{index + 1}: {snippet}";
    }

    /// <summary>命中行前后各展开若干行，命中行用 |&gt; 标记，方便模型直接定位修改位置。</summary>
    private static string FormatContextHit(string path, string[] lines, int index, int context)
    {
        var from = Math.Max(0, index - context);
        var to = Math.Min(lines.Length - 1, index + context);
        var builder = new StringBuilder();
        builder.AppendLine($"{path}:{index + 1}（第 {from + 1}-{to + 1} 行）");
        for (var current = from; current <= to; current++)
        {
            var snippet = lines[current].TrimEnd();
            if (snippet.Length > 140) snippet = snippet[..140] + "…";
            builder.AppendLine($"{current + 1}|{(current == index ? ">" : " ")}{snippet}");
        }

        return builder.ToString().TrimEnd();
    }

    /// <summary>
    /// 按符号名查找定义（AutoCodeRover / CodeAgent 式定义级检索）：
    /// 只返回类、接口、函数 / 方法等声明行，比全文搜索更少噪音，方便先定位再读取。
    /// </summary>
    public WorkspaceResult FindSymbols(string? query, string? include = null, int maxResults = 20)
    {
        if (!IsConfigured) return new(false, "尚未设置工作文件夹");
        var needle = (query ?? "").Trim();
        if (needle.Length == 0) return new(false, "query 不能为空");
        if (needle.Length > 120) return new(false, "query 过长（上限 120 字符）");

        var includePattern = NormalizeInclude(include);
        var limit = Math.Clamp(maxResults, 1, MaxSymbolResults);
        var hits = new List<string>();
        var truncated = false;
        var (files, indexTruncated) = SnapshotSourceFiles();

        foreach (var (file, cachedLines) in files)
        {
            if (includePattern is not null &&
                !System.IO.Enumeration.FileSystemName.MatchesSimpleExpression(
                    includePattern, Path.GetFileName(file), ignoreCase: true))
            {
                continue;
            }

            var lines = cachedLines ?? TryReadLines(file);
            if (lines is null) continue;

            for (var index = 0; index < lines.Length; index++)
            {
                var symbol = MatchSymbol(lines[index]);
                if (symbol is null || !symbol.Contains(needle, StringComparison.OrdinalIgnoreCase)) continue;
                if (hits.Count >= limit)
                {
                    truncated = true;
                    break;
                }

                var signature = lines[index].Trim();
                if (signature.Length > 160) signature = signature[..160] + "…";
                hits.Add($"{Relative(file)}:{index + 1}: {signature}");
            }

            if (truncated) break;
        }

        var notes = BuildSearchNotes(truncated ? $"（已达到 {limit} 条上限，可能还有更多定义）" : null, indexTruncated);
        if (hits.Count == 0)
        {
            var empty = $"没有找到包含「{needle}」的定义";
            return new(true, notes.Length == 0 ? empty : $"{empty}\n{notes}");
        }

        var output = string.Join('\n', hits);
        return new(true, notes.Length == 0 ? output : $"{output}\n{notes}");
    }

    private static string? MatchSymbol(string line)
    {
        var trimmed = line.Trim();
        if (trimmed.Length == 0 || trimmed.Length > 240) return null;
        if (trimmed.StartsWith("//", StringComparison.Ordinal) ||
            trimmed.StartsWith("/*", StringComparison.Ordinal) ||
            trimmed.StartsWith("*", StringComparison.Ordinal) ||
            trimmed.StartsWith("#", StringComparison.Ordinal))
        {
            return null;
        }

        if (trimmed.StartsWith("return ", StringComparison.Ordinal) ||
            trimmed.StartsWith("new ", StringComparison.Ordinal) ||
            trimmed.StartsWith("throw ", StringComparison.Ordinal) ||
            trimmed.StartsWith("await ", StringComparison.Ordinal) ||
            trimmed.StartsWith("yield ", StringComparison.Ordinal) ||
            trimmed.StartsWith("if (", StringComparison.Ordinal) ||
            trimmed.StartsWith("for (", StringComparison.Ordinal) ||
            trimmed.StartsWith("while (", StringComparison.Ordinal) ||
            trimmed.StartsWith("switch (", StringComparison.Ordinal) ||
            trimmed.StartsWith("catch (", StringComparison.Ordinal))
        {
            return null;
        }

        foreach (var pattern in SymbolPatterns)
        {
            var match = pattern.Match(trimmed);
            if (!match.Success) continue;

            var name = match.Groups[1].Value;
            if (name.Length <= 1) continue;
            if (name is "if" or "for" or "while" or "switch" or "catch" or "foreach" or "lock") continue;
            return name;
        }

        return null;
    }

    /// <summary>从补丁文本提取目标文件路径（只看 +++ 行，跳过 /dev/null），用于写前读取校验。</summary>
    public static IReadOnlyList<string> PatchTargets(string? patch)
    {
        var targets = new List<string>();
        foreach (var raw in (patch ?? "").Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (!line.StartsWith("+++ ", StringComparison.Ordinal)) continue;

            var path = NormalizePatchPath(ExtractPatchPath(line[4..]));
            if (path.Length > 0 && path != "/dev/null") targets.Add(path);
        }

        return targets;
    }

    public WorkspaceResult WriteFile(string? relativePath, string? content)
    {
        if (string.IsNullOrWhiteSpace(relativePath)) return new(false, "缺少 path 参数");
        content ??= "";
        if (content.Contains('\0')) return new(false, "内容包含非法空字符");
        if (Encoding.UTF8.GetByteCount(content) > MaxWriteBytes) return new(false, "内容超过 512 KB 上限");

        if (!TryResolve(relativePath, out var full, out var error)) return new(false, error);
        if (Directory.Exists(full)) return new(false, $"{Relative(full)} 是目录，不能写入");

        try
        {
            var parent = Path.GetDirectoryName(full);
            if (parent is not null) Directory.CreateDirectory(parent);

            // 覆盖已有文件时沿用它的行尾与 BOM；新建文件默认 LF、无 BOM
            var (crlf, bom) = File.Exists(full) ? ProbeTextStyle(full) : (false, false);
            SaveText(full, content, crlf, bom);
            InvalidateIndex(full);
            return new(true, $"已写入 {Relative(full)}（{content.Length} 字符）");
        }
        catch (Exception ex)
        {
            return new(false, $"写入失败：{ex.Message}");
        }
    }

    public WorkspaceResult ReplaceInFile(string? relativePath, string? oldText, string? newText)
    {
        if (string.IsNullOrEmpty(oldText)) return new(false, "old_text 不能为空");
        if (oldText.Contains('\0') || (newText?.Contains('\0') ?? false)) return new(false, "文本包含非法空字符");

        if (!TryResolve(relativePath, out var full, out var error)) return new(false, error);
        if (!File.Exists(full)) return new(false, $"文件不存在：{Relative(full)}");

        var info = new FileInfo(full);
        if (info.Length > MaxReadBytes) return new(false, $"文件过大（{Formatters.Bytes(info.Length)}），上限 256 KB");

        string text;
        bool crlf;
        bool bom;
        try
        {
            (text, crlf, bom) = LoadText(full);
        }
        catch (Exception ex)
        {
            return new(false, $"读取失败：{ex.Message}");
        }

        // 模型看到的是 read_file 的 LF 文本，匹配前把 old_text 也归一化为 LF
        var normalizedOld = oldText.Replace("\r\n", "\n");
        var count = CountOccurrences(text, normalizedOld);
        if (count == 0)
        {
            var totalLines = text.Length == 0 ? 0 : text.Split('\n').Length;
            var oldLines = normalizedOld.Split('\n').Length;
            return new(false,
                $"未在 {Relative(full)} 中找到要替换的文本" +
                $"（文件行尾风格：{(crlf ? "CRLF" : "LF")}，共 {totalLines} 行；old_text 归一化后有 {oldLines} 行）。" +
                "请确认空白、缩进与大小写完全一致，或先用 read_file 重新读取该文件。");
        }

        var normalizedNew = (newText ?? "").Replace("\r\n", "\n");
        var updated = text.Replace(normalizedOld, normalizedNew, StringComparison.Ordinal);
        if (Encoding.UTF8.GetByteCount(updated) > MaxWriteBytes) return new(false, "替换后内容超过 512 KB 上限");

        try
        {
            SaveText(full, updated, crlf, bom);
            InvalidateIndex(full);
            return new(true, $"已在 {Relative(full)} 中替换 {count} 处");
        }
        catch (Exception ex)
        {
            return new(false, $"写入失败：{ex.Message}");
        }
    }

    /// <summary>
    /// 应用统一 diff（SWE-agent / Agentless 式补丁编辑）：支持多文件、多 hunk，
    /// 行号越界直接拒绝，上下文必须精确匹配（允许按唯一上下文重定位）；
    /// 先校验全部 hunk，再统一落盘，任一处失败都不会写入任何文件。
    /// </summary>
    public WorkspaceResult ApplyPatch(string? patch)
    {
        var text = (patch ?? "").Replace("\r\n", "\n");
        if (text.Trim().Length == 0) return new(false, "patch 不能为空");
        if (Encoding.UTF8.GetByteCount(text) > MaxPatchBytes) return new(false, "patch 超过 1 MB 上限");

        if (!TryParsePatch(text, out var files, out var error)) return new(false, error);
        if (files.Count == 0) return new(false, "没有找到 --- / +++ 文件头");
        if (files.Count > MaxPatchFiles) return new(false, $"单次最多应用 {MaxPatchFiles} 个文件");

        var pending = new List<(string FullPath, string Content, bool Crlf, bool Bom)>();
        foreach (var file in files)
        {
            if (!TryResolve(file.Path, out var full, out var resolveError)) return new(false, resolveError);

            string original;
            bool crlf = false;
            bool bom = false;
            if (file.IsNew)
            {
                if (File.Exists(full)) return new(false, $"新建文件已存在：{Relative(full)}");
                original = "";
            }
            else
            {
                if (!File.Exists(full)) return new(false, $"文件不存在：{Relative(full)}");
                var info = new FileInfo(full);
                if (info.Length > MaxReadBytes)
                    return new(false, $"文件过大（{Formatters.Bytes(info.Length)}），上限 256 KB");

                try
                {
                    (original, crlf, bom) = LoadText(full);
                }
                catch (Exception ex)
                {
                    return new(false, $"读取失败：{ex.Message}");
                }
            }

            if (!TryApplyHunks(file, original, out var updated, out error)) return new(false, error);
            if (Encoding.UTF8.GetByteCount(updated) > MaxWriteBytes)
                return new(false, $"补丁应用后 {Relative(full)} 超过 512 KB 上限");

            pending.Add((full, updated, crlf, bom));
        }

        try
        {
            foreach (var (full, content, crlf, bom) in pending)
            {
                var parent = Path.GetDirectoryName(full);
                if (parent is not null) Directory.CreateDirectory(parent);
                SaveText(full, content, crlf, bom);
                InvalidateIndex(full);
            }
        }
        catch (Exception ex)
        {
            return new(false, $"写入失败：{ex.Message}");
        }

        var names = pending.Select((entry, index) => $"{Relative(entry.FullPath)}（{files[index].Hunks.Count} 个 hunk）");
        return new(true, $"已应用补丁：{string.Join('、', names)}");
    }

    private static bool TryParsePatch(string text, out List<PatchFile> files, out string error)
    {
        files = new List<PatchFile>();
        error = "";
        PatchFile? current = null;
        PatchHunk? hunk = null;
        string? oldPath = null;
        var oldConsumed = 0;
        var newConsumed = 0;

        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');

            if (hunk is not null)
            {
                var kind = ' ';
                var content = "";
                if (line.Length == 0)
                {
                    // 少数模型会省略上下文行的前导空格，空行按空上下文处理
                }
                else if (line[0] is ' ' or '-' or '+')
                {
                    kind = line[0];
                    content = line[1..];
                }
                else if (line[0] == '\\')
                {
                    // "\ No newline at end of file" 等标记不参与计数
                    continue;
                }
                else
                {
                    error = $"无法解析 hunk 内容行：{line}";
                    return false;
                }

                if (kind != '+') oldConsumed++;
                if (kind != '-') newConsumed++;
                if (oldConsumed > hunk.OldCount || newConsumed > hunk.NewCount)
                {
                    error = "hunk 行数与头声明不一致：" +
                            $"@@ -{hunk.OldStart},{hunk.OldCount} +{hunk.NewStart},{hunk.NewCount} @@";
                    return false;
                }

                hunk.Lines.Add(new PatchLine(kind, content));
                if (oldConsumed == hunk.OldCount && newConsumed == hunk.NewCount) hunk = null;
                continue;
            }

            if (line.StartsWith("--- "))
            {
                oldPath = ExtractPatchPath(line[4..]);
                continue;
            }

            if (line.StartsWith("+++ "))
            {
                if (oldPath is null)
                {
                    error = "+++ 之前缺少 --- 文件头";
                    return false;
                }

                var newPath = ExtractPatchPath(line[4..]);
                var isNew = oldPath == "/dev/null";
                if (!isNew && newPath == "/dev/null")
                {
                    error = "不支持通过补丁删除文件";
                    return false;
                }

                if (isNew && newPath == "/dev/null")
                {
                    error = "无效补丁：新旧路径都是 /dev/null";
                    return false;
                }

                var normalizedOld = NormalizePatchPath(oldPath);
                var normalizedNew = NormalizePatchPath(newPath);
                if (!isNew && !normalizedOld.Equals(normalizedNew, PathComparison))
                {
                    error = $"--- 与 +++ 路径不一致：{normalizedOld} / {normalizedNew}";
                    return false;
                }

                current = new PatchFile(isNew ? normalizedNew : normalizedOld, isNew, new List<PatchHunk>());
                files.Add(current);
                oldPath = null;
                continue;
            }

            if (line.StartsWith("@@"))
            {
                if (current is null)
                {
                    error = "hunk 出现在文件头之前";
                    return false;
                }

                var match = HunkHeaderPattern.Match(line);
                if (!match.Success)
                {
                    error = $"无法解析 hunk 头：{line}";
                    return false;
                }

                var oldStart = int.Parse(match.Groups[1].Value);
                var oldCount = match.Groups[2].Success ? int.Parse(match.Groups[2].Value) : 1;
                var newStart = int.Parse(match.Groups[3].Value);
                var newCount = match.Groups[4].Success ? int.Parse(match.Groups[4].Value) : 1;
                if (oldCount > 0 && oldStart < 1)
                {
                    error = $"hunk 起始行无效：{line}";
                    return false;
                }

                if (current.Hunks.Count >= MaxPatchHunks)
                {
                    error = $"单个补丁最多 {MaxPatchHunks} 个 hunk";
                    return false;
                }

                hunk = new PatchHunk(oldStart, oldCount, newStart, newCount, new List<PatchLine>());
                current.Hunks.Add(hunk);
                oldConsumed = 0;
                newConsumed = 0;
                if (oldCount == 0 && newCount == 0) hunk = null;
                continue;
            }

            if (line.Length == 0 || line.StartsWith("diff ") || line.StartsWith("index ") ||
                line.StartsWith("new file mode ") || line.StartsWith("deleted file mode ") ||
                line.StartsWith("old mode ") || line.StartsWith("new mode ") ||
                line.StartsWith("similarity index ") || line.StartsWith("rename ") || line.StartsWith("\\ "))
            {
                continue;
            }

            error = $"无法识别的补丁行：{line}";
            return false;
        }

        if (hunk is not null)
        {
            error = "hunk 内容不完整，行数与头声明不一致";
            return false;
        }

        foreach (var file in files)
        {
            if (file.Hunks.Count == 0)
            {
                error = $"{file.Path} 没有任何 hunk";
                return false;
            }
        }

        return true;
    }

    private static bool TryApplyHunks(PatchFile file, string original, out string updated, out string error)
    {
        updated = "";
        error = "";

        var normalized = original.Replace("\r\n", "\n");
        var trailingNewline = !file.IsNew && normalized.EndsWith('\n');

        string[] sourceLines;
        if (file.IsNew || normalized.Length == 0)
        {
            sourceLines = Array.Empty<string>();
        }
        else
        {
            sourceLines = normalized.Split('\n');
            if (trailingNewline) sourceLines = sourceLines[..^1];
        }

        var working = new List<string>(sourceLines);
        var offset = 0;

        foreach (var hunk in file.Hunks)
        {
            var oldBlock = hunk.Lines.Where(line => line.Kind != '+').Select(line => line.Text).ToList();
            var newBlock = hunk.Lines.Where(line => line.Kind != '-').Select(line => line.Text).ToList();
            if (oldBlock.Count != hunk.OldCount || newBlock.Count != hunk.NewCount)
            {
                error = $"hunk 行数与头声明不一致（{file.Path}）";
                return false;
            }

            var expected = hunk.OldStart + offset;
            var target = hunk.OldCount == 0 ? expected : expected - 1;
            if (target < 0 || target > working.Count || target + oldBlock.Count > working.Count)
            {
                error = $"hunk 行号超出 {file.Path} 的范围" +
                        $"（@@ -{hunk.OldStart},{hunk.OldCount} @@，文件共 {working.Count} 行）";
                return false;
            }

            if (oldBlock.Count > 0 && !MatchesAt(working, target, oldBlock))
            {
                var found = FindUniqueBlock(working, oldBlock);
                if (found < 0)
                {
                    var hint = DescribeNearest(working, oldBlock, hunk.OldStart);
                    error = $"hunk 上下文与 {file.Path} 不匹配" +
                            $"（@@ -{hunk.OldStart},{hunk.OldCount} @@）{hint}";
                    return false;
                }

                target = found;
            }

            working.RemoveRange(target, oldBlock.Count);
            working.InsertRange(target, newBlock);
            offset += newBlock.Count - oldBlock.Count;
        }

        updated = string.Join('\n', working);
        if ((trailingNewline || file.IsNew) && updated.Length > 0 && !updated.EndsWith('\n')) updated += "\n";
        return true;
    }

    private static bool MatchesAt(List<string> lines, int index, IReadOnlyList<string> block)
    {
        if (block.Count == 0) return true;
        if (index + block.Count > lines.Count) return false;
        for (var offset = 0; offset < block.Count; offset++)
        {
            if (!lines[index + offset].Equals(block[offset], StringComparison.Ordinal)) return false;
        }

        return true;
    }

    /// <summary>在文件里找唯一匹配的上下文块；找不到或有多个候选都视为失败，避免改错位置。</summary>
    private static int FindUniqueBlock(List<string> lines, IReadOnlyList<string> block)
    {
        var found = -1;
        for (var index = 0; index + block.Count <= lines.Count; index++)
        {
            if (!MatchesAt(lines, index, block)) continue;
            if (found >= 0) return -1;
            found = index;
        }

        return found;
    }

    /// <summary>补丁失败时给出可执行的修复线索：上下文首行出现在哪些行、文件总行数。</summary>
    private static string DescribeNearest(List<string> lines, IReadOnlyList<string> block, int declaredLine)
    {
        var needle = block.FirstOrDefault(line => line.Trim().Length > 0);
        if (needle is null) return "；请 read_file 核对后重新生成补丁";

        var matches = new List<int>();
        for (var index = 0; index < lines.Count && matches.Count < 3; index++)
        {
            if (lines[index].Trim().Equals(needle.Trim(), StringComparison.Ordinal))
            {
                matches.Add(index + 1);
            }
        }

        if (matches.Count == 0)
        {
            return $"；上下文首行未找到，文件共 {lines.Count} 行，请 read_file 核对后重新生成补丁";
        }

        return $"；上下文首行出现在第 {string.Join("、", matches)} 行" +
               $"（补丁声明第 {declaredLine} 行），请先 read_file 再重新生成补丁";
    }

    /// <summary>去掉 git 的 a/、b/ 前缀与时间戳，保留 /dev/null 语义。</summary>
    private static string NormalizePatchPath(string value)
    {
        var path = value.Trim();
        if (path is "/dev/null" or "dev/null") return "/dev/null";
        if (path.StartsWith("a/") || path.StartsWith("b/") || path.StartsWith("i/") || path.StartsWith("w/"))
        {
            path = path[2..];
        }

        return path.Trim();
    }

    private static string ExtractPatchPath(string value)
    {
        var path = value.Trim();
        if (path.StartsWith('"'))
        {
            var end = path.IndexOf('"', 1);
            if (end > 0) return path[1..end];
        }

        var tab = path.IndexOf('\t');
        if (tab > 0) path = path[..tab];
        return path.Trim();
    }

    /// <summary>把模型给出的相对路径解析为沙箱内的绝对路径；越界、绝对路径、符号链接一律拒绝。</summary>
    public bool TryResolve(string? relativePath, out string fullPath, out string error)
    {
        fullPath = "";
        error = "";

        if (!IsConfigured)
        {
            error = "尚未设置工作文件夹";
            return false;
        }

        var relative = string.IsNullOrWhiteSpace(relativePath) ? "." : relativePath.Trim();
        if (relative.Contains('\0'))
        {
            error = "路径包含非法字符";
            return false;
        }

        if (Path.IsPathRooted(relative))
        {
            error = "只允许工作文件夹内的相对路径";
            return false;
        }

        string combined;
        try
        {
            combined = Path.GetFullPath(Path.Combine(_root!, relative));
        }
        catch
        {
            error = "路径无效";
            return false;
        }

        if (!IsWithin(_root!, combined))
        {
            error = $"路径超出工作文件夹：{relative}";
            return false;
        }

        if (ContainsReparsePoint(_root!, combined))
        {
            error = "路径包含符号链接，已拒绝";
            return false;
        }

        fullPath = combined;
        return true;
    }

    private static bool IsWithin(string root, string path)
    {
        var basePath = Path.TrimEndingDirectorySeparator(root);
        var prefix = basePath.EndsWith(Path.DirectorySeparatorChar)
            ? basePath
            : basePath + Path.DirectorySeparatorChar;
        return path.Equals(basePath, PathComparison) ||
               path.StartsWith(prefix, PathComparison);
    }

    /// <summary>检查 root 到 target 之间是否经过符号链接/联接点，阻止通过链接逃逸沙箱。</summary>
    private static bool ContainsReparsePoint(string root, string target)
    {
        var relative = Path.GetRelativePath(root, target);
        if (relative == ".") return false;

        var current = root;
        foreach (var part in relative.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, part);
            if (IsReparsePoint(current)) return true;
        }

        return false;
    }

    private static bool IsReparsePoint(string path)
    {
        try
        {
            return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
        }
        catch (FileNotFoundException)
        {
            return false;
        }
        catch (DirectoryNotFoundException)
        {
            return false;
        }
        catch
        {
            return true;
        }
    }

    private void Walk(string directory, int depth, List<string> lines)
    {
        if (depth > MaxListDepth || lines.Count >= MaxListEntries) return;

        IEnumerable<string> entries;
        try
        {
            entries = Directory.EnumerateFileSystemEntries(directory)
                .OrderBy(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch
        {
            return;
        }

        foreach (var entry in entries)
        {
            if (lines.Count >= MaxListEntries) return;

            if (IsReparsePoint(entry))
            {
                lines.Add($"{Relative(entry)}（链接，已跳过）");
                continue;
            }

            if (Directory.Exists(entry))
            {
                lines.Add($"{Relative(entry)}/");
                Walk(entry, depth + 1, lines);
            }
            else
            {
                long size = 0;
                try
                {
                    size = new FileInfo(entry).Length;
                }
                catch
                {
                    // 忽略无法读取元数据的文件
                }

                lines.Add($"{Relative(entry)}（{Formatters.Bytes(size)}）");
            }
        }
    }

    private string Relative(string fullPath) =>
        Path.GetRelativePath(_root!, fullPath).Replace('\\', '/');

    /// <summary>把 "*.cs" / "cs" / ".cs" 统一成简单通配模式；空值表示不限制。</summary>
    private static string? NormalizeInclude(string? include)
    {
        var value = (include ?? "").Trim();
        if (value.Length == 0) return null;
        if (value.Contains('*')) return value;
        return value.StartsWith('.') ? "*" + value : "*." + value;
    }

    /// <summary>
    /// 搜索前同步索引并返回可搜索文件：路径按序稳定，未变的文件复用缓存行，
    /// 变化或新增的文件按 mtime + 大小重读，删除的文件从索引移除。
    /// 返回的 Lines 为 null 表示超过内存预算或读盘失败，调用方按次现读。
    /// </summary>
    private (List<(string FullPath, string[]? Lines)> Files, bool Truncated) SnapshotSourceFiles()
    {
        lock (_indexLock)
        {
            var root = _root!;
            if (!string.Equals(_indexRoot, root, PathComparison))
            {
                _index.Clear();
                _indexBytes = 0;
                _indexRoot = root;
            }

            var listed = CollectSourceFiles(8, out var truncated);
            // 路径排序保证同样的工作区每次返回一致的命中顺序。
            listed.Sort((left, right) =>
                StringComparer.OrdinalIgnoreCase.Compare(Relative(left), Relative(right)));

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var files = new List<(string, string[]?)>(listed.Count);

            foreach (var fullPath in listed)
            {
                if (!seen.Add(fullPath)) continue;

                FileInfo info;
                try
                {
                    info = new FileInfo(fullPath);
                }
                catch
                {
                    files.Add((fullPath, null));
                    continue;
                }

                if (!info.Exists || info.Length > MaxReadBytes) continue;

                var cached = _index.TryGetValue(fullPath, out var entry) ? entry : null;
                if (cached is not null &&
                    cached.Length == info.Length &&
                    cached.MtimeTicks == info.LastWriteTimeUtc.Ticks)
                {
                    files.Add((fullPath, cached.Lines));
                    continue;
                }

                if (cached is not null) RemoveEntry(fullPath, cached);

                if (_indexBytes + info.Length > MaxIndexBytes)
                {
                    // 超出内存预算：不缓存，本次搜索仍按次读盘。
                    files.Add((fullPath, null));
                    continue;
                }

                try
                {
                    var lines = File.ReadAllLines(fullPath, new UTF8Encoding(false, false));
                    var indexed = new IndexedFile(info.Length, info.LastWriteTimeUtc.Ticks, lines);
                    _index[fullPath] = indexed;
                    _indexBytes += info.Length;
                    files.Add((fullPath, lines));
                }
                catch
                {
                    files.Add((fullPath, null));
                }
            }

            foreach (var stale in _index.Keys.Where(path => !seen.Contains(path)).ToList())
            {
                RemoveEntry(stale, _index[stale]);
            }

            return (files, truncated);
        }
    }

    private void RemoveEntry(string fullPath, IndexedFile entry)
    {
        if (_index.Remove(fullPath)) _indexBytes -= entry.Length;
    }

    /// <summary>写盘成功后让对应文件失效，下一次搜索重新读取；新增文件无需处理。</summary>
    private void InvalidateIndex(string? fullPath)
    {
        if (string.IsNullOrEmpty(fullPath)) return;
        lock (_indexLock)
        {
            if (_index.TryGetValue(fullPath, out var entry)) RemoveEntry(fullPath, entry);
        }
    }

    /// <summary>遍历工作区源码文件：跳过忽略目录、符号链接与二进制扩展名；超过上限时置 truncated。</summary>
    private List<string> CollectSourceFiles(int maxDepth, out bool truncated)
    {
        var files = new List<string>();
        truncated = false;
        if (!IsConfigured) return files;

        var stack = new Stack<(string Directory, int Depth)>();
        stack.Push((_root!, 0));

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
                if (IsReparsePoint(entry)) continue;

                if (Directory.Exists(entry))
                {
                    if (depth + 1 > maxDepth) continue;
                    var name = Path.GetFileName(entry);
                    if (IgnoredDirectories.Contains(name, StringComparer.OrdinalIgnoreCase)) continue;
                    stack.Push((entry, depth + 1));
                    continue;
                }

                var extension = Path.GetExtension(entry);
                if (BinaryExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase)) continue;

                if (files.Count >= MaxIndexFiles)
                {
                    truncated = true;
                    return files;
                }

                files.Add(entry);
            }
        }

        return files;
    }

    private sealed record IndexedFile(long Length, long MtimeTicks, string[] Lines);
    private sealed record PatchLine(char Kind, string Text);
    private sealed record PatchHunk(int OldStart, int OldCount, int NewStart, int NewCount, List<PatchLine> Lines);
    private sealed record PatchFile(string Path, bool IsNew, List<PatchHunk> Hunks);

    /// <summary>解码 UTF-8 文本，返回统一 LF 的正文、原文件是否 CRLF、是否带 BOM。</summary>
    private static (string Text, bool Crlf, bool Bom) DecodeText(byte[] bytes)
    {
        var bom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
        var text = new UTF8Encoding(false, false)
            .GetString(bytes, bom ? 3 : 0, bytes.Length - (bom ? 3 : 0));
        var crlf = text.Contains("\r\n", StringComparison.Ordinal);
        return (text.Replace("\r\n", "\n"), crlf, bom);
    }

    private static (string Text, bool Crlf, bool Bom) LoadText(string fullPath) =>
        DecodeText(File.ReadAllBytes(fullPath));

    /// <summary>按原文件的行尾与 BOM 风格写回，避免把 CRLF 仓库整体改成 LF。</summary>
    private static void SaveText(string fullPath, string text, bool crlf, bool bom)
    {
        var normalized = text.Replace("\r\n", "\n");
        if (crlf) normalized = normalized.Replace("\n", "\r\n");
        File.WriteAllText(fullPath, normalized, new UTF8Encoding(bom));
    }

    private static (bool Crlf, bool Bom) ProbeTextStyle(string fullPath)
    {
        try
        {
            using var stream = File.OpenRead(fullPath);
            var buffer = new byte[8192];
            var read = stream.Read(buffer, 0, buffer.Length);
            var bom = read >= 3 && buffer[0] == 0xEF && buffer[1] == 0xBB && buffer[2] == 0xBF;
            for (var index = bom ? 3 : 0; index + 1 < read; index++)
            {
                if (buffer[index] == (byte)'\r' && buffer[index + 1] == (byte)'\n') return (true, bom);
            }

            return (false, bom);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 风格探测失败时按新建文件处理（LF、无 BOM），不影响内容写入
            return (false, false);
        }
    }

    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }

        return count;
    }
}

public readonly record struct WorkspaceResult(bool Success, string Text);
