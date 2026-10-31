using LocalModelHub.Core.Models;
using LocalModelHub.Core.Services;
using Xunit;

namespace LocalModelHub.Core.Tests;

public class ContextBudgetTests
{
    [Fact]
    public void Estimate_counts_cjk_as_one_token_and_ascii_as_quarter_token()
    {
        Assert.Equal(0, ContextBudget.Estimate((string?)null));
        Assert.Equal(2, ContextBudget.Estimate("abcdefgh"));
        Assert.Equal(4, ContextBudget.Estimate("你好世界"));
        Assert.Equal(4, ContextBudget.Estimate("你好abcdefgh"));
    }

    [Fact]
    public void Estimate_of_messages_includes_tool_call_arguments()
    {
        var plain = new List<ChatMessage> { ChatMessage.User("abcdefgh") };
        var withCall = new List<ChatMessage>
        {
            new("assistant", "abcdefgh")
            {
                ToolCalls = [new ToolCall("1", "read_file", "{\"path\":\"src/Program.cs\"}")]
            }
        };

        Assert.True(ContextBudget.Estimate(withCall) > ContextBudget.Estimate(plain));
    }

    [Fact]
    public void TrimObservation_keeps_short_text_untouched()
    {
        var text = string.Join('\n', Enumerable.Range(1, 10).Select(i => $"line {i}"));

        Assert.Equal(text, ContextBudget.TrimObservation(text, 400, "a.txt", 1));
    }

    [Fact]
    public void TrimObservation_keeps_head_and_tail_and_points_to_next_window()
    {
        var text = string.Join('\n', Enumerable.Range(1, 100).Select(i => $"line {i}"));

        var trimmed = ContextBudget.TrimObservation(text, 40, "src/Program.cs", 1);

        Assert.Contains("此处省略 68 行", trimmed);
        Assert.Contains("read_file path=\"src/Program.cs\" start_line=21", trimmed);
        Assert.StartsWith("line 1", trimmed);
        Assert.EndsWith("line 100", trimmed);
        Assert.True(trimmed.Length < text.Length);
    }

    [Fact]
    public void TrimObservation_truncates_a_single_huge_line_by_characters()
    {
        var text = new string('a', 5000);

        var trimmed = ContextBudget.TrimObservation(text, 100, "bundle.min.js", 1);

        Assert.True(trimmed.Length < text.Length);
        Assert.Contains("已截断", trimmed);
        Assert.Contains("bundle.min.js", trimmed);
    }

    [Fact]
    public void Compact_recycles_oldest_tool_results_and_keeps_recent_turns()
    {
        var messages = new List<ChatMessage> { ChatMessage.System("system") };
        for (var i = 0; i < 10; i++)
        {
            messages.Add(new ChatMessage("tool", new string('x', 3000)) { ToolCallId = $"call-{i}" });
        }

        ContextBudget.Compact(messages, budget: 8192, reserveForOutput: 2048);

        Assert.StartsWith("（早期工具结果已因上下文预算回收", messages[1].Content);
        Assert.Equal("call-0", messages[1].ToolCallId);
        Assert.Equal(new string('x', 3000), messages[^1].Content);
        Assert.True(ContextBudget.Estimate(messages) <= 8192);
    }

    [Fact]
    public void Compact_leaves_messages_under_budget_untouched()
    {
        var messages = new List<ChatMessage>
        {
            ChatMessage.System("system"),
            new("tool", "small result") { ToolCallId = "call-0" },
            ChatMessage.User("继续")
        };

        ContextBudget.Compact(messages, budget: 8192, reserveForOutput: 2048);

        Assert.Equal("small result", messages[1].Content);
    }
}
