using System.Text.Json.Nodes;

namespace LocalModelHub.Core.Models;

/// <summary>对话页的三种模式：普通对话、计划（只读）、目标（读写执行）。</summary>
public enum AgentMode
{
    Chat = 0,
    Plan = 1,
    Goal = 2
}

public sealed record ChatMessage(string Role, string Content)
{
    /// <summary>assistant 消息携带的工具调用（OpenAI tool_calls 格式）。</summary>
    public IReadOnlyList<ToolCall>? ToolCalls { get; init; }

    /// <summary>tool 结果消息对应的调用 id。</summary>
    public string? ToolCallId { get; init; }

    public static ChatMessage User(string content) => new("user", content);
    public static ChatMessage Assistant(string content) => new("assistant", content);
    public static ChatMessage System(string content) => new("system", content);
}

public sealed record ToolCall(string Id, string Name, string Arguments);

public sealed record ToolDefinition(string Name, string Description, JsonObject Parameters);

public sealed class ChatTurn
{
    public string Content { get; set; } = "";
    /// <summary>推理模型单独返回的思考内容（reasoning_content）。</summary>
    public string Reasoning { get; set; } = "";
    public List<ToolCall> ToolCalls { get; } = [];
}

/// <summary>流式增量：正文与思考过程分开，便于 UI 折叠展示。</summary>
public sealed record ChatChunk(string? Content = null, string? Reasoning = null);

public sealed record ToolActivity(string Name, string Summary);

public sealed record AgentResult(
    string Text,
    IReadOnlyList<ToolActivity> Activities,
    AgentVerification? Verification = null,
    AgentTrajectory? Trajectory = null);

public sealed record LaunchOptions(
    int Port,
    int ContextSize,
    int GpuLayers,
    int Threads,
    string Alias,
    string Host = "127.0.0.1",
    string? ApiKey = null,
    string FlashAttention = "auto",
    string CacheTypeK = "f16",
    string CacheTypeV = "f16",
    int CpuMoeLayers = 0,
    int ParallelSlots = 1,
    string LoadMode = "mmap",
    string ReasoningFormat = "deepseek",
    bool EnableMetrics = false);

public sealed class DownloadedModel
{
    public required string ModelId { get; init; }
    public required string DisplayName { get; init; }
    public required string QuantLabel { get; init; }
    public required string FolderPath { get; init; }
    public required string PrimaryPath { get; init; }
    public required long TotalBytes { get; init; }
    public DateTime AddedAt { get; init; }
    public string? Developer { get; init; }
    public double ParametersB { get; init; }
    public int ContextLength { get; init; }
    public ModelEntry? Entry { get; init; }
    public ModelQuant? Quant { get; init; }

    public string SizeText => Formatters.Bytes(TotalBytes);
}
