using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace LocalModelHub.Core.Services;

/// <summary>
/// 项目规约注入（P1-7）：按 AGENTS.md / CLAUDE.md / 规则目录等通用约定读取项目自带的协作说明，
/// 合并成一个「项目规约」区块，总预算不超过 800 token，放在系统提示末尾。
/// 发现顺序对齐 Claude Code 与 Codex：工作目录及其上级目录全部参与，上级在前、当前目录在后；
/// 同一目录里 AGENTS.override.md 替代 AGENTS.md，AGENTS.override 这类本地覆盖不写入任何用户文件。
/// </summary>
public static class ProjectRules
{
    /// <summary>注入预算（近似 token）：中文按 1 token/字，其它按 4 字符/token 估算。</summary>
    public const int MaxTokens = 800;

    public static readonly string[] CandidatePaths = ["AGENTS.md", Path.Combine(".lmh", "rules.md"), "PLAN.md"];

    private const int MaxFileBytes = 512 * 1024;
    private const int MaxFiles = 6;
    private const int MaxDepth = 5;
    private const int MaxRulesPerDirectory = 2;

    private static readonly string[] RootFiles = ["CLAUDE.md", "CLAUDE.local.md", ".cursorrules", "GEMINI.md"];
    private static readonly string[] RuleDirectories = [".trae", ".claude", ".cursor"];
    private static readonly string[] RuleExtensions = [".md", ".mdc", ".txt"];

    private static readonly Regex HtmlComment = new(@"<!--[\s\S]*?-->", RegexOptions.Compiled);

    /// <summary>按优先级拼出可注入的规约区块；工作区为空、文件缺失或全部为空时返回空串。</summary>
    public static string Build(string? root)
    {
        if (string.IsNullOrWhiteSpace(root)) return "";

        string normalized;
        try
        {
            normalized = Path.GetFullPath(root.Trim());
        }
        catch
        {
            return "";
        }

        if (!Directory.Exists(normalized)) return "";

        List<(string FullPath, string Label)> paths;
        try
        {
            paths = Collect(normalized);
        }
        catch (IOException)
        {
            return "";
        }
        catch (UnauthorizedAccessException)
        {
            return "";
        }

        if (paths.Count == 0) return "";

        var sections = new List<(string Path, string Text)>();
        var remaining = MaxTokens;
        foreach (var (fullPath, label) in paths)
        {
            if (remaining <= 0) break;

            var text = TryRead(fullPath, remaining, out var usedTokens);
            if (text.Length == 0) continue;

            sections.Add((label, text));
            remaining -= usedTokens;
        }

        if (sections.Count == 0) return "";

        var builder = new StringBuilder();
        builder.AppendLine("【项目规约】以下规则来自项目目录，优先于通用习惯；与用户当前指令冲突时以用户指令为准：");
        foreach (var (path, text) in sections)
        {
            builder.AppendLine($"--- {path} ---");
            builder.AppendLine(text.TrimEnd());
        }

        return builder.ToString().TrimEnd();
    }

    /// <summary>粗略 token 估算：中日韩字符按 1，其余字符按 1/4。</summary>
    public static int EstimateTokens(string? text)
    {
        if (string.IsNullOrEmpty(text)) return 0;

        var cjk = 0;
        foreach (var ch in text)
        {
            if (IsCjk(ch)) cjk++;
        }

        return cjk + (text.Length - cjk + 3) / 4;
    }

    /// <summary>
    /// 从工作目录向上收集规约文件：近处目录优先占用名额，拼接时按「上级 → 当前」的顺序，
    /// 让最贴近当前目录的规则最后被模型读到。
    /// </summary>
    private static List<(string FullPath, string Label)> Collect(string root)
    {
        var collected = new List<(string FullPath, string Label, int Depth)>();
        var directory = root;

        for (var depth = 0; depth < MaxDepth && !string.IsNullOrEmpty(directory); depth++)
        {
            foreach (var file in FilesIn(directory, isRoot: depth == 0))
            {
                collected.Add((file, Path.GetRelativePath(root, file).Replace('\\', '/'), depth));
                if (collected.Count >= MaxFiles) break;
            }

            if (collected.Count >= MaxFiles) break;
            directory = Path.GetDirectoryName(directory);
        }

        return collected
            .Take(MaxFiles)
            .OrderByDescending(item => item.Depth)
            .Select(item => (item.FullPath, item.Label))
            .ToList();
    }

    private static IEnumerable<string> FilesIn(string directory, bool isRoot)
    {
        // 本地覆盖优先：AGENTS.override.md 存在时不再读取同目录的 AGENTS.md。
        var overrideFile = Path.Combine(directory, "AGENTS.override.md");
        var agentsFile = Path.Combine(directory, "AGENTS.md");
        if (File.Exists(overrideFile)) yield return overrideFile;
        else if (File.Exists(agentsFile)) yield return agentsFile;

        foreach (var name in RootFiles)
        {
            var path = Path.Combine(directory, name);
            if (File.Exists(path)) yield return path;
        }

        var copilot = Path.Combine(directory, ".github", "copilot-instructions.md");
        if (File.Exists(copilot)) yield return copilot;

        if (isRoot)
        {
            foreach (var name in CandidatePaths)
            {
                // AGENTS.md 已在上方按 override 规则处理，避免重复注入。
                if (string.Equals(name, "AGENTS.md", StringComparison.OrdinalIgnoreCase)) continue;

                var path = Path.Combine(directory, name);
                if (File.Exists(path)) yield return path;
            }
        }

        foreach (var directoryName in RuleDirectories)
        {
            var rulesDirectory = Path.Combine(directory, directoryName, "rules");
            if (!Directory.Exists(rulesDirectory)) continue;

            var count = 0;
            foreach (var file in Directory.EnumerateFiles(rulesDirectory).Order(StringComparer.OrdinalIgnoreCase))
            {
                if (!RuleExtensions.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase)) continue;
                yield return file;
                if (++count >= MaxRulesPerDirectory) break;
            }
        }
    }

    /// <summary>超预算时按行截断，并在末尾标注「已截断」，避免模型以为规则到此结束。</summary>
    private static string TryRead(string fullPath, int budgetTokens, out int usedTokens)
    {
        usedTokens = 0;
        try
        {
            var info = new FileInfo(fullPath);
            if (!info.Exists || info.Length == 0) return "";
            if (info.Length > MaxFileBytes) return "";

            var text = StripHtmlComments(File.ReadAllText(fullPath, Encoding.UTF8).Trim());
            if (text.Length == 0) return "";

            var tokens = EstimateTokens(text);
            if (tokens <= budgetTokens)
            {
                usedTokens = tokens;
                return text;
            }

            // 超预算：逐行累计，保留能放下的前缀。
            var builder = new StringBuilder();
            foreach (var line in text.Split('\n'))
            {
                var candidate = builder.Length == 0 ? line : builder + "\n" + line;
                if (EstimateTokens(candidate) > budgetTokens)
                {
                    break;
                }

                builder.AppendLine(line);
            }

            var trimmed = builder.ToString().Trim();
            if (trimmed.Length == 0) return "";

            usedTokens = EstimateTokens(trimmed);
            return trimmed + "\n（以上内容因长度限制已截断）";
        }
        catch (Exception ex)
        {
            // 规则文件不可读时不影响 Agent 启动，只记录诊断信息。
            Debug.WriteLine($"[ProjectRules] 读取 {fullPath} 失败：{ex.Message}");
            return "";
        }
    }

    /// <summary>维护者注释（&lt;!-- --&gt;）在注入前剥离，避免占用上下文。</summary>
    private static string StripHtmlComments(string text) =>
        text.Contains("<!--", StringComparison.Ordinal) ? HtmlComment.Replace(text, "").Trim() : text;

    private static bool IsCjk(char value) =>
        value is >= '\u4e00' and <= '\u9fff'
            or >= '\u3400' and <= '\u4dbf'
            or >= '\u3040' and <= '\u30ff'
            or >= '\uac00' and <= '\ud7af'
            or >= '\uf900' and <= '\ufaff';
}
