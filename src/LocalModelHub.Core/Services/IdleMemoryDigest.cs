using System.Text;
using System.Text.RegularExpressions;
using LocalModelHub.Core.Models;
using LocalModelHub.Core.Services.Abstractions;

namespace LocalModelHub.Core.Services;

/// <summary>
/// 闲时自动记忆：空闲休眠前翻阅历史会话，挑出尚未整理的会话，用一次短模型调用提炼
/// 「跨会话仍有用的用户偏好与工作习惯」；模型不可用时退回规则提取。
/// 只写全局记忆——历史会话没有工作区归属，写项目层会把事实记到错误的项目上。
/// </summary>
public sealed class IdleMemoryDigest
{
    /// <summary>每次闲时最多翻阅的会话数：积压的历史会在后续空闲里分批消化。</summary>
    public const int MaxSessionsPerRun = 5;
    public const int MaxMessagesPerSession = 20;
    public const int MaxInputChars = 4000;
    public const int MaxOutputTokens = 320;

    private const int MaxTextLength = 220;
    private const int TimeoutSeconds = 45;

    private static readonly Regex Whitespace = new(@"\s+", RegexOptions.Compiled);

    private const string Prompt = """
        你是长期记忆整理器。下面是一段历史会话记录，只提取「跨会话仍有用的用户偏好与工作习惯」：
        1. 用户明确表达的语言、风格、技术栈偏好、角色背景、固定工作流程；
        2. 用户明确纠正过的做法（以后不要再……/统一用……）；
        3. 不要提取：具体项目的技术细节与命令（无法判断会话属于哪个工作区）、一次性任务细节、临时文件名、操作结果、模型自己的推测或道歉；
        4. 不要记录密码、密钥、令牌、手机号、身份证号等敏感信息；
        5. 每条用陈述句，不超过 60 字，不重复；最多 4 条，没有新信息就返回 {"facts":[]}。
        只输出 JSON，不要解释，不要代码块。
        """;

    private readonly IMemoryStore _memory;
    private readonly Func<IReadOnlyList<ChatMessage>, int, CancellationToken, Task<string>>? _complete;

    /// <param name="complete">完成一次短对话并返回正文；为空时只走规则提取。</param>
    public IdleMemoryDigest(
        IMemoryStore memory,
        Func<IReadOnlyList<ChatMessage>, int, CancellationToken, Task<string>>? complete = null)
    {
        _memory = memory;
        _complete = complete;
    }

    /// <summary>挑选尚未整理、或整理后又有新消息的会话，最近更新的优先。</summary>
    public static IReadOnlyList<ChatSession> SelectPending(
        IReadOnlyList<ChatSession> sessions,
        int limit = MaxSessionsPerRun)
    {
        if (limit <= 0) return [];

        return sessions
            .Where(session => session.Messages.Count > 0)
            .Where(session => session.MemoryDigestedAt is null || session.MemoryDigestedAt < session.UpdatedAt)
            .OrderByDescending(session => session.UpdatedAt)
            .Take(limit)
            .ToArray();
    }

    /// <summary>从给定会话中提炼并写入全局记忆；返回新增 / 合并条数与来源。</summary>
    public async Task<IdleDigestResult> DigestAsync(
        IReadOnlyList<ChatSession> sessions,
        CancellationToken cancellationToken)
    {
        var selected = sessions
            .Where(session => session.Messages.Count > 0)
            .Take(MaxSessionsPerRun)
            .ToArray();
        if (selected.Length == 0) return IdleDigestResult.Empty;

        var transcript = BuildTranscript(selected);
        if (transcript.Length == 0) return IdleDigestResult.Empty with { Sessions = selected.Length };

        IReadOnlyList<MemoryCandidate> candidates;
        string source;

        if (_complete is not null)
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(TimeoutSeconds));
                var raw = await _complete(
                    [ChatMessage.System(Prompt), ChatMessage.User(transcript)],
                    MaxOutputTokens,
                    timeout.Token);

                if (MemoryDistiller.TryParse(raw, out var parsed))
                {
                    candidates = MemoryDistiller.Sanitize(parsed, allowProject: false);
                    source = "model";
                }
                else
                {
                    candidates = Fallback(selected);
                    source = "rules";
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception)
            {
                // 模型不可用 / 超时：退回规则提取，不影响休眠。
                candidates = Fallback(selected);
                source = "rules";
            }
        }
        else
        {
            candidates = Fallback(selected);
            source = "rules";
        }

        var added = 0;
        var merged = 0;
        foreach (var candidate in candidates)
        {
            var result = _memory.Upsert(candidate.Text, "model", MemoryEntry.KindUser, "");
            if (result.IsNew) added++;
            else merged++;
        }

        return new IdleDigestResult(added, merged, selected.Length, source);
    }

    private static string BuildTranscript(IReadOnlyList<ChatSession> sessions)
    {
        var builder = new StringBuilder();
        foreach (var session in sessions)
        {
            var header = $"### 会话：{session.Title}（{session.UpdatedAt:yyyy-MM-dd HH:mm}）\n";
            if (builder.Length + header.Length >= MaxInputChars) break;
            builder.AppendLine(header.TrimEnd());

            var messages = session.Messages
                .Where(message => message.Role is "user" or "assistant" && !string.IsNullOrWhiteSpace(message.Text))
                .TakeLast(MaxMessagesPerSession);

            foreach (var message in messages)
            {
                var line = $"{(message.Role == "user" ? "用户" : "助手")}：{Compact(message.Text, MaxTextLength)}\n";
                if (builder.Length + line.Length > MaxInputChars) return builder.ToString().TrimEnd();
                builder.Append(line);
            }
        }

        return builder.ToString().TrimEnd();
    }

    /// <summary>没有模型时的兜底：只从用户消息里提取明确的偏好陈述。</summary>
    private static IReadOnlyList<MemoryCandidate> Fallback(IReadOnlyList<ChatSession> sessions)
    {
        var texts = new List<string>();
        foreach (var session in sessions)
        {
            foreach (var message in session.Messages.Where(message => message.Role == "user"))
            {
                texts.AddRange(AutoMemoryExtractor.Extract(message.Text));
            }
        }

        return MemoryDistiller.Sanitize(
            texts.Select(text => new MemoryCandidate(text, MemoryEntry.KindUser)).ToArray(),
            allowProject: false);
    }

    private static string Compact(string? text, int maxLength)
    {
        var value = Whitespace.Replace((text ?? "").Trim(), " ");
        return value.Length <= maxLength ? value : value[..maxLength] + "…";
    }
}

/// <summary>闲时整理结果：写入条数、看过的会话数与来源（model / rules / skipped）。</summary>
public sealed record IdleDigestResult(int Added, int Merged, int Sessions, string Source)
{
    public static IdleDigestResult Empty { get; } = new(0, 0, 0, "skipped");

    public bool HasChanges => Added > 0 || Merged > 0;

    public string StatusText => !HasChanges
        ? ""
        : Merged > 0
            ? $"闲时记忆：新增 {Added} 条、合并 {Merged} 条（翻阅 {Sessions} 个会话）"
            : $"闲时记忆：新增 {Added} 条（翻阅 {Sessions} 个会话）";
}
