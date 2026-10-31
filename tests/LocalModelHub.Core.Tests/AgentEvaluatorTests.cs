using LocalModelHub.Core.Models;
using LocalModelHub.Core.Services;
using Xunit;

namespace LocalModelHub.Core.Tests;

public class AgentEvaluatorTests
{
    [Fact]
    public void Verified_run_with_read_before_write_scores_full()
    {
        var steps = new[]
        {
            Act("read_file", "a.txt"),
            Act("write_file", "a.txt")
        };
        var verification = new AgentVerification(true, "完成", Array.Empty<string>(), ["a.txt"], "json");

        var evaluation = AgentEvaluator.Evaluate(steps, verification);

        Assert.Equal(100, evaluation.Score);
        Assert.Contains("证据 20/20", evaluation.Detail);
    }

    [Fact]
    public void Write_without_reading_first_loses_evidence_points()
    {
        var evaluation = AgentEvaluator.Evaluate(
            [Act("write_file", "a.txt")],
            new AgentVerification(true, "完成", Array.Empty<string>(), Array.Empty<string>(), "json"));

        Assert.Equal(80, evaluation.Score);
        Assert.Contains("证据 0/20", evaluation.Detail);
    }

    [Fact]
    public void Duplicate_calls_lose_efficiency_points()
    {
        var duplicate = Act("write_file", "a.txt");
        var steps = new[]
        {
            Act("read_file", "a.txt"),
            duplicate,
            duplicate with { Success = false, Observation = "重复调用已拦截" }
        };

        var evaluation = AgentEvaluator.Evaluate(
            steps,
            new AgentVerification(true, "完成", Array.Empty<string>(), Array.Empty<string>(), "json"));

        Assert.True(evaluation.Score < 100);
        Assert.Contains("效率 7/15", evaluation.Detail);
    }

    [Fact]
    public void Failed_verification_drops_verify_points()
    {
        var steps = new[] { Act("read_file", "a.txt"), Act("write_file", "a.txt") };
        var evaluation = AgentEvaluator.Evaluate(
            steps,
            new AgentVerification(false, "未通过", ["缺少验收"], Array.Empty<string>(), "json"));

        Assert.Equal(60, evaluation.Score);
        Assert.Contains("验收 0/40", evaluation.Detail);
    }

    [Fact]
    public void Skipped_verification_scores_partial_not_full()
    {
        var steps = new[] { Act("read_file", "a.txt"), Act("write_file", "a.txt") };
        var evaluation = AgentEvaluator.Evaluate(steps, AgentVerification.Skipped("服务不可用"));

        Assert.Equal(76, evaluation.Score);
        Assert.Contains("验收 16/40", evaluation.Detail);
    }

    [Fact]
    public void No_actions_scores_zero()
    {
        var evaluation = AgentEvaluator.Evaluate(Array.Empty<AgentStep>(), null);

        Assert.Equal(0, evaluation.Score);
    }

    [Fact]
    public void Trajectory_stats_report_failures_and_duplicates()
    {
        var steps = new[]
        {
            Act("read_file", "a.txt"),
            Act("read_file", "a.txt", success: false),
            Act("write_file", "a.txt")
        };

        var stats = TrajectoryStats.From(steps);

        Assert.Equal(3, stats.ToolCalls);
        Assert.Equal(1, stats.FailedCalls);
        Assert.Equal(1, stats.DuplicateCalls);
        Assert.Equal(1.0 / 3, stats.FailureRate, 3);
        Assert.Contains("失败 1", stats.Detail);
        Assert.Contains("重复 1", stats.Detail);
    }

    [Fact]
    public void Evaluation_detail_includes_trajectory_stats_without_changing_score()
    {
        var withFailure = new[]
        {
            Act("read_file", "a.txt"),
            Act("write_file", "a.txt"),
            Act("read_file", "b.txt", success: false)
        };
        var verification = new AgentVerification(true, "完成", Array.Empty<string>(), ["a.txt"], "json");

        var evaluation = AgentEvaluator.Evaluate(withFailure, verification);

        Assert.Contains("轨迹 调用 3 · 失败 1", evaluation.Detail);
        Assert.Contains("执行 17/25", evaluation.Detail);
        Assert.Contains("效率 15/15", evaluation.Detail);
        Assert.Equal(92, evaluation.Score);
    }

    [Fact]
    public void Apply_patch_counts_as_write_and_requires_prior_read()
    {
        var patch = """
            --- a/a.txt
            +++ b/a.txt
            @@ -1,1 +1,1 @@
            -old
            +new
            """;
        var patchJson = $$"""{"patch":{{System.Text.Json.JsonSerializer.Serialize(patch)}}}""";

        var afterRead = AgentEvaluator.Evaluate(
            [
                Act("read_file", "a.txt"),
                new AgentStep(AgentStage.Act, 1, "", "apply_patch", patchJson, "已应用补丁", true)
            ],
            new AgentVerification(true, "完成", Array.Empty<string>(), ["a.txt"], "json"));
        Assert.Equal(100, afterRead.Score);

        var withoutRead = AgentEvaluator.Evaluate(
            [new AgentStep(AgentStage.Act, 1, "", "apply_patch", patchJson, "已应用补丁", true)],
            new AgentVerification(true, "完成", Array.Empty<string>(), ["a.txt"], "json"));
        Assert.Contains("证据 0/20", withoutRead.Detail);
        Assert.Equal(80, withoutRead.Score);
    }

    private static AgentStep Act(string action, string path, bool success = true) =>
        new(AgentStage.Act, 1, "", action, $$"""{"path":"{{path}}"}""", "ok", success);
}
