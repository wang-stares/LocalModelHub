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

    /// <summary>流式生成连续 60 秒没有新内容即判定服务假死。</summary>
    private static readonly TimeSpan StreamIdleTimeout = TimeSpan.FromSeconds(60);
    private Process? _process;
    private string? _alias;
    private string? _apiKey;
    private string? _apiKeyFile;
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

    /// <summary>供 <see cref="IChatCompletion.ModelName"/> 使用：按模型覆盖提示词时取实际加载的模型名。</summary>
    string? IChatCompletion.ModelName => LoadedModelName;

    /// <summary>服务端 /props 报告的实际上下文窗口；/props 不可用时退化为启动参数里的 ContextSize。</summary>
    public int ContextTokens { get; private set; } = 8192;

    /// <summary>最近一次 /props 结果（含 chat template），用于模型能力探测。</summary>
    public ServerProps? Props { get; private set; }

    public string LogTail => string.Join(Environment.NewLine, _log.TakeLast(80));
    public string WebUiUrl => $"http://127.0.0.1:{Port}";

    /// <summary>/props 中与上下文预算相关的字段。</summary>
    public sealed record ServerProps(int NCtx, string ChatTemplate, int TotalSlots);

    /// <summary>
    /// 读取服务端真实 n_ctx。启动参数里的 -c 可能被 llama-server 按显存调整，
    /// 只有 /props 的值才是实际可用的上下文窗口。
    /// </summary>
    public async Task<ServerProps?> GetPropsAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{Port}/props");
            ApplyAuthorization(request);
            using var response = await _healthClient.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode) return null;

            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            var root = document.RootElement;

            var nCtx = 0;
            if (root.TryGetProperty("default_generation_settings", out var settings) &&
                settings.TryGetProperty("n_ctx", out var nested) && nested.TryGetInt32(out var nestedValue))
            {
                nCtx = nestedValue;
            }
            else if (root.TryGetProperty("n_ctx", out var direct) && direct.TryGetInt32(out var directValue))
            {
                nCtx = directValue;
            }

            var template = root.TryGetProperty("chat_template", out var t) ? t.GetString() ?? "" : "";
            var slots = root.TryGetProperty("total_slots", out var s) && s.TryGetInt32(out var slotCount)
                ? slotCount
                : 1;
            return nCtx > 0 ? new ServerProps(nCtx, template, slots) : null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // /props 在旧版本或非 llama.cpp 后端上可能不存在：调用方回退到启动参数
            return null;
        }
    }

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

        // API Key 不再走命令行（进程列表可见）：写入仅当前用户可读的临时文件，退出时删除。
        string? apiKeyFile = null;
        if (!string.IsNullOrWhiteSpace(options.ApiKey))
        {
            try
            {
                apiKeyFile = CreateApiKeyFile(options.ApiKey);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                AppendLog($"[错误] 无法创建 API Key 临时文件：{ex.Message}");
                throw new InvalidOperationException(
                    $"无法创建 API Key 临时文件（{ex.Message}）。请检查用户配置目录的写入权限后重试。", ex);
            }
        }

        var arguments = BuildArguments(modelPath, options, apiKeyFile);

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
            DeleteApiKeyFile(apiKeyFile);
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
            _apiKeyFile = apiKeyFile;
            Port = options.Port;
            LoadedModelPath = modelPath;
            LoadedModelName = options.Alias;
            RuntimeVariant = Path.GetFileName(Path.GetDirectoryName(serverPath));
            ContextTokens = options.ContextSize > 0 ? options.ContextSize : 8192;
        }

        StateChanged?.Invoke(this, EventArgs.Empty);
        AppLog.Info("server", $"等待服务就绪：端口 {options.Port} · 最长 180 s");

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
                    // /health 200 不代表上下文已就绪；/props 拿不到时退回启动参数。
                    var props = await GetPropsAsync(cancellationToken);
                    Props = props;
                    ContextTokens = props is { NCtx: > 0 } ? props.NCtx : options.ContextSize;
                    AppendLog($"[就绪] 模型加载完成（上下文 {ContextTokens} token）");
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
        AppLog.Error("server", $"模型加载超时（180 秒）：{LogTail}");
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
            string? keyFile;
            lock (_sync)
            {
                _alias = null;
                _apiKey = null;
                keyFile = _apiKeyFile;
                _apiKeyFile = null;
                LoadedModelPath = null;
                LoadedModelName = null;
                Props = null;
            }

            DeleteApiKeyFile(keyFile);
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
    public static string BuildArguments(string modelPath, LaunchOptions options) =>
        BuildArguments(modelPath, options, null);

    /// <inheritdoc cref="BuildArguments(string, LaunchOptions)"/>
    /// <param name="apiKeyFile">API Key 临时文件路径；提供后使用 --api-key-file，避免密钥出现在进程列表。</param>
    public static string BuildArguments(string modelPath, LaunchOptions options, string? apiKeyFile)
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
        // 只通过文件传递密钥：--api-key 会把明文暴露在进程列表与命令行历史里。
        if (!string.IsNullOrWhiteSpace(apiKeyFile))
        {
            builder.Append($" --api-key-file \"{apiKeyFile}\"");
        }

        return builder.ToString();
    }

    /// <summary>
    /// 把 API Key 写入临时文件：Unix 上权限 600，Windows 放在当前用户的 LocalApplicationData 且设为隐藏。
    /// 文件由 <see cref="DeleteApiKeyFile"/> 在服务停止时删除。
    /// </summary>
    public static string CreateApiKeyFile(string apiKey)
    {
        var directory = Path.Combine(AppPaths.RootDirectory, "secrets");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"api-key-{Guid.NewGuid():N}.txt");
        File.WriteAllText(path, apiKey.Trim(), new UTF8Encoding(false));

        if (OperatingSystem.IsWindows())
        {
            try
            {
                File.SetAttributes(path, FileAttributes.Hidden);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[LlamaServer] 设置密钥文件隐藏属性失败：{ex.Message}");
            }
        }
        else
        {
            try
            {
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[LlamaServer] 设置密钥文件权限失败：{ex.Message}");
            }
        }

        return path;
    }

    /// <summary>删除启动用的 API Key 临时文件；已删除或路径为空时静默返回。</summary>
    public static void DeleteApiKeyFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[LlamaServer] 删除密钥文件失败：{ex.Message}");
        }
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

    /// <summary>把内部超时与用户主动取消区分开：用户取消继续抛 OperationCanceledException。</summary>
    private static async Task<T> WithTimeout<T>(Task<T> task, CancellationToken userToken, string message)
    {
        try
        {
            return await task;
        }
        catch (OperationCanceledException ex) when (!userToken.IsCancellationRequested)
        {
            throw new TimeoutException(message, ex);
        }
    }

    /// <summary>ValueTask 版本（TextReader.ReadLineAsync(CancellationToken) 等 API 使用）。</summary>
    private static async Task<T> WithTimeout<T>(ValueTask<T> task, CancellationToken userToken, string message)
    {
        try
        {
            return await task;
        }
        catch (OperationCanceledException ex) when (!userToken.IsCancellationRequested)
        {
            throw new TimeoutException(message, ex);
        }
    }

    private static string TruncateBody(string? body)
    {
        var text = (body ?? "").Trim();
        return text.Length <= 400 ? text : text[..400] + "…";
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

        // 流式场景按「无新内容」计时：首 token 之前或生成中途卡住超过 60 秒就中止，
        // 避免服务假死时界面永久转圈。用户主动取消仍抛 OperationCanceledException。
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        linked.CancelAfter(StreamIdleTimeout);
        var timeoutMessage =
            $"生成超过 {StreamIdleTimeout.TotalSeconds:0} 秒没有新内容，已停止；模型可能卡住或显存不足。";

        using var response = await WithTimeout(
            _chatClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, linked.Token),
            cancellationToken, timeoutMessage);
        if (!response.IsSuccessStatusCode)
        {
            var body = await WithTimeout(
                response.Content.ReadAsStringAsync(linked.Token), cancellationToken, timeoutMessage);
            throw new InvalidOperationException($"推理服务返回 {(int)response.StatusCode}：{TruncateBody(body)}");
        }

        await using var stream = await WithTimeout(
            response.Content.ReadAsStreamAsync(linked.Token), cancellationToken, timeoutMessage);
        using var reader = new StreamReader(stream);
        while (true)
        {
            var line = await WithTimeout(
                reader.ReadLineAsync(linked.Token), cancellationToken, timeoutMessage);
            if (line is null) break;

            linked.CancelAfter(StreamIdleTimeout); // 收到新行后重新计时
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
    public Task<ChatTurn> ChatAsync(
        IReadOnlyList<ChatMessage> messages,
        IReadOnlyList<ToolDefinition>? tools,
        int maxTokens,
        CancellationToken cancellationToken) =>
        ChatAsync(messages, tools, maxTokens, new ChatRequestOptions(), cancellationToken);

    /// <summary>
    /// 带扩展选项的对话：OnDelta 非空时改为 SSE 流式读取并逐块回调正文，
    /// JsonMode / JsonSchema 打开 llama.cpp 的约束解码（response_format）。
    /// </summary>
    public async Task<ChatTurn> ChatAsync(
        IReadOnlyList<ChatMessage> messages,
        IReadOnlyList<ToolDefinition>? tools,
        int maxTokens,
        ChatRequestOptions requestOptions,
        CancellationToken cancellationToken)
    {
        if (!IsRunning) throw new InvalidOperationException("推理服务尚未启动。");

        var streaming = requestOptions.OnDelta is not null;
        var payload = BuildChatPayload(messages, tools, maxTokens, requestOptions, streaming);

        using var request = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{Port}/v1/chat/completions")
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
        };
        if (streaming) request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        ApplyAuthorization(request);

        var watch = Stopwatch.StartNew();
        try
        {
            var turn = streaming
                ? await ChatStreamingCoreAsync(request, requestOptions, cancellationToken)
                : await ChatBufferedCoreAsync(request, maxTokens, cancellationToken);
            watch.Stop();
            AppLog.Info("llm",
                $"{messages.Count} 条消息 · tools={tools?.Count ?? 0} · max_tokens={maxTokens}" +
                $" · {watch.ElapsedMilliseconds} ms · 输出 {turn.Content.Length} 字 · tool_calls={turn.ToolCalls.Count}");
            return turn;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            AppLog.Error("llm", $"请求失败（{watch.ElapsedMilliseconds} ms）", ex);
            throw;
        }
    }

    private Dictionary<string, object?> BuildChatPayload(
        IReadOnlyList<ChatMessage> messages,
        IReadOnlyList<ToolDefinition>? tools,
        int maxTokens,
        ChatRequestOptions requestOptions,
        bool stream)
    {
        var payload = new Dictionary<string, object?>
        {
            ["model"] = _alias ?? LoadedModelName ?? "local",
            ["messages"] = messages.Select(SerializeMessage).ToArray(),
            ["stream"] = stream,
            ["temperature"] = requestOptions.Temperature,
            ["top_p"] = requestOptions.TopP,
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

        if (requestOptions.JsonMode || !string.IsNullOrWhiteSpace(requestOptions.JsonSchema))
        {
            payload["response_format"] = BuildResponseFormat(requestOptions.JsonSchema);
        }

        return payload;
    }

    /// <summary>json_object 只约束「输出是 JSON」；json_schema 进一步约束字段与类型。</summary>
    private static Dictionary<string, object?> BuildResponseFormat(string? jsonSchema)
    {
        if (string.IsNullOrWhiteSpace(jsonSchema))
        {
            return new Dictionary<string, object?> { ["type"] = "json_object" };
        }

        JsonElement schema;
        try
        {
            schema = JsonSerializer.Deserialize<JsonElement>(jsonSchema);
        }
        catch (JsonException)
        {
            return new Dictionary<string, object?> { ["type"] = "json_object" };
        }

        return new Dictionary<string, object?>
        {
            ["type"] = "json_schema",
            ["json_schema"] = new Dictionary<string, object?>
            {
                ["name"] = "agent_verification",
                ["strict"] = true,
                ["schema"] = schema
            }
        };
    }

    private async Task<ChatTurn> ChatBufferedCoreAsync(
        HttpRequestMessage request,
        int maxTokens,
        CancellationToken cancellationToken)
    {
        // 8B 模型约 20 tok/s：给足生成时间，但服务卡死时不能让调用方无限等待。
        var timeout = TimeSpan.FromSeconds(Math.Clamp(maxTokens * 0.35, 30, 300));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        linked.CancelAfter(timeout);
        string body;
        try
        {
            using var response = await _chatClient.SendAsync(request, linked.Token);
            body = await response.Content.ReadAsStringAsync(linked.Token);
            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException($"推理服务返回 {(int)response.StatusCode}：{TruncateBody(body)}");
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException ex)
        {
            throw new TimeoutException(
                $"推理超时（{timeout.TotalSeconds:0} 秒），模型可能卡住或显存不足。", ex);
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

    private async Task<ChatTurn> ChatStreamingCoreAsync(
        HttpRequestMessage request,
        ChatRequestOptions requestOptions,
        CancellationToken cancellationToken)
    {
        // 与 ChatStreamAsync 相同：按「无新内容」计时，避免服务假死时永久等待；
        // 用户主动取消仍抛 OperationCanceledException，保证停止按钮立即生效。
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        linked.CancelAfter(StreamIdleTimeout);
        var timeoutMessage =
            $"生成超过 {StreamIdleTimeout.TotalSeconds:0} 秒没有新内容，已停止；模型可能卡住或显存不足。";

        using var response = await WithTimeout(
            _chatClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, linked.Token),
            cancellationToken, timeoutMessage);
        if (!response.IsSuccessStatusCode)
        {
            var body = await WithTimeout(
                response.Content.ReadAsStringAsync(CancellationToken.None), cancellationToken, timeoutMessage);
            throw new InvalidOperationException($"推理服务返回 {(int)response.StatusCode}：{TruncateBody(body)}");
        }

        await using var stream = await WithTimeout(
            response.Content.ReadAsStreamAsync(linked.Token), cancellationToken, timeoutMessage);
        using var reader = new StreamReader(stream);
        var turn = new ChatTurnAccumulator();
        while (true)
        {
            var line = await WithTimeout(
                reader.ReadLineAsync(linked.Token), cancellationToken, timeoutMessage);
            if (line is null) break;

            linked.CancelAfter(StreamIdleTimeout);
            if (string.IsNullOrWhiteSpace(line) || !line.StartsWith("data:", StringComparison.Ordinal)) continue;

            var data = line[5..].Trim();
            if (data == "[DONE]") break;

            var chunk = ChatStreamParser.Parse(data);
            if (chunk is null) continue;

            turn.Append(chunk);
            if (chunk.Content is { Length: > 0 }) requestOptions.OnDelta?.Invoke(chunk.Content);
        }

        return turn.Build();
    }

    /// <summary>把流式片段拼回完整的 ChatTurn：正文、思考与按 index 合并的工具调用。</summary>
    private sealed class ChatTurnAccumulator
    {
        private readonly StringBuilder _content = new();
        private readonly StringBuilder _reasoning = new();
        private readonly Dictionary<int, PendingCall> _calls = new();

        public void Append(ChatChunk chunk)
        {
            if (!string.IsNullOrEmpty(chunk.Content)) _content.Append(chunk.Content);
            if (!string.IsNullOrEmpty(chunk.Reasoning)) _reasoning.Append(chunk.Reasoning);
            if (chunk.ToolCall is not { } delta) return;

            if (!_calls.TryGetValue(delta.Index, out var call))
            {
                call = new PendingCall();
                _calls[delta.Index] = call;
            }

            if (!string.IsNullOrEmpty(delta.Id)) call.Id = delta.Id;
            if (!string.IsNullOrEmpty(delta.Name)) call.Name = delta.Name;
            if (!string.IsNullOrEmpty(delta.Arguments)) call.Arguments.Append(delta.Arguments);
        }

        public ChatTurn Build()
        {
            var turn = new ChatTurn
            {
                Content = _content.ToString(),
                Reasoning = _reasoning.ToString()
            };

            foreach (var pair in _calls.OrderBy(pair => pair.Key))
            {
                var call = pair.Value;
                if (call.Name.Length == 0) continue;
                var arguments = call.Arguments.Length == 0 ? "{}" : call.Arguments.ToString();
                turn.ToolCalls.Add(new ToolCall(call.Id.Length > 0 ? call.Id : $"call_{pair.Key}", call.Name, arguments));
            }

            return turn;
        }

        private sealed class PendingCall
        {
            public string Id { get; set; } = "";
            public string Name { get; set; } = "";
            public StringBuilder Arguments { get; } = new();
        }
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
        var text = line.Trim();
        _log.Enqueue(text);
        while (_log.Count > 500) _log.TryDequeue(out _);
        // 同时写持久化日志（启动参数脱敏、保留 7 天），出问题时可复盘。
        AppLog.Info("server", text);
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
            var toolCall = ReadToolCallDelta(delta);
            if (string.IsNullOrEmpty(content) && string.IsNullOrEmpty(reasoning) && toolCall is null) return null;
            return new ChatChunk(content, reasoning, toolCall);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>解析流式工具调用片段（delta.tool_calls）：index 缺省按 0，arguments 可能是分片字符串。</summary>
    private static ToolCallDelta? ReadToolCallDelta(JsonElement delta)
    {
        if (!delta.TryGetProperty("tool_calls", out var calls) || calls.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var call in calls.EnumerateArray())
        {
            var index = call.TryGetProperty("index", out var indexElement) && indexElement.TryGetInt32(out var value)
                ? value
                : 0;
            var id = ReadString(call, "id");
            string? name = null;
            string? arguments = null;
            if (call.TryGetProperty("function", out var function) && function.ValueKind == JsonValueKind.Object)
            {
                name = ReadString(function, "name");
                arguments = ReadString(function, "arguments");
            }

            if (name is null && arguments is null && id is null) continue;
            return new ToolCallDelta(index, id, name, arguments);
        }

        return null;
    }

    private static string? ReadString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
