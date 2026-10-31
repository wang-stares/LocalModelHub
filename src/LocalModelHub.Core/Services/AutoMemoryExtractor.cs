using System.Text.RegularExpressions;

namespace LocalModelHub.Core.Services;

/// <summary>
/// 从用户消息中提取值得长期记住的事实与偏好（规则版，不额外调用模型）。
/// 只保留陈述句，疑问句与假设句一律跳过，避免把一次性问题写进记忆。
/// </summary>
public static class AutoMemoryExtractor
{
    private const int MinLength = 6;
    private const int MaxLength = 160;

    private static readonly Regex SentenceSplit = new(@"[。！？!?\n；;]+", RegexOptions.Compiled);
    private static readonly Regex Whitespace = new(@"\s+", RegexOptions.Compiled);

    private static readonly Regex ExplicitRemember = new(
        @"^(?:请|帮我|麻烦|记得)?(?:记住|记一下|记下|别忘了|以后(?:都|请)?(?:用|按|叫|称呼|回复|回答|使用))[:：,，\s]*(?<body>.+)$",
        RegexOptions.Compiled);

    private static readonly Regex Signal = new(
        @"(?:我(?:更|比较|很|特别)?(?:喜欢|不喜欢|讨厌|偏好|习惯|常用|想要|需要|希望)|我(?:是|在做|正在做|负责)|我们(?:项目|团队|公司|这边|组)(?:用|使用|采用|是)|我的(?:电脑|机器|设备|显卡|内存|项目|工作|环境|系统)|这台(?:电脑|机器))",
        RegexOptions.Compiled);

    /// <summary>返回最多 <paramref name="maxItems"/> 条可写入长期记忆的文本。</summary>
    public static IReadOnlyList<string> Extract(string? userText, int maxItems = 3)
    {
        var text = (userText ?? "").Trim();
        if (text.Length < MinLength) return [];

        var results = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var raw in SentenceSplit.Split(text))
        {
            var sentence = Whitespace.Replace(raw, " ").Trim();
            if (sentence.Length < MinLength) continue;

            var body = sentence;
            var explicitMatch = ExplicitRemember.Match(sentence);
            if (explicitMatch.Success)
            {
                body = explicitMatch.Groups["body"].Value.Trim();
            }
            else if (!Signal.IsMatch(sentence))
            {
                continue;
            }

            body = body.Trim(' ', '，', ',', '、', '。', '！', '；', ';', '"', '“', '”');
            if (body.Length < MinLength || body.Length > MaxLength) continue;
            if (body.Contains("如果", StringComparison.Ordinal) ||
                body.Contains("假如", StringComparison.Ordinal) ||
                body.Contains("假设", StringComparison.Ordinal)) continue;
            if (body.EndsWith("吗", StringComparison.Ordinal) ||
                body.EndsWith("呢", StringComparison.Ordinal)) continue;

            if (seen.Add(body)) results.Add(body);
            if (results.Count >= maxItems) break;
        }

        return results;
    }
}
