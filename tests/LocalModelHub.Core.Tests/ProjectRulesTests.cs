using LocalModelHub.Core.Models;
using LocalModelHub.Core.Services;
using Xunit;

namespace LocalModelHub.Core.Tests;

public sealed class ProjectRulesTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"lmh-rules-{Guid.NewGuid():N}");

    public ProjectRulesTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void Build_reads_candidates_in_priority_order()
    {
        File.WriteAllText(Path.Combine(_root, "AGENTS.md"), "先跑 dotnet test");
        Directory.CreateDirectory(Path.Combine(_root, ".lmh"));
        File.WriteAllText(Path.Combine(_root, ".lmh", "rules.md"), "不要改动 public API");
        File.WriteAllText(Path.Combine(_root, "PLAN.md"), "第三阶段：补测试");

        var rules = ProjectRules.Build(_root);

        Assert.Contains("【项目规约】", rules);
        Assert.True(rules.IndexOf("AGENTS.md", StringComparison.Ordinal) <
                    rules.IndexOf(".lmh/rules.md", StringComparison.Ordinal));
        Assert.True(rules.IndexOf(".lmh/rules.md", StringComparison.Ordinal) <
                    rules.IndexOf("PLAN.md", StringComparison.Ordinal));
        Assert.Contains("先跑 dotnet test", rules);
    }

    [Fact]
    public void Build_truncates_when_over_budget()
    {
        var line = new string('中', 400);
        File.WriteAllText(Path.Combine(_root, "AGENTS.md"), string.Join('\n', Enumerable.Repeat(line, 10)));

        var rules = ProjectRules.Build(_root);

        Assert.Contains("已截断", rules);
        Assert.True(ProjectRules.EstimateTokens(rules) <= ProjectRules.MaxTokens + 40);
    }

    [Fact]
    public void Build_returns_empty_when_no_rules()
    {
        Assert.Equal("", ProjectRules.Build(_root));
        Assert.Equal("", ProjectRules.Build(null));
    }

    [Fact]
    public async Task Agent_injects_project_rules_at_end_of_system_prompt()
    {
        File.WriteAllText(Path.Combine(_root, "AGENTS.md"), "提交前必须运行测试");
        var workspace = new WorkspaceService();
        workspace.SetRoot(_root);
        var memory = new MemoryService(Path.Combine(_root, "memory.json"));
        var chat = new FakeChat();

        var agent = new WorkspaceAgent(chat, workspace, memory, AgentMode.Chat, allowMemoryWrites: false);
        await agent.RunAsync([ChatMessage.User("你好")], null, null, CancellationToken.None);

        var system = chat.Calls[0].Messages[0].Content;
        Assert.Contains("【项目规约】", system);
        Assert.Contains("提交前必须运行测试", system);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch
        {
            // 临时目录清理失败不影响测试
        }
    }

    private sealed class FakeChat : IChatCompletion
    {
        public List<(IReadOnlyList<ChatMessage> Messages, bool HasTools)> Calls { get; } = [];

        public Task<ChatTurn> ChatAsync(
            IReadOnlyList<ChatMessage> messages,
            IReadOnlyList<ToolDefinition>? tools,
            int maxTokens,
            CancellationToken cancellationToken)
        {
            Calls.Add(([.. messages], tools is { Count: > 0 }));
            return Task.FromResult(new ChatTurn { Content = "好" });
        }
    }
}
