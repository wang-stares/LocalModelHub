namespace LocalModelHub.Core.Services;

using LocalModelHub.Core.Models;

/// <summary>
/// 粗略 token 估算与上下文裁剪：中文按 1 token/字、ASCII 按 4 字符/token。
/// llama-server 返回 400 "exceeds the available context size" 时整轮 Agent 都会失败，
/// 因此工具结果进入 messages 前必须先按预算裁剪，并在超限时从最旧的结果开始回收。
/// </summary>
public static class ContextBudget
{
    private const string RecycledObservation = "（早期工具结果已因上下文预算回收。如需该内容请重新调用工具。）";

    public static int Estimate(string? text)
    {
        if (string.IsNullOrEmpty(text)) return 0;

        var cjk = 0;
        foreach (var c in text)
        {
            if (c is >= '\u4E00' and <= '\u9FFF') cjk++;
        }

        return cjk + (text.Length - cjk) / 4;
    }

    public static int Estimate(IEnumerable<ChatMessage> messages) =>
        messages.Sum(message =>
            Estimate(message.Content)
            + (message.ToolCalls?.Sum(call => Estimate(call.Name) + Estimate(call.Arguments)) ?? 0) + 8);

    /// <summary>
    /// 保留头尾、中间省略，并给出续读指令。行数太少（压缩成一行的文件）时按字符截断。
    /// </summary>
    public static string TrimObservation(string text, int maxTokens, string? path = null, int startLine = 0)
    {
        if (string.IsNullOrEmpty(text) || maxTokens <= 0 || Estimate(text) <= maxTokens) return text;

        var lines = text.Split('\n');
        var keepHead = Math.Max(4, lines.Length / 5);
        var keepTail = Math.Max(4, lines.Length / 8);
        var omitted = lines.Length - keepHead - keepTail;
        if (omitted > 4)
        {
            var hint = path is null
                ? $"\n…（此处省略 {omitted} 行，如需完整内容请重新调用工具）\n"
                : $"\n…（此处省略 {omitted} 行。需要时调用 read_file path=\"{path}\" start_line={startLine + keepHead} 继续读取）\n";
            return string.Join('\n', lines.Take(keepHead)) + hint + string.Join('\n', lines.TakeLast(keepTail));
        }

        var maxChars = Math.Max(400, maxTokens * 4);
        if (text.Length <= maxChars) return text;
        var keep = text[..maxChars];
        return path is null
            ? keep + "\n…（内容过长已截断，请缩小查询范围或重新调用工具读取剩余部分）"
            : keep + $"\n…（内容过长已截断，请用 read_file path=\"{path}\" 分片读取剩余部分）";
    }

    /// <summary>超预算时从最旧的工具观察开始回收，保留最近 2 轮完整内容。</summary>
    /// <param name="aggressive">上下文已超限时只保留最近 1 轮，给重试留出足够空间。</param>
    public static void Compact(List<ChatMessage> messages, int budget, int reserveForOutput, bool aggressive = false)
    {
        if (budget <= 0) return;

        var keepRecent = aggressive ? 2 : 4;
        var limit = Math.Max(1024, budget - reserveForOutput);
        var guard = 0;
        while (Estimate(messages) > limit && messages.Count > keepRecent && guard++ < 64)
        {
            // 只回收最近 2 轮之外的 tool 消息，保持 tool_call / tool 消息的配对关系。
            // 已回收的占位消息必须跳过，否则 while 循环会一直命中同一条、预算永远降不下来。
            var index = -1;
            for (var i = 1; i < messages.Count - keepRecent; i++)
            {
                if (messages[i].Role == "tool" && messages[i].Content != RecycledObservation)
                {
                    index = i;
                    break;
                }
            }

            if (index < 0) break; // 没有可回收的工具结果：交给调用方降级或缩窗口重试
            messages[index] = new ChatMessage("tool", RecycledObservation)
            {
                ToolCallId = messages[index].ToolCallId
            };
        }
    }
}
