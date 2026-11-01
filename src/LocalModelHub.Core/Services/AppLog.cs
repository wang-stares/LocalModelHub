using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace LocalModelHub.Core.Services;

/// <summary>
/// 持久化运行日志：logs/app-{日期}.log，保留 7 天，单文件超过 10 MB 时滚动。
/// 出问题时可以复盘启动参数、健康检查与异常堆栈，而不是只看内存里的 500 行。
/// </summary>
public static class AppLog
{
    private const long MaxBytes = 10L * 1024 * 1024;
    private const int KeepDays = 7;

    private static readonly object Sync = new();

    /// <summary>命令行里的密钥只写 ***，避免日志泄漏。</summary>
    private static readonly Regex SecretPattern = new(
        @"(--api-key[=\s]+)(""[^""]*""|[^\s]+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static bool _pruned;

    public static string Directory => AppPaths.LogsDirectory;

    public static string FileFor(DateTime date) =>
        Path.Combine(Directory, $"app-{date:yyyyMMdd}.log");

    public static void Info(string category, string message) => Write("INFO", category, message);
    public static void Warn(string category, string message) => Write("WARN", category, message);

    public static void Error(string category, string message, Exception? error = null) =>
        Write("ERROR", category, message, error);

    /// <summary>日志正文脱敏：只保留参数名，值统一打码。</summary>
    public static string Mask(string text) => SecretPattern.Replace(text, "$1***");

    private static void Write(string level, string category, string message, Exception? error = null)
    {
        try
        {
            lock (Sync)
            {
                AppPaths.EnsureCreated();
                if (!_pruned)
                {
                    Prune();
                    _pruned = true;
                }

                var path = FileFor(DateTime.Now);
                if (File.Exists(path) && new FileInfo(path).Length > MaxBytes)
                {
                    path = Path.Combine(Directory, $"app-{DateTime.Now:yyyyMMdd-HHmmss}.log");
                }

                var builder = new StringBuilder();
                builder.Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"))
                    .Append(" [").Append(level).Append("] [")
                    .Append(category).Append("] ")
                    .Append(Mask(message));
                if (error is not null) builder.AppendLine().Append(error);

                File.AppendAllText(path, builder.ToString().TrimEnd() + Environment.NewLine, Encoding.UTF8);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Debug.WriteLine($"[AppLog] 写入日志失败：{ex.Message}");
        }
    }

    private static void Prune()
    {
        try
        {
            if (!System.IO.Directory.Exists(Directory)) return;
            var cutoff = DateTime.Now.AddDays(-KeepDays);
            foreach (var file in System.IO.Directory.EnumerateFiles(Directory, "app-*.log"))
            {
                try
                {
                    if (File.GetLastWriteTime(file) < cutoff) File.Delete(file);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    Debug.WriteLine($"[AppLog] 清理旧日志失败：{ex.Message}");
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Debug.WriteLine($"[AppLog] 扫描日志目录失败：{ex.Message}");
        }
    }
}
