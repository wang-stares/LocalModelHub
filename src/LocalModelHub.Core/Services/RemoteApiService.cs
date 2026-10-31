using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace LocalModelHub.Core.Services;

using LocalModelHub.Core.Models;

/// <summary>OpenAI 兼容外部 API：拉取模型清单，并把对话请求转发到远端模型。</summary>
public sealed class RemoteApiService
{
    private readonly HttpClient _listClient;
    private readonly HttpClient _chatClient;
    private string? _apiKey;

    public RemoteApiService(HttpMessageHandler? handler = null)
    {
        handler ??= new SocketsHttpHandler
        {
            AllowAutoRedirect = true,
            AutomaticDecompression = DecompressionMethods.All,
            ConnectTimeout = TimeSpan.FromSeconds(10)
        };

        _listClient = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
        _chatClient = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        _listClient.DefaultRequestHeaders.UserAgent.ParseAdd("LocalModelHub/1.5");
        _chatClient.DefaultRequestHeaders.UserAgent.ParseAdd("LocalModelHub/1.5");
    }

    public event EventHandler? ActiveChanged;

    public bool IsActive { get; private set; }
    public string BaseUrl { get; private set; } = "";
    public string ModelId { get; private set; } = "";
    public string DisplayName { get; private set; } = "";

    /// <summary>把用户输入的地址规范化为 OpenAI 兼容的 /v1 根路径。</summary>
    public static string NormalizeBaseUrl(string? input)
    {
        var text = (input ?? "").Trim();
        if (text.Length == 0) return "";
        if (!text.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !text.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            text = "https://" + text;
        }

        text = text.TrimEnd('/');
        if (text.EndsWith("/models", StringComparison.OrdinalIgnoreCase)) text = text[..^7].TrimEnd('/');
        if (text.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase)) text = text[..^17].TrimEnd('/');
        if (!text.EndsWith("/v1", StringComparison.OrdinalIgnoreCase)) text += "/v1";
        return text;
    }

    /// <summary>解析 /models 响应，兼容 data / models 数组与裸数组。</summary>
    public static IReadOnlyList<string> ParseModelIds(string json)
    {
        var ids = new List<string>();
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Array)
            {
                Collect(root, ids);
            }
            else if (root.ValueKind == JsonValueKind.Object)
            {
                if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
                {
                    Collect(data, ids);
                }
                else if (root.TryGetProperty("models", out var models) && models.ValueKind == JsonValueKind.Array)
                {
                    Collect(models, ids);
                }
            }
        }
        catch (JsonException)
        {
            return [];
        }

        return ids
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static void Collect(JsonElement array, List<string> ids)
    {
        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
            {
                var text = item.GetString();
                if (!string.IsNullOrWhiteSpace(text)) ids.Add(text.Trim());
                continue;
            }

            if (item.ValueKind != JsonValueKind.Object) continue;
            foreach (var key in new[] { "id", "model", "name" })
            {
                if (item.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String)
                {
                    var text = value.GetString();
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        ids.Add(text.Trim());
                        break;
                    }
                }
            }
        }
    }

    public async Task<IReadOnlyList<string>> ListModelsAsync(
        string baseUrl,
        string? apiKey,
        CancellationToken cancellationToken)
    {
        var normalized = NormalizeBaseUrl(baseUrl);
        if (normalized.Length == 0) throw new InvalidOperationException("请填写 API 地址");

        using var request = new HttpRequestMessage(HttpMethod.Get, normalized + "/models");
        ApplyAuthorization(request, apiKey);
        using var response = await _listClient.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"API 返回 {(int)response.StatusCode}：{Truncate(body, 200)}");
        }

        var ids = ParseModelIds(body);
        if (ids.Count == 0) throw new InvalidOperationException("API 没有返回任何模型");
        return ids;
    }

    public void Activate(string baseUrl, string? apiKey, string modelId, string displayName)
    {
        BaseUrl = NormalizeBaseUrl(baseUrl);
        ModelId = modelId;
        DisplayName = displayName;
        _apiKey = apiKey;
        IsActive = true;
        ActiveChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Deactivate()
    {
        if (!IsActive) return;
        IsActive = false;
        _apiKey = null;
        ActiveChanged?.Invoke(this, EventArgs.Empty);
    }

    public async IAsyncEnumerable<ChatChunk> ChatStreamAsync(
        IReadOnlyList<ChatMessage> messages,
        ChatOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (!IsActive) throw new InvalidOperationException("尚未连接外部 API。");

        var payload = new
        {
            model = ModelId,
            messages = messages.Select(SerializeMessage).ToArray(),
            stream = true,
            temperature = options.Temperature,
            top_p = options.TopP,
            max_tokens = options.MaxTokens
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, BaseUrl + "/chat/completions")
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        ApplyAuthorization(request, _apiKey);

        using var response = await _chatClient.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException($"API 返回 {(int)response.StatusCode}：{Truncate(body, 300)}");
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

    private static void ApplyAuthorization(HttpRequestMessage request, string? apiKey)
    {
        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey.Trim());
        }
    }

    private static string Truncate(string text, int max) =>
        text.Length <= max ? text : text[..max] + "…";
}
