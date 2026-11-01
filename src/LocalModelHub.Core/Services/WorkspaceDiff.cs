using System.Text;

namespace LocalModelHub.Core.Services;

/// <summary>工作区文本快照：相对路径 → 内容。二进制与超大文件不进入快照。</summary>
public sealed record WorkspaceSnapshot(IReadOnlyDictionary<string, string> Files);

/// <summary>由快照差异生成统一 diff，并记录新增 / 修改 / 删除文件与改动行数。</summary>
public sealed record WorkspacePatch(
    string Text,
    IReadOnlyList<string> Added,
    IReadOnlyList<string> Modified,
    IReadOnlyList<string> Removed,
    int AddedLines,
    int RemovedLines)
{
    public int ChangedFiles => Added.Count + Modified.Count + Removed.Count;

    public bool IsEmpty => Text.Length == 0;
}

/// <summary>
/// 不依赖 git 的工作区差异：用于多 Agent 评审。
/// 只比较文本文件，忽略构建产物与二进制，单文件上限与 WorkspaceService 一致。
/// </summary>
public static class WorkspaceDiff
{
    private const int MaxFiles = 3000;
    private const int MaxFileBytes = 256 * 1024;
    private const int ContextLines = 3;

    public static WorkspaceSnapshot Capture(string? root)
    {
        var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) return new(files);

        var rules = WorkspaceRules.ForRoot(root);
        try
        {
            foreach (var file in EnumerateFiles(root, 8, rules))
            {
                if (files.Count >= MaxFiles) break;
                if (rules.IsBinaryFile(file)) continue;

                try
                {
                    if (new FileInfo(file).Length > MaxFileBytes) continue;
                    files[ToRelative(root, file)] = File.ReadAllText(file);
                }
                catch
                {
                    // 单个文件读取失败不影响整体快照
                }
            }
        }
        catch
        {
            // 目录枚举失败时返回已收集的快照
        }

        return new(files);
    }

    public static WorkspacePatch BuildPatch(WorkspaceSnapshot before, WorkspaceSnapshot after)
    {
        var builder = new StringBuilder();
        var added = new List<string>();
        var modified = new List<string>();
        var removed = new List<string>();
        var addedLines = 0;
        var removedLines = 0;

        foreach (var path in after.Files.Keys.OrderBy(key => key, StringComparer.Ordinal))
        {
            if (!before.Files.ContainsKey(path))
            {
                var lines = SplitLines(after.Files[path]);
                // 空文件没有可应用的 hunk，ApplyPatch 无法处理，直接跳过。
                if (lines.Length == 0) continue;
                added.Add(path);
                addedLines += lines.Length;
                AppendAdded(builder, path, lines);
                continue;
            }

            if (string.Equals(before.Files[path], after.Files[path], StringComparison.Ordinal)) continue;

            var oldLines = SplitLines(before.Files[path]);
            var newLines = SplitLines(after.Files[path]);
            var counts = AppendModified(builder, path, oldLines, newLines);
            modified.Add(path);
            addedLines += counts.Added;
            removedLines += counts.Removed;
        }

        foreach (var path in before.Files.Keys.OrderBy(key => key, StringComparer.Ordinal))
        {
            if (after.Files.ContainsKey(path)) continue;
            var lines = SplitLines(before.Files[path]);
            if (lines.Length == 0) continue;
            removed.Add(path);
            removedLines += lines.Length;
            AppendRemoved(builder, path, lines);
        }

        return new WorkspacePatch(
            builder.ToString().TrimEnd('\n'),
            added, modified, removed, addedLines, removedLines);
    }

    private static void AppendAdded(StringBuilder builder, string path, string[] lines)
    {
        builder.Append("--- /dev/null\n");
        builder.Append("+++ b/").Append(path).Append('\n');
        builder.Append("@@ -0,0 +1,").Append(lines.Length).Append(" @@\n");
        foreach (var line in lines) builder.Append('+').Append(line).Append('\n');
    }

    private static void AppendRemoved(StringBuilder builder, string path, string[] lines)
    {
        builder.Append("--- a/").Append(path).Append('\n');
        builder.Append("+++ /dev/null\n");
        builder.Append("@@ -1,").Append(lines.Length).Append(" +0,0 @@\n");
        foreach (var line in lines) builder.Append('-').Append(line).Append('\n');
    }

    private static (int Added, int Removed) AppendModified(
        StringBuilder builder,
        string path,
        string[] oldLines,
        string[] newLines)
    {
        var prefix = 0;
        var shared = Math.Min(oldLines.Length, newLines.Length);
        while (prefix < shared &&
               string.Equals(oldLines[prefix], newLines[prefix], StringComparison.Ordinal))
        {
            prefix++;
        }

        var suffix = 0;
        while (suffix < shared - prefix &&
               string.Equals(oldLines[oldLines.Length - 1 - suffix], newLines[newLines.Length - 1 - suffix],
                   StringComparison.Ordinal))
        {
            suffix++;
        }

        var start = Math.Max(0, prefix - ContextLines);
        var oldEnd = oldLines.Length - suffix;
        var newEnd = newLines.Length - suffix;
        var tail = Math.Min(ContextLines, suffix);
        var added = newEnd - prefix;
        var removed = oldEnd - prefix;

        var oldCount = (prefix - start) + removed + tail;
        var newCount = (prefix - start) + added + tail;

        builder.Append("--- a/").Append(path).Append('\n');
        builder.Append("+++ b/").Append(path).Append('\n');
        builder.Append("@@ -").Append(start + 1).Append(',').Append(oldCount)
            .Append(" +").Append(start + 1).Append(',').Append(newCount).Append(" @@\n");

        for (var index = start; index < prefix; index++)
        {
            builder.Append(' ').Append(oldLines[index]).Append('\n');
        }

        for (var index = prefix; index < oldEnd; index++)
        {
            builder.Append('-').Append(oldLines[index]).Append('\n');
        }

        for (var index = prefix; index < newEnd; index++)
        {
            builder.Append('+').Append(newLines[index]).Append('\n');
        }

        for (var index = oldEnd; index < oldEnd + tail; index++)
        {
            builder.Append(' ').Append(oldLines[index]).Append('\n');
        }

        return (added, removed);
    }

    private static string[] SplitLines(string text)
    {
        var normalized = text.Replace("\r\n", "\n");
        if (normalized.EndsWith('\n')) normalized = normalized[..^1];
        return normalized.Length == 0 ? [] : normalized.Split('\n');
    }

    private static IEnumerable<string> EnumerateFiles(string root, int maxDepth, WorkspaceRules rules)
    {
        var stack = new Stack<(string Path, int Depth)>();
        stack.Push((root, 0));

        while (stack.Count > 0)
        {
            var (directory, depth) = stack.Pop();
            if (depth > maxDepth) continue;

            string[] children;
            try
            {
                children = Directory.GetDirectories(directory);
            }
            catch
            {
                children = [];
            }

            foreach (var child in children)
            {
                if (rules.IsIgnoredDirectory(Path.GetFileName(child))) continue;
                stack.Push((child, depth + 1));
            }

            string[] files;
            try
            {
                files = Directory.GetFiles(directory);
            }
            catch
            {
                files = [];
            }

            foreach (var file in files) yield return file;
        }
    }

    private static string ToRelative(string root, string file)
    {
        var relative = Path.GetRelativePath(root, file);
        return relative.Replace('\\', '/');
    }
}
