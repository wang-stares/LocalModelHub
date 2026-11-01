using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using LocalModelHub.Core.Models;
using LocalModelHub.Core.Services.Abstractions;

namespace LocalModelHub.Core.Services;

/// <summary>
/// 模型自主记忆：每轮对话或每次文件编辑结束后，用一次短调用让模型自己总结
/// 「这轮了解到的用户偏好与项目事实」并写入长期记忆，不需要用户主动说「记住」。
/// 模型输出无法解析时退回规则提取，保证小模型或远程 API 不稳定时记忆链路不中断。
/// </summary>
public sealed class MemoryDistiller
{
    /// <summary>约束解码用的 JSON Schema：只允许 facts（文本 + 类型）。</summary>
    public const string JsonSchema = """
        {"type":"object","properties":{"facts":{"type":"array","items":{"type":"object","properties":{"text":{"type":"string","description":"一条可长期复用的用户偏好或项目事实，陈述句，不超过 60 字"},"kind":{"type":"string","enum":["user","project"]}},"required":["text","kind"]}}},"required":["facts"]}
        """;

    public const int MaxFacts = 4;
    private const int MinTextLength = 6;
    private const int MaxTextLength = 160;
    private const int MaxOutputTokens = 320;
    private const int TimeoutSeconds = 30;

    private static readonly Regex Whitespace = new(@"\s+", RegexOptions.Compiled);
    private static readonly Regex LeadingNoise = new(
        @"^\s*(?:[-*•·]|\(?\d+\)?[.、)）．]|记忆|事实|偏好)\s*[：:,，]?\s*",
        RegexOptions.Compiled);
    private static readonly Regex Ephemeral = new(
        @"本次|这次|刚才|刚刚|本轮|该任务|这一次|上面的|```|用户要求我|模型应该|助手应该",
        RegexOptions.Compiled);
    private static readonly Regex Hypothetical = new(
        @"^(?:如果|假如|假设|要是)",
        RegexOptions.Compiled);
    /// <summary>敏感信息不进记忆：密码、密钥、令牌、手机号、身份证号、银行卡号。</summary>
    private static readonly Regex Sensitive = new(
        @"\bpassword\b|\bpasswd\b|密码|口令|\bapi[ _-]?key\b|access[ _-]?token|\bsecret\b|\bsk-[A-Za-z0-9]{8,}|\bBearer\s|1[3-9]\d{9}|\d{17}[\dXx]|\d{16,19}",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    /// <summary>临时性、一次性的安排不沉淀为长期记忆。</summary>
    private static readonly Regex Transient = new(
        @"^(?:临时|暂时|今天先|先用|接下来先|马上先|本次先)",
        RegexOptions.Compiled);

    private readonly IMemoryStore _memory;
    private readonly Func<IReadOnlyList<ChatMessage>, int, CancellationToken, Task<string>> _complete;

    /// <param name="complete">完成一次非流式对话并返回正文；本地模型与外部 API 由调用方适配。</param>
    public MemoryDistiller(
        IMemoryStore memory,
        Func<IReadOnlyList<ChatMessage>, int, CancellationToken, Task<string>> complete)
    {
        _memory = memory;
        _complete = complete;
    }

    /// <summary>
    /// 从一轮对话中提炼并写入记忆。写入只在调用方确认「允许模型写记忆」后发生；
    /// 返回统计结果，供界面提示自动记录了几条。
    /// </summary>
    public async Task<MemoryDistillResult> DistillAsync(
        MemoryDistillRequest request,
        CancellationToken cancellationToken)
    {
        var userText = Compress(request.UserText, 400);
        var answer = Compress(request.AssistantText, 600);
        // 打招呼、单句确认等过短的一轮没有可沉淀的信息，直接跳过，省一次模型调用。
        if (userText.Length + answer.Length < 24) return MemoryDistillResult.Skipped;

        var scope = (request.Workspace ?? "").Trim();
        var allowProject = request.AllowProjectFacts && scope.Length > 0;

        IReadOnlyList<MemoryCandidate> candidates;
        string source;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(TimeoutSeconds));

            var raw = await _complete(
                BuildMessages(request, userText, answer, scope, allowProject),
                MaxOutputTokens,
                timeout.Token);

            if (TryParse(raw, out var parsed))
            {
                candidates = Sanitize(parsed, allowProject);
                source = "model";
                // 模型明确返回空数组：这一轮没有新信息，不再用规则补齐。
                if (parsed.Count == 0) return MemoryDistillResult.Skipped with { Source = source };
            }
            else
            {
                candidates = Sanitize(Fallback(request.UserText), allowProject);
                source = "rules";
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            // 模型不可用 / 超时 / 输出乱码：退回规则提取，不影响本轮回答。
            candidates = Sanitize(Fallback(request.UserText), allowProject);
            source = "rules";
        }

        if (candidates.Count == 0) return MemoryDistillResult.Skipped with { Source = source };

        var added = 0;
        var merged = 0;
        var conflicts = new List<MemoryEntry>();
        foreach (var candidate in candidates)
        {
            var kind = candidate.Kind == MemoryEntry.KindProject ? MemoryEntry.KindProject : MemoryEntry.KindUser;
            var entryScope = kind == MemoryEntry.KindProject ? scope : "";
            var result = _memory.Upsert(candidate.Text, "model", kind, entryScope);
            if (result.IsNew)
            {
                added++;
                conflicts.AddRange(_memory.FindConflicts(candidate.Text));
            }
            else
            {
                merged++;
            }
        }

        return new MemoryDistillResult(
            added,
            merged,
            conflicts.DistinctBy(entry => entry.Id).ToArray(),
            source);
    }

    /// <summary>
    /// 解析模型输出。返回 false 表示完全解析不出结构（调用方应退回规则提取）；
    /// 返回 true 但列表为空表示模型确认「没有可记住的内容」。
    /// </summary>
    public static bool TryParse(string? raw, out IReadOnlyList<MemoryCandidate> facts)
    {
        facts = [];
        var payload = ExtractJsonObject(raw);
        if (payload is null) return false;

        try
        {
            using var document = JsonDocument.Parse(payload);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return false;
            if (!root.TryGetProperty("facts", out var array) || array.ValueKind != JsonValueKind.Array) return false;

            var list = new List<MemoryCandidate>();
            foreach (var item in array.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                var text = item.TryGetProperty("text", out var textElement) && textElement.ValueKind == JsonValueKind.String
                    ? textElement.GetString()
                    : null;
                if (string.IsNullOrWhiteSpace(text)) continue;

                var kind = item.TryGetProperty("kind", out var kindElement) && kindElement.ValueKind == JsonValueKind.String
                    ? kindElement.GetString()
                    : null;
                list.Add(new MemoryCandidate(text, NormalizeKind(kind)));
            }

            facts = list;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static IReadOnlyList<ChatMessage> BuildMessages(
        MemoryDistillRequest request,
        string userText,
        string answer,
        string scope,
        bool allowProject)
    {
        var system = new StringBuilder();
        system.AppendLine("你是长期记忆整理器。阅读这一轮对话，只提取「以后还用得上」的稳定信息：");
        system.AppendLine("1. kind=user：用户的语言、风格、技术栈偏好、角色与工作习惯；");
        system.AppendLine("2. kind=project：当前工作区的技术栈、构建 / 测试命令、目录约定、代码规范、关键依赖。");
        system.AppendLine("不要提取：一次性任务细节、临时文件名、具体代码内容、操作结果、模型推测或道歉。");
        system.AppendLine("每条用陈述句，不超过 60 字，不重复；没有新信息就返回 {\"facts\":[]}。");
        if (!allowProject) system.AppendLine("当前没有工作文件夹：只输出 kind=user 的条目。");
        system.AppendLine("只输出 JSON，不要解释，不要代码块。");

        var body = new StringBuilder();
        body.AppendLine($"工作区：{(scope.Length > 0 ? scope : "未设置")}");
        body.AppendLine($"用户这一轮说：{userText}");
        if (answer.Length > 0) body.AppendLine($"助手这一轮答：{answer}");
        if (request.Activities is { Count: > 0 })
        {
            body.AppendLine("本轮工具动作：");
            foreach (var activity in request.Activities.Where(item => !string.IsNullOrWhiteSpace(item)).Take(6))
            {
                body.AppendLine($"- {Compress(activity, 100)}");
            }
        }

        return
        [
            ChatMessage.System(system.ToString().TrimEnd()),
            ChatMessage.User(body.ToString().TrimEnd())
        ];
    }

    /// <summary>非模型兜底：复用规则提取器，只产出明确的用户偏好陈述。</summary>
    private static IReadOnlyList<MemoryCandidate> Fallback(string? userText) =>
        AutoMemoryExtractor.Extract(userText)
            .Select(text => new MemoryCandidate(text, MemoryEntry.KindUser))
            .ToArray();

    /// <summary>过滤模型或规则产出的候选：去掉临时、敏感、假设、疑问与重复内容。</summary>
    public static IReadOnlyList<MemoryCandidate> Sanitize(
        IReadOnlyList<MemoryCandidate> candidates,
        bool allowProject)
    {
        var results = new List<MemoryCandidate>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var candidate in candidates)
        {
            var text = Clean(candidate.Text);
            if (text.Length < MinTextLength || text.Length > MaxTextLength) continue;
            if (Ephemeral.IsMatch(text)) continue;
            if (Transient.IsMatch(text)) continue;
            if (Sensitive.IsMatch(text)) continue;
            if (Hypothetical.IsMatch(text)) continue;
            if (text.EndsWith("吗", StringComparison.Ordinal) ||
                text.EndsWith("呢", StringComparison.Ordinal) ||
                text.EndsWith('?')) continue;

            var kind = candidate.Kind == MemoryEntry.KindProject ? MemoryEntry.KindProject : MemoryEntry.KindUser;
            if (kind == MemoryEntry.KindProject && !allowProject) continue;

            if (!seen.Add($"{kind}|{text}")) continue;
            results.Add(new MemoryCandidate(text, kind));
            if (results.Count >= MaxFacts) break;
        }

        return results;
    }

    private static string Clean(string? text)
    {
        var value = Whitespace.Replace((text ?? "").Trim(), " ");
        value = LeadingNoise.Replace(value, "");
        value = value.Trim(' ', '，', ',', '、', '。', '！', '；', ';', '"', '“', '”', '-', '*');
        return value;
    }

    private static string NormalizeKind(string? kind)
    {
        var value = (kind ?? "").Trim().ToLowerInvariant();
        return value is "project" or "项目" or "项目事实" ? MemoryEntry.KindProject : MemoryEntry.KindUser;
    }

    private static string Compress(string? text, int maxLength)
    {
        var value = Whitespace.Replace((text ?? "").Trim(), " ");
        return value.Length <= maxLength ? value : value[..maxLength] + "…";
    }

    private static string? ExtractJsonObject(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        if (start < 0 || end <= start) return null;
        return text[start..(end + 1)];
    }
}

/// <summary>一条待写入的记忆候选：文本 + 类型（user / project）。</summary>
public sealed record MemoryCandidate(string Text, string Kind);

/// <summary>一轮对话蒸馏所需的输入；工具动作只给摘要，避免把大段文件内容塞进提示。</summary>
public sealed record MemoryDistillRequest(
    string UserText,
    string AssistantText = "",
    IReadOnlyList<string>? Activities = null,
    string? Workspace = null,
    bool AllowProjectFacts = true);

/// <summary>蒸馏结果：新增 / 合并条数、冲突提示与来源（model / rules / skipped）。</summary>
public sealed record MemoryDistillResult(
    int Added,
    int Merged,
    IReadOnlyList<MemoryEntry> Conflicts,
    string Source)
{
    public static MemoryDistillResult Skipped { get; } =
        new(0, 0, Array.Empty<MemoryEntry>(), "skipped");

    public bool HasChanges => Added > 0 || Merged > 0;

    /// <summary>界面提示文案；没有写入时返回空串。</summary>
    public string StatusText => !HasChanges
        ? ""
        : Merged > 0
            ? $"模型已自动记忆 {Added + Merged} 条（其中 {Merged} 条更新了旧记忆）"
            : $"模型已自动记忆 {Added} 条";
}
