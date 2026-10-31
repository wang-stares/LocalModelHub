namespace LocalModelHub.Core.Models;

/// <summary>Agent 生命周期阶段：规划 → 执行 → 验收（对应论文中的 Plan / Act / Verify 与生命周期编排）。</summary>
public enum AgentStage
{
    Plan = 0,
    Act = 1,
    Verify = 2
}

/// <summary>
/// 一步 Thought–Action–Result 轨迹：思考来自模型正文，动作是工具调用，结果是真实执行输出。
/// 只记录实际发生的事实，便于验收与复盘，而不是把模型的声称写成证据。
/// </summary>
public sealed record AgentStep(
    AgentStage Stage,
    int Round,
    string Thought,
    string Action,
    string Arguments,
    string Observation,
    bool Success);

/// <summary>规划角色输出的结构化计划；无法解析时 Steps 为空，调用方退回直接执行。</summary>
public sealed record AgentPlan(IReadOnlyList<string> Steps, string Raw)
{
    public static AgentPlan Empty { get; } = new(Array.Empty<string>(), "");
    public bool HasSteps => Steps.Count > 0;
}

/// <summary>验收角色的结论：只依据工具证据，未通过时给具体问题，而不是笼统“有问题”。</summary>
public sealed record AgentVerification(
    bool Passed,
    string Summary,
    IReadOnlyList<string> Issues,
    IReadOnlyList<string> Evidence,
    string Source)
{
    /// <summary>自检不可用时跳过，不阻断任务，也不假装通过。</summary>
    public static AgentVerification Skipped(string reason) => new(true, reason, Array.Empty<string>(), Array.Empty<string>(), "skipped");

    public bool Verified => Source != "skipped";
    public string OutcomeLabel => !Verified ? "跳过" : Passed ? "通过" : "未通过";
}

/// <summary>一次任务的完整轨迹，交给评估器打分并可写入技能沉淀。</summary>
public sealed record AgentTrajectory(
    string Request,
    AgentMode Mode,
    AgentPlan Plan,
    IReadOnlyList<AgentStep> Steps,
    AgentVerification? Verification,
    int Score,
    string ScoreDetail)
{
    public int ToolCalls => Steps.Count(step => step.Stage == AgentStage.Act);
    public int FailedCalls => Steps.Count(step => step.Stage == AgentStage.Act && !step.Success);
    public TrajectoryStats Stats => TrajectoryStats.From(Steps);
}

/// <summary>
/// Thought–Action–Result 轨迹度量（对应 TAR 轨迹研究）：只统计可观测事实，
/// 调用总数、失败率与重复调用次数，用于解释效率扣分与复盘模型行为。
/// </summary>
public sealed record TrajectoryStats(int ToolCalls, int FailedCalls, int DuplicateCalls)
{
    public double FailureRate => ToolCalls == 0 ? 0 : FailedCalls / (double)ToolCalls;

    public string Detail => ToolCalls == 0
        ? "调用 0 · 无动作"
        : $"调用 {ToolCalls} · 失败 {FailedCalls}（{FailureRate:P0}） · 重复 {DuplicateCalls}";

    public static TrajectoryStats From(IReadOnlyList<AgentStep> steps)
    {
        var actions = steps.Where(step => step.Stage == AgentStage.Act).ToList();
        var keys = actions.Select(step => $"{step.Action}|{step.Arguments.Trim()}").ToList();
        return new TrajectoryStats(
            actions.Count,
            actions.Count(step => !step.Success),
            keys.Count - keys.Distinct(StringComparer.Ordinal).Count());
    }
}

/// <summary>轨迹评估结果：分数 + 各维度拆解，便于解释「为什么不是满分」。</summary>
public sealed record AgentEvaluation(int Score, string Detail);
