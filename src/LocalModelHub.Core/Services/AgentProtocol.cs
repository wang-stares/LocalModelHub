using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using LocalModelHub.Core.Models;

namespace LocalModelHub.Core.Services;

/// <summary>
/// 规划 / 执行 / 验收三角色的协议层（SEMAP 式角色契约在单模型会话内的落地）：
/// 用显式标记与结构化输出固定「思考 - 行动 - 结果」轨迹，避免小模型在长对话里跑偏，
/// 并让验收只看工具证据，而不是模型自己的声称。
/// </summary>
public static class AgentProtocol
{
    public const string PlanMarker = "[PLAN]";
    public const string ExecuteMarker = "[EXECUTE]";
    public const string VerifyMarker = "[VERIFY]";
    public const string RepairMarker = "[REPAIR]";
    public const string ContextMarker = "[CONTEXT]";

    private const int MaxPlanSteps = 8;
    private const int MaxPlanStepLength = 140;
    private const int MaxLedgerChars = 1400;
    private const int MaxIssueLines = 4;

    private static readonly Regex StepLinePattern = new(
        @"^\s*(?:[-*•·]\s*|\(?\d+\)?[.、)）．:：]\s*|第\s*\d+\s*步[：:.]?\s*|步骤\s*\d+\s*[：:.]?\s*)(.*)$",
        RegexOptions.Compiled);

    private static readonly Regex HeadingOnlyPattern = new(
        @"^(?:计划|方案|步骤|流程|执行计划|规划)\s*[：:]?\s*$",
        RegexOptions.Compiled);

    private static readonly Regex MarkdownNoisePattern = new(@"```|\*\*|__", RegexOptions.Compiled);
    private static readonly Regex FailureHintPattern = new(
        @"未通过|不通过|没有通过|失败|缺少|缺失|遗漏|未完成|未修改|未写入|无证据",
        RegexOptions.Compiled);
    private static readonly Regex IssueLinePattern = new(
        @"问题|缺失|遗漏|未完成|未通过|失败|缺少",
        RegexOptions.Compiled);

    /// <summary>规划角色的系统提示：只出计划，不调用工具，避免和执行阶段抢预算。</summary>
    public static string BuildPlanSystemPrompt() => $"""
        {PlanMarker} 你是规划角色（Planner）。
        只输出可执行计划，不调用工具、不写代码、不复述模型能力。
        计划中的每一步必须能在工作文件夹里通过读取或修改文件完成。
        """;

    /// <summary>规划请求：把用户任务转换成编号计划。</summary>
    public static string BuildPlanRequest(string request) => $"""
        【任务】{(request ?? "").Trim()}
        请按下面格式输出，最多 6 步，每步一行：
        1. 需要先确认的事实或文件
        2. 后续修改步骤
        ...
        最后一步写明验收标准。
        只输出编号计划，不要输出其它内容。
        """;

    /// <summary>把模型输出解析成编号步骤；无法识别的行（寒暄、标题、代码块）直接丢弃。</summary>
    public static AgentPlan ParsePlan(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return AgentPlan.Empty;

        var steps = new List<string>();
        foreach (var raw in text.Split('\n'))
        {
            var line = MarkdownNoisePattern.Replace(raw.Trim(), "").Trim();
            if (line.Length == 0 || HeadingOnlyPattern.IsMatch(line)) continue;

            var match = StepLinePattern.Match(line);
            if (!match.Success) continue;

            var step = match.Groups[1].Value.Trim();
            step = step.TrimStart('-', '–', '—').Trim();
            if (step.Length == 0 || HeadingOnlyPattern.IsMatch(step)) continue;
            if (step.Length > MaxPlanStepLength) step = step[..MaxPlanStepLength] + "…";

            steps.Add(step);
            if (steps.Count >= MaxPlanSteps) break;
        }

        return steps.Count == 0 ? AgentPlan.Empty : new AgentPlan(steps, text.Trim());
    }

    /// <summary>把计划渲染成可注入执行上下文的文本。</summary>
    public static string FormatPlan(AgentPlan plan) =>
        string.Join('\n', plan.Steps.Select((step, index) => $"{index + 1}. {step}"));

    /// <summary>
    /// 验收提示：只允许依据工具轨迹判断，要求输出 JSON；
    /// 证据不足、只改回答没改文件、未读先改都算未通过。
    /// </summary>
    public static string BuildVerifyPrompt(
        string request,
        AgentPlan plan,
        IReadOnlyList<AgentStep> steps)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"{VerifyMarker} 你现在是验收角色（Verifier）。只依据下面的工具轨迹判断，不允许臆测，也不要相信回答里的自我声明。");
        builder.AppendLine($"原始请求：{(request ?? "").Trim()}");
        builder.AppendLine();
        builder.AppendLine("执行计划：");
        builder.AppendLine(plan.HasSteps ? FormatPlan(plan) : "（模型没有给出结构化计划）");
        builder.AppendLine();
        builder.AppendLine("工具轨迹（Thought → Action → Result）：");
        builder.AppendLine(SummarizeSteps(steps));
        builder.AppendLine();
        builder.AppendLine("检查清单：");
        builder.AppendLine("1. 请求中的每一项要求是否真的完成；");
        builder.AppendLine("2. 涉及改动的文件是否由 write_file / replace_in_file / apply_patch 真实写入，而不是只在回答里声称；");
        builder.AppendLine("3. 是否存在未读取就修改、或用旧内容覆盖新内容的情况；");
        builder.AppendLine("4. 回答里是否有工具结果支撑不了的结论。");
        builder.AppendLine("5. 若运行了构建 / 测试命令（run_command），退出码必须为 0；失败输出要么已修复，要么在回答里明确说明未通过。");
        builder.AppendLine("任一项无法由工具轨迹证明时，pass 必须为 false，并在 issues 里写明缺什么证据。");
        builder.AppendLine("只输出 JSON，不要输出其它文字：");
        builder.AppendLine("""{"pass": true/false, "summary": "一句话结论", "issues": ["具体问题"], "evidence": ["文件名或工具调用"]}""");
        return builder.ToString().TrimEnd();
    }

    /// <summary>修复提示：只针对验收列出的问题，避免模型推倒重来。</summary>
    public static string BuildRepairPrompt(AgentVerification verification)
    {
        var issues = verification.Issues.Count > 0
            ? string.Join('\n', verification.Issues.Select((issue, index) => $"{index + 1}. {issue}"))
            : "1. 验收未通过，但模型没有列出具体问题；请重新核对原始请求与工具结果。";

        return $"""
            {RepairMarker} 上一轮验收未通过，请只修复下列问题，不要重复已经成功的调用：
            {issues}
            修复后不要重新解释全部过程，用一两句话说明改动与验收结果。
            """;
    }

    /// <summary>
    /// 解析验收输出：优先解析 JSON；模型只给文字时按关键词兜底，并标记来源，
    /// 便于界面区分「结构化验收」与「文本判断」，避免把兜底结果当成严格结论。
    /// </summary>
    public static AgentVerification ParseVerification(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return AgentVerification.Skipped("模型没有返回自检内容");

        var payload = ExtractJsonObject(text);
        if (payload is not null)
        {
            try
            {
                using var document = JsonDocument.Parse(payload);
                var root = document.RootElement;
                if (root.ValueKind == JsonValueKind.Object && TryReadPass(root, out var passed))
                {
                    var summary = ReadString(root, "summary");
                    var issues = ReadStringArray(root, "issues");
                    var evidence = ReadStringArray(root, "evidence");
                    return new AgentVerification(
                        passed,
                        summary.Length > 0 ? summary : passed ? "自检通过" : "自检未通过",
                        issues,
                        evidence,
                        "json");
                }
            }
            catch (JsonException)
            {
                // 落到文本兜底
            }
        }

        var body = text.Trim();
        var issuesFromText = body.Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.Length > 0 && IssueLinePattern.IsMatch(line))
            .Take(MaxIssueLines)
            .ToList();

        var passedByText = !FailureHintPattern.IsMatch(body);
        return new AgentVerification(
            passedByText,
            FirstLine(body, 120),
            issuesFromText,
            Array.Empty<string>(),
            "text");
    }

    /// <summary>
    /// 工具轨迹台账：只保留路径、成功/失败与结果首行，控制长度，避免把大段文件内容塞进验收上下文。
    /// </summary>
    public static string SummarizeSteps(IReadOnlyList<AgentStep> steps, int maxChars = MaxLedgerChars)
    {
        var builder = new StringBuilder();
        var actions = steps.Where(step => step.Stage == AgentStage.Act).ToList();
        if (actions.Count == 0) return "（没有工具调用）";

        foreach (var step in actions)
        {
            var line = $"- 第{step.Round}轮 {step.Action}({ShortArguments(step.Arguments)}) → " +
                       $"{(step.Success ? "成功" : "失败")}：{FirstLine(step.Observation, 90)}";
            if (builder.Length + line.Length > maxChars)
            {
                builder.AppendLine("- …（轨迹过长，已截断）");
                break;
            }

            builder.AppendLine(line);
        }

        return builder.ToString().TrimEnd();
    }

    /// <summary>提取参数里的 path，避免把完整文件内容带进验收提示。</summary>
    public static string ShortArguments(string? arguments)
    {
        var text = (arguments ?? "").Trim();
        if (text.Length == 0) return "";
        try
        {
            using var document = JsonDocument.Parse(text);
            if (document.RootElement.ValueKind == JsonValueKind.Object)
            {
                var path = ReadString(document.RootElement, "path");
                if (path.Length > 0) return path;
                var command = ReadString(document.RootElement, "command");
                if (command.Length > 0) return FirstLine(command, 60);
                var other = ReadString(document.RootElement, "text");
                if (other.Length > 0) return FirstLine(other, 40);
            }
        }
        catch (JsonException)
        {
            // 非 JSON 参数按原文压缩
        }

        return FirstLine(text, 60);
    }

    /// <summary>
    /// 计划完整性检查（规划阶段的轻量验收）：只要求四个必要部分，缺哪项就报哪项，
    /// 不额外调用模型，避免为了自检再消耗一次上下文。
    /// </summary>
    public static IReadOnlyList<string> MissingPlanSections(string? text)
    {
        var body = text ?? "";
        var missing = new List<string>();
        if (!body.Contains("目标", StringComparison.Ordinal)) missing.Add("目标");
        if (!body.Contains("步骤", StringComparison.Ordinal) &&
            !body.Contains("计划", StringComparison.Ordinal) &&
            !body.Contains("流程", StringComparison.Ordinal))
        {
            missing.Add("步骤");
        }

        if (!body.Contains("风险", StringComparison.Ordinal)) missing.Add("风险");
        if (!body.Contains("验收", StringComparison.Ordinal)) missing.Add("验收");
        return missing;
    }

    private static string? ExtractJsonObject(string text)
    {
        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        if (start < 0 || end <= start) return null;
        return text[start..(end + 1)];
    }

    private static bool TryReadPass(JsonElement root, out bool passed)
    {
        passed = false;
        if (!root.TryGetProperty("pass", out var element)) return false;

        switch (element.ValueKind)
        {
            case JsonValueKind.True:
                passed = true;
                return true;
            case JsonValueKind.False:
                passed = false;
                return true;
            case JsonValueKind.String:
                var value = (element.GetString() ?? "").Trim().ToLowerInvariant();
                if (value is "true" or "yes" or "pass" or "通过" or "是")
                {
                    passed = true;
                    return true;
                }

                if (value is "false" or "no" or "fail" or "未通过" or "否")
                {
                    passed = false;
                    return true;
                }

                return false;
            default:
                return false;
        }
    }

    private static string ReadString(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var element)) return "";
        return element.ValueKind switch
        {
            JsonValueKind.String => (element.GetString() ?? "").Trim(),
            JsonValueKind.Array => string.Join("；", ReadStringArray(root, name)),
            _ => ""
        };
    }

    private static IReadOnlyList<string> ReadStringArray(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var element)) return Array.Empty<string>();

        if (element.ValueKind == JsonValueKind.String)
        {
            var single = (element.GetString() ?? "").Trim();
            return single.Length == 0 ? Array.Empty<string>() : [single];
        }

        if (element.ValueKind != JsonValueKind.Array) return Array.Empty<string>();

        var items = new List<string>();
        foreach (var item in element.EnumerateArray())
        {
            var value = item.ValueKind == JsonValueKind.String ? (item.GetString() ?? "").Trim() : "";
            if (value.Length > 0) items.Add(value);
            if (items.Count >= MaxIssueLines) break;
        }

        return items;
    }

    private static string FirstLine(string? text, int limit)
    {
        var line = (text ?? "").Split('\n', 2)[0].Trim();
        return line.Length <= limit ? line : line[..limit] + "…";
    }
}
