using System.Diagnostics;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace LocalModelHub.Core.Services;

/// <summary>微信发送方式：openclaw（本机 CLI）| webhook（企业微信群机器人）| none。</summary>
public sealed record WeChatRelayOptions(
    string Mode,
    string OpenClawCliPath,
    string Channel,
    string Target,
    string WebhookUrl);

/// <summary>
/// 把星坞里的消息发到微信：优先使用 OpenClaw CLI 的 <c>message send</c>，
/// 或退化为企业微信群机器人 Webhook。同时生成 OpenClaw 的自定义模型配置。
/// </summary>
public sealed partial class WeChatRelayService : IDisposable
{
    private const int MaxTextLength = 4000;
    private const int CommandTimeoutSeconds = 30;

    private static readonly JsonSerializerOptions ConfigOptions = new() { WriteIndented = true };

    private readonly HttpClient _client = new() { Timeout = TimeSpan.FromSeconds(20) };

    /// <summary>
    /// 生成可直接合并进 <c>~/.openclaw/openclaw.json</c> 的 JSON5 片段：
    /// 把 OpenClaw 的默认模型指向本机星坞的 OpenAI 兼容端点。
    /// </summary>
    public static string BuildOpenClawConfig(
        string baseUrl,
        string apiKey,
        string modelId,
        string providerId = "localmodelhub")
    {
        var provider = new Dictionary<string, object?>
        {
            ["baseUrl"] = baseUrl.Trim().TrimEnd('/'),
            ["apiKey"] = string.IsNullOrWhiteSpace(apiKey) ? "local" : apiKey.Trim(),
            ["api"] = "openai-completions",
            ["models"] = new[]
            {
                new Dictionary<string, object?> { ["id"] = modelId, ["name"] = modelId }
            }
        };

        var config = new Dictionary<string, object?>
        {
            ["agents"] = new Dictionary<string, object?>
            {
                ["defaults"] = new Dictionary<string, object?>
                {
                    ["model"] = new Dictionary<string, object?>
                    {
                        ["primary"] = $"{providerId}/{modelId}"
                    }
                }
            },
            ["models"] = new Dictionary<string, object?>
            {
                ["mode"] = "merge",
                ["providers"] = new Dictionary<string, object?> { [providerId] = provider }
            }
        };

        return JsonSerializer.Serialize(config, ConfigOptions);
    }

    /// <summary>OpenClaw CLI 参数：openclaw message send --channel openclaw-weixin --target &lt;id&gt; --message &lt;text&gt;。</summary>
    public static IReadOnlyList<string> BuildOpenClawArguments(string channel, string target, string text)
    {
        var normalizedChannel = NormalizeChannel(channel);
        var normalizedTarget = NormalizeTarget(target);
        var normalizedText = NormalizeText(text);
        return
        [
            "message", "send",
            "--channel", normalizedChannel,
            "--target", normalizedTarget,
            "--message", normalizedText
        ];
    }

    /// <summary>企业微信群机器人文本消息体。</summary>
    public static string BuildWeComPayload(string text) =>
        JsonSerializer.Serialize(new
        {
            msgtype = "text",
            text = new { content = NormalizeText(text) }
        });

    /// <summary>按配置发送一条消息，返回可直接展示的结果说明；失败时抛出带上下文的异常。</summary>
    public async Task<string> SendAsync(WeChatRelayOptions options, string text, CancellationToken cancellationToken)
    {
        var normalized = NormalizeText(text);
        return options.Mode switch
        {
            "openclaw" => await SendByOpenClawAsync(options, normalized, cancellationToken),
            "webhook" => await SendByWeComAsync(options.WebhookUrl, normalized, cancellationToken),
            _ => throw new InvalidOperationException("未启用微信发送：请在“手机 / 微信”页选择发送方式。")
        };
    }

    private async Task<string> SendByWeComAsync(string url, string text, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https"))
        {
            throw new InvalidOperationException("企业微信 Webhook 地址无效。");
        }

        using var response = await _client.PostAsync(
            uri,
            new StringContent(BuildWeComPayload(text), Encoding.UTF8, "application/json"),
            cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"企业微信返回 {(int)response.StatusCode}：{Truncate(body)}");
        }

        // 企业微信群机器人始终返回 200，需要用 errcode 判断业务结果。
        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.TryGetProperty("errcode", out var code) && code.GetInt32() != 0)
            {
                var message = document.RootElement.TryGetProperty("errmsg", out var errmsg)
                    ? errmsg.GetString()
                    : "未知错误";
                throw new InvalidOperationException($"企业微信拒绝发送：{message}");
            }
        }
        catch (JsonException)
        {
            // 非 JSON 响应不阻断发送结果
        }

        return "已发送到企业微信群";
    }

    private async Task<string> SendByOpenClawAsync(
        WeChatRelayOptions options,
        string text,
        CancellationToken cancellationToken)
    {
        var channel = NormalizeChannel(options.Channel);
        var args = BuildOpenClawArguments(channel, options.Target, text);
        var startInfo = BuildStartInfo(options.OpenClawCliPath, args);

        using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        var output = new StringBuilder();
        var error = new StringBuilder();
        process.OutputDataReceived += (_, e) => AppendLine(output, e.Data);
        process.ErrorDataReceived += (_, e) => AppendLine(error, e.Data);

        try
        {
            if (!process.Start()) throw new InvalidOperationException("无法启动 OpenClaw CLI。");
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or FileNotFoundException)
        {
            throw new InvalidOperationException(
                $"无法启动 OpenClaw（{ex.Message}）。请确认已安装 OpenClaw，并在设置中填写 openclaw 可执行文件路径。", ex);
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(CommandTimeoutSeconds));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch
            {
                // ignore
            }

            throw new TimeoutException($"OpenClaw 发送超时（{CommandTimeoutSeconds} 秒）。");
        }

        if (process.ExitCode != 0)
        {
            var detail = Truncate(error.Length > 0 ? error.ToString() : output.ToString());
            throw new InvalidOperationException($"OpenClaw 发送失败（退出码 {process.ExitCode}）：{detail}");
        }

        return $"已通过 OpenClaw 发送到 {channel}";
    }

    private static ProcessStartInfo BuildStartInfo(string cliPath, IReadOnlyList<string> args)
    {
        var path = string.IsNullOrWhiteSpace(cliPath) ? "openclaw" : cliPath.Trim();
        var info = new ProcessStartInfo
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        // Windows 上 npm 全局安装的是 openclaw.cmd，CreateProcess 不能直接执行批处理。
        if (OperatingSystem.IsWindows() && IsBatchFile(path))
        {
            info.FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";
            var command = string.Join(' ', new[] { path }.Concat(args).Select(QuoteForCmd));
            info.Arguments = $"/d /v:off /s /c \"{command}\"";
            return info;
        }

        info.FileName = path;
        foreach (var arg in args) info.ArgumentList.Add(arg);
        return info;
    }

    private static bool IsBatchFile(string path)
    {
        var extension = Path.GetExtension(path);
        return extension.Equals(".cmd", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".bat", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>cmd.exe 单参数转义：换行折成空格，% 变成 %%，引号加倍，整体加引号。</summary>
    private static string QuoteForCmd(string value)
    {
        var safe = value
            .Replace("\r\n", " ")
            .Replace('\r', ' ')
            .Replace('\n', ' ')
            .Replace("%", "%%")
            .Replace("\"", "\"\"");
        return $"\"{safe}\"";
    }

    private static string NormalizeChannel(string channel)
    {
        var value = (channel ?? "").Trim();
        if (!ChannelPattern().IsMatch(value))
        {
            throw new InvalidOperationException("OpenClaw 渠道名无效：只允许小写字母、数字、点、下划线与短横线。");
        }

        return value;
    }

    private static string NormalizeTarget(string target)
    {
        var value = (target ?? "").Trim();
        if (value.Length == 0)
        {
            throw new InvalidOperationException("请填写微信接收对象（联系人或会话 ID）。");
        }

        if (value.Length > 200)
        {
            throw new InvalidOperationException("微信接收对象过长。");
        }

        return value;
    }

    private static string NormalizeText(string text)
    {
        var value = (text ?? "").Trim();
        if (value.Length == 0) throw new InvalidOperationException("消息内容不能为空。");
        return value.Length <= MaxTextLength ? value : value[..MaxTextLength];
    }

    private static void AppendLine(StringBuilder builder, string? line)
    {
        if (string.IsNullOrWhiteSpace(line)) return;
        if (builder.Length > 0) builder.Append('\n');
        builder.Append(line.Trim());
    }

    private static string Truncate(string value) =>
        value.Length <= 300 ? value : value[..300] + "…";

    [GeneratedRegex("^[a-z0-9._-]{1,32}$", RegexOptions.CultureInvariant)]
    private static partial Regex ChannelPattern();

    public void Dispose() => _client.Dispose();
}
