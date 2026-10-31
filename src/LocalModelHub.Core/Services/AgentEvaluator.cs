using LocalModelHub.Core.Models;

namespace LocalModelHub.Core.Services;

/// <summary>
/// 轨迹评估（论文中的评估框架在本地小模型场景的轻量落地）：
/// 只按可观测事实打分——验收结论、工具成功率、改动前是否读过原文、有没有绕圈子的重复调用。
/// 满分 100：验收 40 + 执行 25 + 证据 20 + 效率 15。
/// </summary>
public static class AgentEvaluator
{
    private const int VerifyPoints = 40;
    private const int ExecutePoints = 25;
    private const int EvidencePoints = 20;
    private const int EfficiencyPoints = 15;

    public static AgentEvaluation Evaluate(
        IReadOnlyList<AgentStep> steps,
        AgentVerification? verification)
    {
        var actions = steps.Where(step => step.Stage == AgentStage.Act).ToList();

        var successPoints = actions.Count == 0
            ? 0
            : (int)Math.Round(ExecutePoints * actions.Count(step => step.Success) / (double)actions.Count);

        var verifyPoints = verification switch
        {
            null => 0,
            { Verified: false } => (int)Math.Round(VerifyPoints * 0.4), // 自检不可用时给部分分，但不当作通过
            { Passed: true } => VerifyPoints,
            _ => 0
        };

        var writes = actions
            .Where(step => step.Success && step.Action is "write_file" or "replace_in_file" or "apply_patch")
            .ToList();
        var evidencePoints = EvidencePoints;
        if (actions.Count == 0)
        {
            evidencePoints = 0;
        }
        else if (writes.Count > 0)
        {
            var covered = writes.Count(write => HasPriorRead(actions, write));
            evidencePoints = (int)Math.Round(EvidencePoints * covered / (double)writes.Count);
        }

        // 重复调用被拦截后仍然留在轨迹里，作为「绕圈子」的扣分依据；没有动作谈不上效率。
        var stats = TrajectoryStats.From(steps);
        var efficiencyPoints = actions.Count == 0 ? 0 : Math.Max(0, EfficiencyPoints - stats.DuplicateCalls * 8);

        var score = Math.Clamp(verifyPoints + successPoints + evidencePoints + efficiencyPoints, 0, 100);
        var detail = $"验收 {verifyPoints}/{VerifyPoints} · 执行 {successPoints}/{ExecutePoints} · " +
                     $"证据 {evidencePoints}/{EvidencePoints} · 效率 {efficiencyPoints}/{EfficiencyPoints} · " +
                     $"轨迹 {stats.Detail}";
        return new AgentEvaluation(score, detail);
    }

    /// <summary>改动之前是否成功读过同一路径，用于区分「读了再改」与「盲改」。</summary>
    private static bool HasPriorRead(IReadOnlyList<AgentStep> actions, AgentStep write)
    {
        var path = PathOf(write);
        if (path.Length == 0) return false;

        var writeIndex = IndexOf(actions, write);
        if (writeIndex <= 0) return false;

        for (var index = 0; index < writeIndex; index++)
        {
            var candidate = actions[index];
            if (!candidate.Success || candidate.Action != "read_file") continue;
            if (PathOf(candidate).Equals(path, StringComparison.OrdinalIgnoreCase)) return true;
        }

        return false;
    }

    private static int IndexOf(IReadOnlyList<AgentStep> actions, AgentStep step)
    {
        for (var index = 0; index < actions.Count; index++)
        {
            if (ReferenceEquals(actions[index], step)) return index;
        }

        return -1;
    }

    private static string PathOf(AgentStep step)
    {
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(
                string.IsNullOrWhiteSpace(step.Arguments) ? "{}" : step.Arguments);
            if (document.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object &&
                document.RootElement.TryGetProperty("path", out var path) &&
                path.ValueKind == System.Text.Json.JsonValueKind.String)
            {
                return (path.GetString() ?? "").Trim();
            }

            if (document.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object &&
                document.RootElement.TryGetProperty("patch", out var patch) &&
                patch.ValueKind == System.Text.Json.JsonValueKind.String)
            {
                return PatchPath(patch.GetString());
            }
        }
        catch (System.Text.Json.JsonException)
        {
            // 参数不是合法 JSON 时无法核对路径，按未读处理
        }

        return "";
    }

    /// <summary>从统一 diff 的 +++ 行提取目标路径，用于核对「改前是否读过」。</summary>
    private static string PatchPath(string? patch)
    {
        foreach (var raw in (patch ?? "").Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (!line.StartsWith("+++ ") && !line.StartsWith("--- ")) continue;

            var path = line[4..].Trim();
            var tab = path.IndexOf('\t');
            if (tab > 0) path = path[..tab];
            if (path is "/dev/null" or "dev/null") continue;
            if (path.StartsWith("a/") || path.StartsWith("b/")) path = path[2..];
            return path.Trim();
        }

        return "";
    }
}
