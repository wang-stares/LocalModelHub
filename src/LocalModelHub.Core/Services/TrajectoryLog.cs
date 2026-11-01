using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using LocalModelHub.Core.Models;

namespace LocalModelHub.Core.Services;

/// <summary>
/// Agent 轨迹落盘：logs/trajectory-{日期}.jsonl，每行一条完整轨迹
/// （请求、工具序列、验收结论、过程分、最终回答哈希与耗时）。
/// 只记录可观测事实与摘要，不写文件内容，便于复盘与后续固定回归集对比。
/// </summary>
public static class TrajectoryLog
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private static readonly object Sync = new();

    public static string FileFor(DateTime date) =>
        Path.Combine(AppPaths.LogsDirectory, $"trajectory-{date:yyyyMMdd}.jsonl");

    public static void Append(
        string source,
        string request,
        string finalText,
        AgentResult? result,
        TimeSpan duration,
        string? reviewSummary = null,
        bool? reviewPassed = null)
    {
        try
        {
            var trajectory = result?.Trajectory;
            var record = new
            {
                at = DateTime.Now.ToString("O"),
                source,
                mode = trajectory?.Mode.ToString(),
                request = Shorten(request, 400),
                score = trajectory?.Score,
                scoreDetail = trajectory?.ScoreDetail,
                verification = trajectory?.Verification is { } verification
                    ? new { passed = verification.Passed, source = verification.Source, summary = verification.Summary }
                    : null,
                review = reviewSummary is null ? null : new { passed = reviewPassed, summary = reviewSummary },
                actions = trajectory?.Steps
                    .Where(step => step.Stage == AgentStage.Act)
                    .Select(step => new
                    {
                        round = step.Round,
                        tool = step.Action,
                        success = step.Success,
                        arguments = Shorten(AgentProtocol.ShortArguments(step.Arguments), 120)
                    }),
                resultHash = Hash(finalText),
                resultChars = finalText.Length,
                durationMs = (int)duration.TotalMilliseconds
            };

            var line = JsonSerializer.Serialize(record, JsonOptions);
            lock (Sync)
            {
                AppPaths.EnsureCreated();
                File.AppendAllText(FileFor(DateTime.Now), line + Environment.NewLine, Encoding.UTF8);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Debug.WriteLine($"[TrajectoryLog] 写入轨迹失败：{ex.Message}");
        }
    }

    private static string Hash(string text)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(text));
        return Convert.ToHexString(bytes)[..16].ToLowerInvariant();
    }

    private static string Shorten(string? text, int limit)
    {
        var value = (text ?? "").Replace("\r", "").Replace('\n', ' ').Trim();
        return value.Length <= limit ? value : value[..limit] + "…";
    }
}
