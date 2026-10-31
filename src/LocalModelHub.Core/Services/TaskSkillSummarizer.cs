using System.Text.RegularExpressions;

namespace LocalModelHub.Core.Services;

using LocalModelHub.Core.Models;

/// <summary>
/// 把一次完成的任务（用户请求 + 工具调用记录）总结成可复用技能（规则版）。
/// 没有产生实际动作（读取/写入/命令）时不沉淀，避免把闲聊存成技能。
/// </summary>
public static class TaskSkillSummarizer
{
    private const int MaxSteps = 8;
    private const int MaxStepLength = 70;

    private static readonly Regex Whitespace = new(@"\s+", RegexOptions.Compiled);
    private static readonly Regex TrailingPunctuation = new(@"[。！？!?，,；;：:]+$", RegexOptions.Compiled);

    /// <summary>生命周期阶段与记忆写入不算可复用动作，不写进技能步骤。</summary>
    private static readonly HashSet<string> NonActionNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "remember", "plan", "verify", "repair", "localize"
    };

    public static SkillDraft? Summarize(
        string? request,
        IEnumerable<(string Name, string Summary)> activities,
        int maxSteps = MaxSteps)
    {
        var steps = activities
            .Where(item => !NonActionNames.Contains(item.Name))
            .Select(item => FormatStep(item.Name, item.Summary))
            .Where(step => step.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(Math.Clamp(maxSteps, 1, 12))
            .ToList();

        if (steps.Count == 0) return null;

        var ask = Whitespace.Replace((request ?? "").Trim(), " ");
        ask = TrailingPunctuation.Replace(ask, "");
        if (ask.Length == 0) return null;

        var name = Shorten(ask, 30);
        var trigger = $"当用户提出类似请求：{Shorten(ask, 90)}";
        var numbered = string.Join("\n", steps.Select((step, index) => $"{index + 1}. {step}"));
        return new SkillDraft(name, trigger, numbered);
    }

    private static string FormatStep(string name, string summary)
    {
        var text = Whitespace.Replace((summary ?? "").Trim(), " ");
        if (text.Length == 0) return "";

        // 工具过程消息已经附带「动作 · 目标」，直接截断即可；这里只补全缺失的动作前缀。
        if (text.Length > MaxStepLength) text = text[..MaxStepLength];

        var action = name switch
        {
            "write_file" => "写入",
            "replace_in_file" => "修改",
            "apply_patch" => "补丁",
            "find_symbols" => "查定义",
            "read_file" => "读取",
            "list_files" => "列出",
            "search_code" => "搜索",
            "run_command" => "运行",
            _ => ""
        };

        if (action.Length > 0 && !text.StartsWith(action, StringComparison.Ordinal) &&
            !text.Contains(" · ", StringComparison.Ordinal))
        {
            return $"{action} {text}";
        }

        return text;
    }

    private static string Shorten(string text, int limit) =>
        text.Length <= limit ? text : text[..limit].TrimEnd(' ', '，', ',') + "…";
}
