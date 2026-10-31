using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace LocalModelHub.Core.Services;

/// <summary>允许 Agent 调用的构建 / 测试命令运行器。</summary>
public interface ICommandRunner
{
    Task<CommandResult> RunAsync(string command, string workingDirectory, CancellationToken cancellationToken);
}

/// <summary>命令执行结果：退出码、合并后的输出与失败原因。</summary>
public sealed record CommandResult(bool Success, int ExitCode, string Output, string Command);

/// <summary>
/// 白名单命令执行器（RepairAgent / ThinkRepair 的验证手段）：只允许构建与测试类命令，
/// 拒绝 shell 元字符，工作目录锁定工作文件夹，超时强制结束，输出截断后再交给模型。
/// </summary>
public sealed class CommandService : ICommandRunner
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(120);
    private const int MaxOutputChars = 4000;

    private static readonly string[] ShellMetacharacters =
    [
        "|", "&", ";", "<", ">", "$", "`", "\n", "\r", "(", ")", "{", "}"
    ];

    private readonly TimeSpan _timeout;

    public CommandService(TimeSpan? timeout = null) => _timeout = timeout ?? DefaultTimeout;

    /// <summary>命令必须命中内置白名单，且不能包含 shell 元字符（不经过 shell 拼接）。</summary>
    public static bool IsAllowed(string? command)
    {
        if (string.IsNullOrWhiteSpace(command)) return false;
        if (ShellMetacharacters.Any(token => command.Contains(token, StringComparison.Ordinal))) return false;

        var tokens = Tokenize(command);
        if (tokens.Count == 0) return false;

        var executable = NormalizeExecutable(tokens[0]);
        var second = tokens.Count > 1 ? tokens[1].ToLowerInvariant() : "";
        var third = tokens.Count > 2 ? tokens[2].ToLowerInvariant() : "";

        return executable switch
        {
            "dotnet" => second is "build" or "test" or "restore",
            "npm" or "yarn" or "pnpm" => second == "test" ||
                                          (second == "run" && third is "build" or "test" or "lint" or "check"),
            "pytest" => true,
            "python" or "python3" or "py" => second == "-m" && third is "pytest" or "unittest",
            "go" => second is "test" or "build" or "vet",
            "cargo" => second is "test" or "build" or "check" or "clippy",
            "mvn" or "mvnw" or "gradle" or "gradlew" => second is "test" or "verify" or "build" or "check",
            "make" => second is "test" or "build",
            _ => false
        };
    }

    /// <summary>按空白切分参数，支持单双引号；引号未闭合时返回空列表。</summary>
    public static IReadOnlyList<string> Tokenize(string? command)
    {
        var tokens = new List<string>();
        var current = new StringBuilder();
        var quote = '\0';

        foreach (var character in command ?? "")
        {
            if (quote != '\0')
            {
                if (character == quote) quote = '\0';
                else current.Append(character);
                continue;
            }

            if (character is '"' or '\'')
            {
                quote = character;
                continue;
            }

            if (char.IsWhiteSpace(character))
            {
                if (current.Length > 0)
                {
                    tokens.Add(current.ToString());
                    current.Clear();
                }

                continue;
            }

            current.Append(character);
        }

        if (quote != '\0') return Array.Empty<string>();
        if (current.Length > 0) tokens.Add(current.ToString());
        return tokens;
    }

    public async Task<CommandResult> RunAsync(
        string command,
        string workingDirectory,
        CancellationToken cancellationToken)
    {
        if (!IsAllowed(command))
        {
            return new(false, -1, "命令不在白名单内，或包含 shell 符号（| & ; < > $ 等）。", command);
        }

        var tokens = Tokenize(command);
        var fileName = tokens[0];
        if (OperatingSystem.IsWindows() && NormalizeExecutable(fileName) is "npm" or "yarn" or "pnpm")
        {
            fileName += ".cmd";
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in tokens.Skip(1)) startInfo.ArgumentList.Add(argument);

        Process? process = null;
        try
        {
            process = Process.Start(startInfo);
            if (process is null) return new(false, -1, "命令进程启动失败。", command);

            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(_timeout);

            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                process.Kill(true);
                return new(false, -1, $"命令超过 {_timeout.TotalSeconds:0} 秒，已终止。", command);
            }

            var output = Combine(await stdout, await stderr);
            return new(process.ExitCode == 0, process.ExitCode, output, command);
        }
        catch (Win32Exception ex)
        {
            return new(false, -1, $"无法启动命令：{ex.Message}", command);
        }
        catch (InvalidOperationException ex)
        {
            return new(false, -1, $"命令启动失败：{ex.Message}", command);
        }
        finally
        {
            process?.Dispose();
        }
    }

    private static string NormalizeExecutable(string token)
    {
        var name = Path.GetFileName(token).ToLowerInvariant();
        return name.EndsWith(".exe", StringComparison.Ordinal) ? name[..^4]
            : name.EndsWith(".cmd", StringComparison.Ordinal) ? name[..^4]
            : name.EndsWith(".bat", StringComparison.Ordinal) ? name[..^4]
            : name;
    }

    /// <summary>stdout / stderr 合并后截断：保留开头与结尾，中间的构建日志通常价值不高。</summary>
    private static string Combine(string stdout, string stderr)
    {
        var output = (stdout.Trim() + "\n" + stderr.Trim()).Trim();
        if (output.Length <= MaxOutputChars) return output;

        var head = MaxOutputChars * 3 / 4;
        var tail = MaxOutputChars - head;
        return output[..head] + "\n…（输出过长，已截断中间部分）…\n" + output[^tail..];
    }
}
