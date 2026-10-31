using LocalModelHub.Core.Services;
using Xunit;

namespace LocalModelHub.Core.Tests;

public class AgentFailureTests
{
    [Fact]
    public void Classify_context_overflow_by_server_message()
    {
        var error = new InvalidOperationException(
            "推理服务返回 400：the request exceeds the available context size");

        Assert.Equal(FailureKind.ContextOverflow, AgentFailure.Classify(error));
    }

    [Fact]
    public void Classify_timeout_errors()
    {
        Assert.Equal(FailureKind.Timeout, AgentFailure.Classify(new TimeoutException("超时")));
        Assert.Equal(FailureKind.Timeout, AgentFailure.Classify(new TaskCanceledException()));
    }

    [Fact]
    public void Classify_server_down_errors()
    {
        Assert.Equal(FailureKind.ServerDown, AgentFailure.Classify(new HttpRequestException("Connection refused")));
        Assert.Equal(FailureKind.ServerDown, AgentFailure.Classify(new InvalidOperationException("推理服务尚未启动。")));
        Assert.Equal(FailureKind.ServerDown, AgentFailure.Classify(new InvalidOperationException("推理服务返回 502：bad gateway")));
    }

    [Fact]
    public void Classify_patch_and_tool_parse_failures()
    {
        Assert.Equal(FailureKind.PatchMismatch,
            AgentFailure.Classify(new InvalidOperationException("old_text 上下文与文件不匹配")));
        Assert.Equal(FailureKind.ToolParse,
            AgentFailure.Classify(new InvalidOperationException("工具参数不是合法 JSON")));
    }

    [Fact]
    public void Classify_unknown_failure()
    {
        Assert.Equal(FailureKind.Unknown, AgentFailure.Classify(new InvalidOperationException("boom")));
        Assert.Equal("未知错误", AgentFailure.Describe(FailureKind.Unknown));
    }
}
