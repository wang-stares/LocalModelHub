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
    private const int ReviewDiffChars = 3000;

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

    public MultiAgentCoordinator(
        IChatCompletion server,
        WorkspaceService workspace,
        MemoryService memory,
        bool allowMemoryWrites,
        bool allowCommands = false,
        ICommandRunner? commands = null,
        int maxRounds = 2)
    {
        _server = server;
        _workspace = workspace;
        _memory = memory;
        _allowMemoryWrites = allowMemoryWrites;
        _allowCommands = allowCommands;
        _commands = commands ?? new CommandService();
        _maxRounds = Math.Clamp(maxRounds, 1, 3);
    }

    public async Task<MultiAgentResult> RunAsync(
        IReadOnlyList<ChatMessage> history,
        string? memoryContext,
        Action<ToolActivity>? onActivity,
        CancellationToken cancellationToken)
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
            var agent = new WorkspaceAgent(
                _server, _workspace, _memory, AgentMode.Goal,
                _allowMemoryWrites, _allowCommands, _commands);
            coder = await agent.RunAsync(coderHistory, coderMemory, onActivity, cancellationToken, plan);
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
            prompt.AppendLine(files.Count == 0 ? "（补丁为空，Coder 没有真实修改文件）" :
                "变更文件：" + string.Join("、", files.Take(12)));
            if (!patch.IsEmpty)
            {
                prompt.AppendLine("补丁预览（可能截断）：");
                prompt.AppendLine("```diff");
                prompt.AppendLine(patch.Text.Length <= ReviewDiffChars
                    ? patch.Text
                    : patch.Text[..ReviewDiffChars] + "\n…（补丁已截断）");
                prompt.AppendLine("```");
            }

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

    private static string FirstLine(string? text, int limit)
    {
        var line = (text ?? "").Split('\n', 2)[0].Trim();
        return line.Length <= limit ? line : line[..limit] + "…";
    }
}
