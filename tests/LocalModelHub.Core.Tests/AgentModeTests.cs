using LocalModelHub.Core.Models;
using LocalModelHub.Core.Services;
using Xunit;

namespace LocalModelHub.Core.Tests;

public sealed class AgentModeTests
{
    [Fact]
    public void Plan_mode_has_read_only_tools()
    {
        Assert.Equal(["list_files", "search_code", "find_symbols", "read_file", "remember"],
            WorkspaceAgent.ToolNames(AgentMode.Plan));
    }

    [Fact]
    public void Goal_mode_can_read_and_write_files()
    {
        var tools = WorkspaceAgent.ToolNames(AgentMode.Goal);
        Assert.Contains("read_file", tools);
        Assert.Contains("find_symbols", tools);
        Assert.Contains("write_file", tools);
        Assert.Contains("replace_in_file", tools);
        Assert.Contains("apply_patch", tools);
        Assert.Contains("remember", tools);
    }

    [Fact]
    public void Goal_mode_has_full_tool_set_and_chat_has_none()
    {
        Assert.Equal(
            ["list_files", "search_code", "find_symbols", "read_file", "write_file", "replace_in_file", "apply_patch", "remember"],
            WorkspaceAgent.ToolNames(AgentMode.Goal));
        Assert.Empty(WorkspaceAgent.ToolNames(AgentMode.Chat));
    }

    [Fact]
    public void Patch_tool_is_not_available_in_plan_mode()
    {
        Assert.DoesNotContain("apply_patch", WorkspaceAgent.ToolNames(AgentMode.Plan));
        Assert.Contains("find_symbols", WorkspaceAgent.ToolNames(AgentMode.Plan));
    }

    [Fact]
    public void Command_tool_is_opt_in_and_goal_only()
    {
        Assert.Contains("run_command", WorkspaceAgent.ToolNames(AgentMode.Goal, allowCommands: true));
        Assert.DoesNotContain("run_command", WorkspaceAgent.ToolNames(AgentMode.Plan, allowCommands: true));
        Assert.DoesNotContain("run_command", WorkspaceAgent.ToolNames(AgentMode.Goal, allowCommands: false));
    }

    [Fact]
    public void Memory_tools_can_be_disabled()
    {
        Assert.DoesNotContain("remember", WorkspaceAgent.ToolNames(AgentMode.Goal, allowMemory: false));
    }
}
