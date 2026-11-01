using System.Diagnostics;
using System.Text;

namespace LocalModelHub.Core.Services;

/// <summary>
/// 项目规约注入（P1-7）：按 AGENTS.md → .lmh/rules.md → PLAN.md 的优先级读取，
/// 合并成一个「项目规约」区块，总预算不超过 800 token，放在系统提示末尾。
/// </summary>
public static class ProjectRules
{
    /// <summary>注入预算（近似 token）：中文按 1 token/字，其它按 4 字符/token 估算。</summary>
    public const int MaxTokens = 800;

    public static readonly string[] CandidatePaths = ["AGENTS.md", Path.Combine(".lmh", "rules.md"), "PLAN.md"];

    private const int MaxFileBytes = 512 * 1024;

    /// <summary>按优先级拼出可注入的规约区块；工作区为空、文件缺失或全部为空时返回空串。</summary>
    public static string Build(string? root)
    {
        if (string.IsNullOrWhiteSpace(root)) return "";

        var sections = new List<(string Path, string Text)>();
        var remaining = MaxTokens;
        foreach (var candidate in CandidatePaths)
        {
            if (remaining <= 0) break;

            var fullPath = Path.Combine(root, candidate);
            var text = TryRead(fullPath, remaining, out var usedTokens);
            if (text.Length == 0) continue;

            sections.Add((candidate.Replace('\\', '/'), text));
            remaining -= usedTokens;
        }

        if (sections.Count == 0) return "";

        var builder = new StringBuilder();
        builder.AppendLine("【项目规约】以下规则来自项目根目录，优先于通用习惯；与用户当前指令冲突时以用户指令为准：");
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

    /// <summary>超预算时按行截断，并在末尾标注「已截断」，避免模型以为规则到此结束。</summary>
    private static string TryRead(string fullPath, int budgetTokens, out int usedTokens)
    {
        usedTokens = 0;
        try
        {
            var info = new FileInfo(fullPath);
            if (!info.Exists || info.Length == 0) return "";
            if (info.Length > MaxFileBytes) return "";

            var text = File.ReadAllText(fullPath, Encoding.UTF8).Trim();
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

    private static bool IsCjk(char value) =>
        value is >= '\u4e00' and <= '\u9fff'
            or >= '\u3400' and <= '\u4dbf'
            or >= '\u3040' and <= '\u30ff'
            or >= '\uac00' and <= '\ud7af'
            or >= '\uf900' and <= '\ufaff';
}
