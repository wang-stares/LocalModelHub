using System.Text.Json;
using LocalModelHub.Core.Services;
using Xunit;

namespace LocalModelHub.Core.Tests;

/// <summary>
/// 固定回归集（P1-6）：离线回放 10 个小仓库任务，校验 Agent 生命周期与最终文件结果。
/// 每次发版跑一遍，报告里的 pass@1 / 平均轮次 / 平均 token / 平均时延可直接对比。
/// </summary>
public sealed class RegressionSetTests
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true
    };

    [Fact]
    public async Task Fixed_regression_set_passes_offline()
    {
        var tasks = LoadTasks();
        var report = await new AgentRegressionRunner().RunAsync(tasks);

        Assert.Equal(10, report.Results.Count);
        foreach (var result in report.Results)
        {
            Assert.True(result.Passed, result.Detail);
        }

        Assert.Equal(1.0, report.PassAt1);
        Assert.True(report.AverageRounds > 0);
        Assert.True(report.AverageTokens > 0);
        Assert.Contains("pass@1", report.Summary);
    }

    [Fact]
    public async Task Failing_assertion_is_reported_not_hidden()
    {
        var task = new RegressionTask
        {
            Id = "failing-assertion",
            Prompt = "把 a.txt 的内容改成 b",
            Files = new Dictionary<string, string> { ["a.txt"] = "a" },
            Script =
            [
                new RegressionScriptStep { Tool = "read_file", Args = new() { ["path"] = "a.txt" } },
                new RegressionScriptStep
                {
                    Tool = "replace_in_file",
                    Args = new() { ["path"] = "a.txt", ["old_text"] = "a", ["new_text"] = "b" }
                }
            ],
            Assertions = [new RegressionAssertion { Path = "a.txt", Contains = "never-written" }]
        };

        var report = await new AgentRegressionRunner().RunAsync([task]);

        Assert.Equal(0, report.PassAt1);
        Assert.Single(report.Results[0].Failures);
        Assert.Contains("never-written", report.Results[0].Failures[0]);
    }

    private static List<RegressionTask> LoadTasks()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "regression", "tasks.json");
        Assert.True(File.Exists(path), $"找不到回归任务文件：{path}");
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var tasks = document.RootElement.GetProperty("tasks").Deserialize<List<RegressionTask>>(Options);
        Assert.NotNull(tasks);
        return tasks!;
    }
}
