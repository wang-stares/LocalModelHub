using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace LocalModelHub.Core.Services;

using LocalModelHub.Core.Models;

/// <summary>启动/停止 llama-server，并代理其 OpenAI 兼容的流式对话接口。</summary>
public sealed class LlamaServerService : IDisposable, IChatCompletion
{
    private readonly ConcurrentQueue<string> _log = new();
    private readonly HttpClient _healthClient = new() { Timeout = TimeSpan.FromSeconds(5) };
    private readonly HttpClient _chatClient = new() { Timeout = Timeout.InfiniteTimeSpan };
    private readonly object _sync = new();
    private Process? _process;
    private string? _alias;
    private string? _apiKey;
    private volatile bool _stopRequested;

    public event EventHandler? StateChanged;

    public bool IsRunning
    {
        get
        {
            lock (_sync)
            {
                try
                {
                    return _process is { HasExited: false };
                }
                catch
                {
                    return false;
                }
            }
        }
    }

    public int Port { get; private set; }
    public string? LoadedModelPath { get; private set; }
    public string? LoadedModelName { get; private set; }
    public string? RuntimeVariant { get; private set; }
    public string LogTail => string.Join(Environment.NewLine, _log.TakeLast(80));
    public string WebUiUrl => $"http://127.0.0.1:{Port}";

    public async Task StartAsync(
        string serverPath,
        string modelPath,
        LaunchOptions options,
        CancellationToken cancellationToken)
    {
        await StopAsync();
        _stopRequested = false;

        if (!File.Exists(serverPath)) throw new FileNotFoundException("未找到 llama-server.exe", serverPath);
        if (!File.Exists(modelPath)) throw new FileNotFoundException("未找到模型文件", modelPath);

        var arguments = BuildArguments(modelPath, options);

        var startInfo = new ProcessStartInfo
        {
            FileName = serverPath,
            Arguments = arguments,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(serverPath) ?? AppContext.BaseDirectory
        };

        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, e) => AppendLog(e.Data);
        process.ErrorDataReceived += (_, e) => AppendLog(e.Data);
        process.Exited += (_, _) =>
        {
            AppendLog("[进程已退出]");
            StateChanged?.Invoke(this, EventArgs.Empty);
        };

        AppendLog($"[启动] {serverPath}");
        AppendLog($"[参数] {arguments}");
        try
        {
            process.Start();
        }
        catch (Exception ex) when (ex is Win32Exception or FileNotFoundException or InvalidOperationException)
        {
            AppendLog($"[错误] 无法启动进程：{ex.Message}");
            process.Dispose();
            throw new InvalidOperationException(
                $"无法启动 llama-server（{ex.Message}）。请确认运行时与系统架构、CPU 指令集匹配，或在“设置”中指定可用的 llama-server.exe。",
                ex);
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        lock (_sync)
        {
            _process = process;
            _alias = options.Alias;
            _apiKey = options.ApiKey;
            Port = options.Port;
            LoadedModelPath = modelPath;
            LoadedModelName = options.Alias;
            RuntimeVariant = Path.GetFileName(Path.GetDirectoryName(serverPath));
        }

        StateChanged?.Invoke(this, EventArgs.Empty);

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(180);
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (_stopRequested)
            {
                throw new OperationCanceledException("已取消模型加载");
            }

            try
            {
                if (process.HasExited)
                {
                    // 必须先读取退出码再释放进程对象：释放后再访问 ExitCode 会抛
                    // 「No process is associated with this object」并丢失真实错误。
                    var exitCode = TryReadExitCode(process);
                    await StopCoreAsync();
                    throw new InvalidOperationException(BuildStartFailure(exitCode));
                }
            }
            catch (InvalidOperationException)
            {
                if (_stopRequested) throw new OperationCanceledException("已取消模型加载");
                throw;
            }
            catch
            {
                // 进程刚退出时 HasExited 可能抛异常，继续轮询
            }

            try
            {
                using var healthRequest = new HttpRequestMessage(
                    HttpMethod.Get, $"http://127.0.0.1:{options.Port}/health");
                ApplyAuthorization(healthRequest);
                using var response = await _healthClient.SendAsync(healthRequest, cancellationToken);
                if (response.IsSuccessStatusCode)
                {
                    AppendLog("[就绪] 模型加载完成");
                    StateChanged?.Invoke(this, EventArgs.Empty);
                    return;
                }
            }
            catch (HttpRequestException)
            {
                // 尚未开始监听
            }
            catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // 单次健康检查超时
            }

            await Task.Delay(400, cancellationToken);
        }

        await StopCoreAsync();
        throw new TimeoutException($"模型加载超时（180 秒）。\n{LogTail}");
    }

    public Task StopAsync()
    {
        _stopRequested = true;
        return StopCoreAsync();
    }

    private Task StopCoreAsync()
    {
        Process? process;
        lock (_sync)
        {
            process = _process;
            _process = null;
        }

        if (process is null) return Task.CompletedTask;

        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(5000);
            }
        }
        catch
        {
            // ignore
        }
        finally
        {
            process.Dispose();
            lock (_sync)
            {
                _alias = null;
                _apiKey = null;
                LoadedModelPath = null;
                LoadedModelName = null;
            }

            AppendLog("[已停止]");
            StateChanged?.Invoke(this, EventArgs.Empty);
        }

        return Task.CompletedTask;
    }

    /// <summary>读取已退出进程的退出码；进程对象已被释放时返回 null，避免抛出框架英文异常。</summary>
    private static int? TryReadExitCode(Process process)
    {
        try
        {
            return process.ExitCode;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private string BuildStartFailure(int? exitCode)
    {
        var head = exitCode is null ? "推理进程启动失败。" : $"推理进程启动失败（退出码 {exitCode}）。";
        var hint = ExplainExitCode(exitCode);
        var tail = LogTail;
        var parts = new[] { head, hint, tail }.Where(part => !string.IsNullOrWhiteSpace(part));
        var message = string.Join("\n", parts);
        if (string.IsNullOrWhiteSpace(tail))
        {
            message += "未捕获到 llama-server 输出：请确认运行时与 CPU 指令集匹配（可改用 Vulkan/CPU 包），或在“设置”中指定可用的 llama-server.exe。";
        }

        return message;
    }

    /// <summary>把 Windows 异常退出码翻译成可操作的中文诊断（常见于显卡驱动、运行时或模型不兼容）。</summary>
    public static string ExplainExitCode(int? exitCode) => exitCode switch
    {
        -1073741819 =>
            "Windows 访问冲突（0xC0000005）：多为显存不足、显卡驱动与 CUDA 运行时不匹配，或运行时缺少 DLL。" +
            "建议在“设置 → 推理引擎”改用 Vulkan 或 CPU 后端，并降低上下文长度后重试。",
        -1073740791 =>
            "Windows 堆损坏（0xC0000409）：运行时与系统或模型不兼容，建议更换运行时后端，必要时重新下载模型。",
        -1073741571 =>
            "栈溢出（0xC00000FD）：上下文或批处理过大，建议降低上下文长度后重试。",
        -1073741675 =>
            "整数/运算异常（0xC0000095）：建议更换运行时后端或更新推理引擎。",
        _ => ""
    };

    /// <summary>拼装 llama-server 启动参数；对来自设置的枚举值做白名单校验，避免拼出非法参数。</summary>
    public static string BuildArguments(string modelPath, LaunchOptions options)
    {
        var builder = new StringBuilder();
        builder.Append($"-m \"{modelPath}\" --host {options.Host} --port {options.Port} -c {options.ContextSize}");
        builder.Append(options.GpuLayers < 0 ? " -ngl auto" : $" -ngl {options.GpuLayers}");
        builder.Append(" --jinja");

        var flashAttention = Normalize(options.FlashAttention, ["on", "off"], "auto");
        if (flashAttention != "auto") builder.Append($" -fa {flashAttention}");

        var cacheK = NormalizeCacheType(options.CacheTypeK);
        var cacheV = NormalizeCacheType(options.CacheTypeV);
        if (cacheK != "f16" || cacheV != "f16")
        {
            builder.Append($" -ctk {cacheK} -ctv {cacheV}");
        }

        if (options.CpuMoeLayers > 0)
        {
            builder.Append($" --n-cpu-moe {Math.Clamp(options.CpuMoeLayers, 1, 999)}");
        }

        if (options.ParallelSlots > 0)
        {
            builder.Append($" -np {Math.Clamp(options.ParallelSlots, 1, 64)}");
        }

        var loadMode = Normalize(options.LoadMode, ["mmap", "mlock", "mmap+mlock"], "auto");
        if (loadMode != "auto" && loadMode != "mmap") builder.Append($" -lm {loadMode}");

        var reasoning = Normalize(options.ReasoningFormat, ["none", "deepseek", "deepseek-legacy"], "deepseek");
        builder.Append($" --reasoning-format {reasoning}");

        if (options.EnableMetrics) builder.Append(" --metrics");
        if (options.Threads > 0) builder.Append($" -t {options.Threads}");
        builder.Append($" --alias \"{options.Alias}\"");
        if (!string.IsNullOrWhiteSpace(options.ApiKey)) builder.Append($" --api-key \"{options.ApiKey}\"");

        return builder.ToString();
    }

    private static string Normalize(string? value, string[] allowed, string fallback)
    {
        var text = (value ?? "").Trim().ToLowerInvariant();
        return allowed.Contains(text) ? text : fallback;
    }

    private static string NormalizeCacheType(string? value)
    {
        var text = (value ?? "").Trim().ToLowerInvariant();
        return text is "f32" or "f16" or "bf16" or "q8_0" or "q4_0" or "q4_1" or "iq4_nl" or "q5_0" or "q5_1"
            ? text
            : "f16";
    }

    public async IAsyncEnumerable<ChatChunk> ChatStreamAsync(
        IReadOnlyList<ChatMessage> messages,
        ChatOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (!IsRunning) throw new InvalidOperationException("推理服务尚未启动。");

        var payload = new
        {
            model = _alias ?? LoadedModelName ?? "local",
            messages = messages.Select(SerializeMessage).ToArray(),
            stream = true,
            temperature = options.Temperature,
            top_p = options.TopP,
            max_tokens = options.MaxTokens
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{Port}/v1/chat/completions")
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        ApplyAuthorization(request);

        using var response = await _chatClient.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException($"推理服务返回 {(int)response.StatusCode}：{body}");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream);
        while (!reader.EndOfStream)
        {
            var line = await reader.ReadLineAsync(cancellationToken);
            if (string.IsNullOrWhiteSpace(line) || !line.StartsWith("data:", StringComparison.Ordinal)) continue;

            var data = line[5..].Trim();
            if (data == "[DONE]") break;

            var chunk = ChatStreamParser.Parse(data);
            if (chunk is null) continue;

            yield return chunk;
            await Task.Yield();
        }
    }

    /// <summary>非流式对话，可携带 tools；返回文本与工具调用（用于工作文件夹代理）。</summary>
    public async Task<ChatTurn> ChatAsync(
        IReadOnlyList<ChatMessage> messages,
        IReadOnlyList<ToolDefinition>? tools,
        int maxTokens,
        CancellationToken cancellationToken)
    {
        if (!IsRunning) throw new InvalidOperationException("推理服务尚未启动。");

        var payload = new Dictionary<string, object?>
        {
            ["model"] = _alias ?? LoadedModelName ?? "local",
            ["messages"] = messages.Select(SerializeMessage).ToArray(),
            ["stream"] = false,
            ["temperature"] = 0.3,
            ["top_p"] = 0.9,
            ["max_tokens"] = maxTokens
        };

        if (tools is { Count: > 0 })
        {
            payload["tools"] = tools.Select(tool => new
            {
                type = "function",
                function = new
                {
                    name = tool.Name,
                    description = tool.Description,
                    parameters = tool.Parameters
                }
            }).ToArray();
            payload["tool_choice"] = "auto";
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{Port}/v1/chat/completions")
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
        };
        ApplyAuthorization(request);

        using var response = await _chatClient.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"推理服务返回 {(int)response.StatusCode}：{body}");
        }

        using var document = JsonDocument.Parse(body);
        var turn = new ChatTurn();
        if (!document.RootElement.TryGetProperty("choices", out var choices) ||
            choices.ValueKind != JsonValueKind.Array ||
            choices.GetArrayLength() == 0)
        {
            return turn;
        }

        var message = choices[0].GetProperty("message");
        if (message.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String)
        {
            turn.Content = content.GetString() ?? "";
        }

        if (message.TryGetProperty("reasoning_content", out var reasoning) &&
            reasoning.ValueKind == JsonValueKind.String)
        {
            turn.Reasoning = reasoning.GetString() ?? "";
        }

        if (message.TryGetProperty("tool_calls", out var calls) && calls.ValueKind == JsonValueKind.Array)
        {
            foreach (var call in calls.EnumerateArray())
            {
                if (!call.TryGetProperty("function", out var function)) continue;

                var id = call.TryGetProperty("id", out var idElement) ? idElement.GetString() ?? "" : "";
                var name = function.TryGetProperty("name", out var nameElement) ? nameElement.GetString() ?? "" : "";
                var arguments = function.TryGetProperty("arguments", out var argumentsElement)
                    ? argumentsElement.ValueKind == JsonValueKind.String
                        ? argumentsElement.GetString() ?? "{}"
                        : argumentsElement.GetRawText()
                    : "{}";

                if (name.Length > 0) turn.ToolCalls.Add(new ToolCall(id, name, arguments));
            }
        }

        return turn;
    }

    private static object SerializeMessage(ChatMessage message)
    {
        var payload = new Dictionary<string, object?>
        {
            ["role"] = message.Role,
            ["content"] = message.Content
        };

        if (message.ToolCalls is { Count: > 0 })
        {
            payload["tool_calls"] = message.ToolCalls.Select(call => new
            {
                id = call.Id,
                type = "function",
                function = new { name = call.Name, arguments = call.Arguments }
            }).ToArray();
        }

        if (!string.IsNullOrWhiteSpace(message.ToolCallId))
        {
            payload["tool_call_id"] = message.ToolCallId;
        }

        return payload;
    }

    private void ApplyAuthorization(HttpRequestMessage request)
    {
        var key = _apiKey;
        if (!string.IsNullOrWhiteSpace(key))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        }
    }

    private void AppendLog(string? line)
    {
        if (string.IsNullOrWhiteSpace(line)) return;
        _log.Enqueue(line);
        while (_log.Count > 500) _log.TryDequeue(out _);
    }

    public void Dispose()
    {
        try
        {
            StopAsync().GetAwaiter().GetResult();
        }
        catch
        {
            // ignore
        }

        _healthClient.Dispose();
        _chatClient.Dispose();
    }
}

/// <summary>解析 OpenAI 兼容 SSE 的单个 data 事件，兼容 content / reasoning_content / reasoning 三种字段。</summary>
public static class ChatStreamParser
{
    public static ChatChunk? Parse(string data)
    {
        if (string.IsNullOrWhiteSpace(data) || data == "[DONE]") return null;

        try
        {
            using var document = JsonDocument.Parse(data);
            if (!document.RootElement.TryGetProperty("choices", out var choices) ||
                choices.ValueKind != JsonValueKind.Array ||
                choices.GetArrayLength() == 0)
            {
                return null;
            }

            if (!choices[0].TryGetProperty("delta", out var delta) || delta.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var content = ReadString(delta, "content");
            var reasoning = ReadString(delta, "reasoning_content") ?? ReadString(delta, "reasoning");
            if (string.IsNullOrEmpty(content) && string.IsNullOrEmpty(reasoning)) return null;
            return new ChatChunk(content, reasoning);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? ReadString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
