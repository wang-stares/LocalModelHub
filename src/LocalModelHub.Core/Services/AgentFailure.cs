namespace LocalModelHub.Core.Services;

public enum FailureKind
{
    ContextOverflow,
    PatchMismatch,
    ToolParse,
    Timeout,
    ServerDown,
    Unknown
}

/// <summary>
/// 把推理 / 工具异常归到可处理的小类。Agent 主循环据此决定压缩重试、超时重试还是直接失败，
/// 避免网络抖动或一次上下文超限就终止整轮任务。
/// </summary>
public static class AgentFailure
{
    public static FailureKind Classify(Exception exception)
    {
        var message = exception.Message ?? "";

        if (exception is HttpRequestException ||
            Contains(message, "推理服务尚未启动") ||
            Contains(message, "推理服务返回 5") ||
            Contains(message, "connection refused") ||
            Contains(message, "actively refused"))
        {
            return FailureKind.ServerDown;
        }

        if (Contains(message, "exceeds the available context") ||
            Contains(message, "context size") ||
            Contains(message, "n_ctx") ||
            Contains(message, "上下文长度") ||
            Contains(message, "上下文超限"))
        {
            return FailureKind.ContextOverflow;
        }

        if (exception is TimeoutException or OperationCanceledException || Contains(message, "推理超时"))
        {
            return FailureKind.Timeout;
        }

        if (Contains(message, "上下文与") || Contains(message, "不匹配"))
        {
            return FailureKind.PatchMismatch;
        }

        if (Contains(message, "参数不是合法 JSON") || Contains(message, "JSON 解析失败"))
        {
            return FailureKind.ToolParse;
        }

        return FailureKind.Unknown;
    }

    public static string Describe(FailureKind kind) => kind switch
    {
        FailureKind.ContextOverflow => "上下文超限",
        FailureKind.PatchMismatch => "补丁上下文不匹配",
        FailureKind.ToolParse => "工具参数解析失败",
        FailureKind.Timeout => "推理超时",
        FailureKind.ServerDown => "推理服务无响应",
        _ => "未知错误"
    };

    private static bool Contains(string message, string pattern) =>
        message.Contains(pattern, StringComparison.OrdinalIgnoreCase);
}
