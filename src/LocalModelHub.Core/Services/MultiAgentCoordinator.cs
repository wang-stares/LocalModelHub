using System.Text;
using LocalModelHub.Core.Models;

namespace LocalModelHub.Core.Services;

/// <summary>协作角色的一次输出，便于界面或报告展示谁做了什么。</summary>
public sealed record MultiAgentRoleOutput(string Role, string Text);

/// <summary>多 Agent 协作结果：最后一个 Coder 结果、独立评审结论、最终补丁与各角色输出。</summary>
public sealed record MultiAgentResult(
    string Text,
    IReadOnlyList<ToolActivity> Activities,
    AgentPlan Plan,
    AgentVerification? Review,
    AgentResult? Coder,
    string Patch,
    IReadOnlyList<MultiAgentRoleOutput> Roles);

/// <summary>
/// 单模型多角色串行协作（ChatDev / AgileCoder 的本地化落地）：
/// Planner 出计划 → Coder 用工作区工具执行 → Reviewer 独立评审补丁；
/// 评审未通过时把具体问题交回 Coder 修复，轮数有上限，全部串行以避免并发写冲突。
/// 角色之间共享工作区，但各自使用独立上下文，避免同一会话里的自我确认偏差。
/// </summary>
public sealed class MultiAgentCoordinator
{
    private const int PlannerTokens = 512;
    private const int ReviewerTokens = 700;
    private const int MaxDigestFiles = 12;
    private const int MaxHunksPerFile = 3;

    private static readonly string ReviewerSystemPrompt = """
        你是独立评审角色（Reviewer），与负责改代码的 Coder 分离。
        只依据工具轨迹和真实补丁判断，不相信 Coder 的自我声明，也不要给出泛泛的建议。
        只输出 JSON，不要输出其它文字：
        {"pass": true/false, "summary": "一句话结论", "issues": ["具体问题"], "evidence": ["文件名或工具调用"]}
        """;

    private readonly IChatCompletion _server;
    private readonly WorkspaceService _workspace;
    private readonly MemoryService _memory;
    private readonly bool _allowMemoryWrites;
    private readonly bool _allowCommands;
    private readonly ICommandRunner _commands;
    private readonly int _maxRounds;
    private readonly bool _textToolProtocol;

    public MultiAgentCoordinator(
        IChatCompletion server,
        WorkspaceService workspace,
        MemoryService memory,
        bool allowMemoryWrites,
        bool allowCommands = false,
        ICommandRunner? commands = null,
        int maxRounds = 2,
        bool textToolProtocol = false)
    {
        _server = server;
        _workspace = workspace;
        _memory = memory;
        _allowMemoryWrites = allowMemoryWrites;
        _allowCommands = allowCommands;
        _commands = commands ?? new CommandService();
        _maxRounds = Math.Clamp(maxRounds, 1, 3);
        _textToolProtocol = textToolProtocol;
    }

    public async Task<MultiAgentResult> RunAsync(
        IReadOnlyList<ChatMessage> history,
        string? memoryContext,
        Action<ToolActivity>? onActivity,
        CancellationToken cancellationToken,
        Action<string>? onDelta = null)
    {
        if (!_workspace.IsConfigured) throw new InvalidOperationException("请先设置工作文件夹。");

        var root = _workspace.Root!;
        var request = history
            .LastOrDefault(message => message.Role == "user" && !string.IsNullOrWhiteSpace(message.Content))
            ?.Content.Trim() ?? "";

        var activities = new List<ToolActivity>();
        var roles = new List<MultiAgentRoleOutput>();

        var before = await Task.Run(() => WorkspaceDiff.Capture(root), cancellationToken);

        // 1）Planner：只出计划，不碰文件。
        var planText = "";
        var plan = AgentPlan.Empty;
        try
        {
            var turn = await _server.ChatAsync(
                [
                    ChatMessage.System(AgentProtocol.BuildPlanSystemPrompt() +
                        "\n你是多 Agent 协作中的 Planner，输出计划后交给 Coder 执行。"),
                    ChatMessage.User(AgentProtocol.BuildPlanRequest(request))
                ],
                null, PlannerTokens, cancellationToken);
            planText = turn.Content.Trim();
            plan = AgentProtocol.ParsePlan(planText);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            planText = $"规划失败：{ex.Message}";
        }

        roles.Add(new MultiAgentRoleOutput("planner", planText));
        var plannerSummary = plan.HasSteps
            ? $"{plan.Steps.Count} 步 · {FirstLine(planText, 80)}"
            : $"未产出结构化计划 · {FirstLine(planText, 70)}";
        Emit(new ToolActivity("planner", plannerSummary));

        // 2）Coder：复用工作区 Agent，带上 Planner 的计划，避免重复规划。
        var baseMemory = string.IsNullOrWhiteSpace(memoryContext)
            ? ""
            : memoryContext.Trim() + "\n\n";
        var coderMemory = baseMemory +
            "【协作上下文】你是多 Agent 协作中的 Coder，按 Planner 的计划执行；完成后由独立的 Reviewer 评审。";
        var coderHistory = history
            .Where(message => message.Role is "user" or "assistant" && !string.IsNullOrWhiteSpace(message.Content))
            .ToList();

        AgentResult? coder = null;
        AgentVerification? review = null;
        WorkspacePatch patch = new("", [], [], [], 0, 0);

        for (var round = 1; round <= _maxRounds; round++)
        {
            // selfVerify: false —— 自检交给独立的 Reviewer，避免同一份改动被两套口径评审、Token 翻倍。
            var agent = new WorkspaceAgent(
                _server, _workspace, _memory, AgentMode.Goal,
                _allowMemoryWrites, _allowCommands, _commands,
                textToolProtocol: _textToolProtocol, selfVerify: false);
            coder = await agent.RunAsync(
                coderHistory, coderMemory, onActivity, cancellationToken, plan, onDelta);
            roles.Add(new MultiAgentRoleOutput("coder", coder.Text));

            var after = await Task.Run(() => WorkspaceDiff.Capture(root), cancellationToken);
            patch = WorkspaceDiff.BuildPatch(before, after);

            // 3）Reviewer：看轨迹 + 补丁，独立给出结论。
            var reviewText = await ReviewAsync(request, plan, coder, patch, cancellationToken);
            if (reviewText is null)
            {
                review = AgentVerification.Skipped("评审服务不可用");
                Emit(new ToolActivity("review", "评审跳过 · 服务不可用"));
                break;
            }

            review = AgentProtocol.ParseVerification(reviewText);
            roles.Add(new MultiAgentRoleOutput("reviewer", reviewText));
            Emit(new ToolActivity("review",
                review.Verified
                    ? $"{review.OutcomeLabel} · {FirstLine(review.Summary, 80)}"
                    : $"评审跳过 · {FirstLine(review.Summary, 70)}"));

            if (!review.Verified) break;
            if (review.Passed) break;
            if (round >= _maxRounds) break;
            if (patch.Removed.Count > 0)
            {
                Emit(new ToolActivity("review", "补丁包含删除文件 · 不再自动返工"));
                break;
            }

            Emit(new ToolActivity("repair", "评审未通过 · 把问题交回 Coder"));
            var ledger = AgentProtocol.SummarizeSteps(coder.Trajectory?.Steps ?? [], 600);
            coderHistory = history
                .Where(message => message.Role is "user" or "assistant" && !string.IsNullOrWhiteSpace(message.Content))
                .ToList();
            coderHistory.Add(ChatMessage.Assistant(coder.Text));
            coderHistory.Add(ChatMessage.User(
                AgentProtocol.BuildRepairPrompt(review) +
                "\n上一轮工具轨迹摘要：\n" + ledger +
                "\n修改前请重新 read_file 最新内容，只修复评审列出的问题。"));
            coderMemory = baseMemory +
                "【协作上下文】你是多 Agent 协作中的 Coder，正在根据 Reviewer 的具体问题返工；不要重复已成功的改动。";
        }

        var text = coder?.Text.Trim() ?? "";
        if (text.Length == 0) text = "（协作执行完成，但 Coder 没有返回文字总结）";
        if (review is { Verified: true, Passed: false })
        {
            text += $"\n\n（评审未通过：{review.Summary}）";
        }
        else if (review is { Verified: false })
        {
            text += "\n\n（评审未完成，本次结果没有经过独立评审）";
        }

        return new MultiAgentResult(text, activities, plan, review, coder, patch.Text, roles);

        void Emit(ToolActivity activity)
        {
            activities.Add(activity);
            onActivity?.Invoke(activity);
        }
    }

    private async Task<string?> ReviewAsync(
        string request,
        AgentPlan plan,
        AgentResult coder,
        WorkspacePatch patch,
        CancellationToken cancellationToken)
    {
        try
        {
            var files = patch.Added.Concat(patch.Modified).Concat(patch.Removed).ToList();
            var prompt = new StringBuilder();
            prompt.AppendLine(AgentProtocol.BuildVerifyPrompt(request, plan, coder.Trajectory?.Steps ?? []));
            prompt.AppendLine();
            prompt.AppendLine($"真实补丁：{files.Count} 个文件，+{patch.AddedLines} / -{patch.RemovedLines} 行。");
            prompt.AppendLine(files.Count == 0 ? "（补丁为空，Coder 没有真实修改文件）" : "变更清单与关键 hunk：");
            if (!patch.IsEmpty) prompt.AppendLine(BuildChangeDigest(patch));

            var turn = await _server.ChatAsync(
                [
                    ChatMessage.System(ReviewerSystemPrompt),
                    ChatMessage.User(prompt.ToString().TrimEnd())
                ],
                null, ReviewerTokens, cancellationToken);
            return turn.Content.Trim();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 用「文件清单 + 每文件行数统计 + 关键 hunk 头」替代整段 diff 预览：
    /// Reviewer 需要的是改动范围与位置，而不是把 3000 字符补丁塞进上下文。
    /// </summary>
    private static string BuildChangeDigest(WorkspacePatch patch)
    {
        var builder = new StringBuilder();
        var entries = new List<(string Path, int Added, int Removed, List<string> Hunks, List<string> Samples)>();
        (string Path, int Added, int Removed, List<string> Hunks, List<string> Samples)? current = null;

        void Flush()
        {
            if (current is { } entry) entries.Add(entry);
            current = null;
        }

        foreach (var raw in patch.Text.Replace("\r\n", "\n").Split('\n'))
        {
            if (raw.StartsWith("+++ ", StringComparison.Ordinal))
            {
                Flush();
                var path = raw[4..].Trim();
                if (path.StartsWith("b/", StringComparison.Ordinal)) path = path[2..];
                if (path == "/dev/null")
                {
                    // 删除文件：路径在上面的 --- 行里，解析不到就用 Removed 列表兜底
                    continue;
                }

                current = (path, 0, 0, new List<string>(), new List<string>());
                continue;
            }

            if (raw.StartsWith("--- ", StringComparison.Ordinal) && current is null)
            {
                var path = raw[4..].Trim();
                if (path.StartsWith("a/", StringComparison.Ordinal)) path = path[2..];
                if (path.Length > 0) current = (path, 0, 0, new List<string>(), new List<string>());
                continue;
            }

            if (current is not { } file) continue;

            if (raw.StartsWith("@@", StringComparison.Ordinal))
            {
                if (file.Hunks.Count < MaxHunksPerFile) file.Hunks.Add(raw.Trim());
                current = file with { Hunks = file.Hunks };
                continue;
            }

            if (raw.StartsWith('+') && !raw.StartsWith("+++", StringComparison.Ordinal))
            {
                file.Added++;
                if (file.Samples.Count < 2) file.Samples.Add("+" + TrimSample(raw[1..]));
            }
            else if (raw.StartsWith('-') && !raw.StartsWith("---", StringComparison.Ordinal))
            {
                file.Removed++;
                if (file.Samples.Count < 2) file.Samples.Add("-" + TrimSample(raw[1..]));
            }

            current = file;
        }

        Flush();

        // 删除文件不会带 +++ 行，用 Removed 列表补齐，保证清单完整。
        foreach (var removed in patch.Removed.Where(path => entries.All(entry => entry.Path != path)))
        {
            entries.Add((removed, 0, 0, [], []));
        }

        if (entries.Count == 0) return "（没有可解析的补丁内容）";

        foreach (var entry in entries.Take(MaxDigestFiles))
        {
            var hunks = entry.Hunks.Count > 0
                ? $"；hunk：{string.Join(" / ", entry.Hunks)}"
                : "";
            builder.AppendLine($"- {entry.Path}：+{entry.Added} / -{entry.Removed}，{entry.Hunks.Count} 个 hunk{hunks}");
            foreach (var sample in entry.Samples)
            {
                builder.AppendLine($"  示例：{sample}");
            }
        }

        if (entries.Count > MaxDigestFiles)
        {
            builder.AppendLine($"- …（共 {entries.Count} 个文件，仅列前 {MaxDigestFiles} 个）");
        }

        return builder.ToString().TrimEnd();
    }

    private static string TrimSample(string text)
    {
        var trimmed = text.Trim();
        return trimmed.Length <= 80 ? trimmed : trimmed[..80] + "…";
    }

    private static string FirstLine(string? text, int limit)
    {
        var line = (text ?? "").Split('\n', 2)[0].Trim();
        return line.Length <= limit ? line : line[..limit] + "…";
    }
}
