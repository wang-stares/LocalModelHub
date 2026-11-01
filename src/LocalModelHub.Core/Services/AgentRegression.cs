using System.Diagnostics;
using System.Text.Json;
using LocalModelHub.Core.Models;

namespace LocalModelHub.Core.Services;

/// <summary>回归任务里的一条断言：读取相对路径的文件，检查存在性与内容。</summary>
public sealed class RegressionAssertion
{
    public string Path { get; set; } = "";
    public string? Contains { get; set; }
    public string? NotContains { get; set; }
}

/// <summary>回归脚本的一步：调用工具（Tool + Args）或输出文本（Text）。</summary>
public sealed class RegressionScriptStep
{
    public string? Tool { get; set; }
    public Dictionary<string, string>? Args { get; set; }
    public string? Text { get; set; }
}

/// <summary>
/// 一条固定回归任务：初始小仓库文件、用户请求、脚本化模型回合与最终断言。
/// 任务只依赖工作区与 Agent 生命周期，不需要真实模型即可离线跑。
/// </summary>
public sealed class RegressionTask
{
    public string Id { get; set; } = "";
    public string Prompt { get; set; } = "";
    public Dictionary<string, string> Files { get; set; } = [];
    public List<RegressionScriptStep> Script { get; set; } = [];
    public List<RegressionAssertion> Assertions { get; set; } = [];
}

public sealed record RegressionTaskResult(
    string Id,
    bool Passed,
    int Rounds,
    int EstimatedTokens,
    double DurationMs,
    IReadOnlyList<string> Failures)
{
    public string Detail => Passed
        ? $"{Id} · 通过 · {Rounds} 轮 · ≈{EstimatedTokens} token · {DurationMs:0} ms"
        : $"{Id} · 失败：{string.Join("；", Failures)}";
}

/// <summary>一次回归集运行的汇总指标：pass@1 / 平均轮次 / 平均 token / 平均时延。</summary>
public sealed record RegressionReport(IReadOnlyList<RegressionTaskResult> Results)
{
    public double PassAt1 => Results.Count == 0 ? 0 : Results.Count(result => result.Passed) / (double)Results.Count;
    public double AverageRounds => Results.Count == 0 ? 0 : Results.Average(result => result.Rounds);
    public double AverageTokens => Results.Count == 0 ? 0 : Results.Average(result => result.EstimatedTokens);
    public double AverageLatencyMs => Results.Count == 0 ? 0 : Results.Average(result => result.DurationMs);

    public string Summary =>
        $"pass@1 {PassAt1:P0}（{Results.Count(result => result.Passed)}/{Results.Count}）" +
        $" · 平均轮次 {AverageRounds:0.0} · 平均 token ≈{AverageTokens:0}" +
        $" · 平均时延 {AverageLatencyMs:0} ms";

    public string ToJson()
    {
        var payload = new
        {
            passAt1 = PassAt1,
            averageRounds = AverageRounds,
            averageTokens = AverageTokens,
            averageLatencyMs = AverageLatencyMs,
            tasks = Results.Select(result => new
            {
                id = result.Id,
                passed = result.Passed,
                rounds = result.Rounds,
                estimatedTokens = result.EstimatedTokens,
                durationMs = result.DurationMs,
                failures = result.Failures
            })
        };
        return JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });
    }
}

/// <summary>
/// 固定回归集执行器（P1-6）：为每条任务准备临时小仓库，用脚本化模型驱动
/// WorkspaceAgent 的完整生命周期，再按断言校验真实文件结果。
/// </summary>
public sealed class AgentRegressionRunner
{
    private readonly string? _workRoot;

    public AgentRegressionRunner(string? workRoot = null) => _workRoot = workRoot;

    public async Task<RegressionReport> RunAsync(
        IReadOnlyList<RegressionTask> tasks,
        CancellationToken cancellationToken = default)
    {
        var results = new List<RegressionTaskResult>();
        foreach (var task in tasks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            results.Add(await RunTaskAsync(task, cancellationToken));
        }

        return new RegressionReport(results);
    }

    public async Task<RegressionTaskResult> RunTaskAsync(
        RegressionTask task,
        CancellationToken cancellationToken = default)
    {
        var root = Path.Combine(
            _workRoot ?? Path.GetTempPath(),
            $"lmh-regression-{task.Id}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);

        var failures = new List<string>();
        var scripted = new ScriptedRegressionChat(task.Script);
        var stopwatch = Stopwatch.StartNew();
        try
        {
            foreach (var (path, content) in task.Files)
            {
                var full = Path.Combine(root, path);
                var parent = Path.GetDirectoryName(full);
                if (parent is not null) Directory.CreateDirectory(parent);
                await File.WriteAllTextAsync(full, content, cancellationToken);
            }

            var workspace = new WorkspaceService();
            workspace.SetRoot(root);
            var memory = new MemoryService(Path.Combine(root, ".memory.json"));
            var agent = new WorkspaceAgent(
                scripted,
                workspace,
                memory,
                AgentMode.Goal,
                allowMemoryWrites: false,
                allowCommands: false);

            var result = await agent.RunAsync(
                [ChatMessage.User(task.Prompt)], null, null, cancellationToken);
            stopwatch.Stop();

            foreach (var assertion in task.Assertions)
            {
                Check(root, assertion, failures);
            }

            var rounds = result.Trajectory?.Steps.Count(step => step.Stage == AgentStage.Act) ?? scripted.ToolCalls;
            return new RegressionTaskResult(
                task.Id,
                failures.Count == 0,
                rounds,
                scripted.EstimatedTokens,
                stopwatch.Elapsed.TotalMilliseconds,
                failures);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            stopwatch.Stop();
            failures.Add($"执行异常：{ex.Message}");
            Debug.WriteLine($"[Regression] {task.Id} 执行失败：{ex}");
            return new RegressionTaskResult(
                task.Id, false, scripted.ToolCalls, scripted.EstimatedTokens,
                stopwatch.Elapsed.TotalMilliseconds, failures);
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Debug.WriteLine($"[Regression] 清理临时目录失败：{ex.Message}");
            }
        }
    }

    private static void Check(string root, RegressionAssertion assertion, List<string> failures)
    {
        if (string.IsNullOrWhiteSpace(assertion.Path)) return;
        var full = Path.Combine(root, assertion.Path);
        if (!File.Exists(full))
        {
            failures.Add($"{assertion.Path} 不存在");
            return;
        }

        var text = File.ReadAllText(full);
        if (!string.IsNullOrEmpty(assertion.Contains) &&
            !text.Contains(assertion.Contains, StringComparison.Ordinal))
        {
            failures.Add($"{assertion.Path} 缺少期望内容「{assertion.Contains}」");
        }

        if (!string.IsNullOrEmpty(assertion.NotContains) &&
            text.Contains(assertion.NotContains, StringComparison.Ordinal))
        {
            failures.Add($"{assertion.Path} 仍包含不应出现的内容「{assertion.NotContains}」");
        }
    }

    /// <summary>按脚本回放模型的工具调用；规划与验收请求按 maxTokens 区分并返回固定合法输出。</summary>
    private sealed class ScriptedRegressionChat : IChatCompletion
    {
        private readonly List<RegressionScriptStep> _script;
        private int _index;

        public ScriptedRegressionChat(List<RegressionScriptStep> script) => _script = script;

        public int ToolCalls { get; private set; }
        public int EstimatedTokens { get; private set; }

        public Task<ChatTurn> ChatAsync(
            IReadOnlyList<ChatMessage> messages,
            IReadOnlyList<ToolDefinition>? tools,
            int maxTokens,
            CancellationToken cancellationToken)
        {
            EstimatedTokens += 40 + ProjectRules.EstimateTokens(
                string.Join('\n', messages.Select(message => message.Content)));

            if (maxTokens <= 512)
            {
                return Task.FromResult(new ChatTurn { Content = "1. 读取目标文件\n2. 按请求修改\n3. 复核结果" });
            }

            if (maxTokens <= 700)
            {
                return Task.FromResult(new ChatTurn
                {
                    Content = """{"pass": true, "summary": "文件改动已落盘", "issues": []}"""
                });
            }

            if (_index >= _script.Count)
            {
                return Task.FromResult(new ChatTurn { Content = "完成。" });
            }

            var step = _script[_index++];
            if (!string.IsNullOrEmpty(step.Tool))
            {
                ToolCalls++;
                var turn = new ChatTurn();
                var arguments = JsonSerializer.Serialize(step.Args ?? new Dictionary<string, string>());
                turn.ToolCalls.Add(new ToolCall($"call-{ToolCalls}", step.Tool, arguments));
                return Task.FromResult(turn);
            }

            return Task.FromResult(new ChatTurn { Content = step.Text ?? "完成。" });
        }
    }
}
