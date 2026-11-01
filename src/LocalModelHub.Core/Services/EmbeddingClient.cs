using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;

namespace LocalModelHub.Core.Services;

/// <summary>文本向量提供者：知识库混合检索（P1-15）用它在需要时向本地推理服务请求向量。</summary>
public interface IEmbeddingProvider
{
    Task<IReadOnlyList<float[]>> EmbedAsync(
        IReadOnlyList<string> inputs,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// 调用 llama-server 的 OpenAI 兼容 /v1/embeddings 接口。
/// 地址、密钥与模型名通过委托实时读取，服务重启或换模型后无需重建客户端。
/// </summary>
public sealed class LlamaEmbeddingClient : IEmbeddingProvider
{
    private const int TimeoutSeconds = 30;

    private readonly HttpClient _client = new() { Timeout = TimeSpan.FromSeconds(TimeoutSeconds) };
    private readonly Func<string?> _baseUrl;
    private readonly Func<string?> _apiKey;
    private readonly Func<string?> _model;

    public LlamaEmbeddingClient(
        Func<string?> baseUrl,
        Func<string?>? apiKey = null,
        Func<string?>? model = null)
    {
        _baseUrl = baseUrl;
        _apiKey = apiKey ?? (() => null);
        _model = model ?? (() => null);
    }

    public async Task<IReadOnlyList<float[]>> EmbedAsync(
        IReadOnlyList<string> inputs,
        CancellationToken cancellationToken = default)
    {
        if (inputs.Count == 0) return [];

        var baseUrl = _baseUrl();
        if (string.IsNullOrWhiteSpace(baseUrl)) throw new InvalidOperationException("推理服务未运行");

        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"{baseUrl.TrimEnd('/')}/v1/embeddings")
        {
            Content = JsonContent.Create(new
            {
                model = _model() ?? "local",
                input = inputs
            })
        };

        var key = _apiKey();
        if (!string.IsNullOrWhiteSpace(key))
        {
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", key);
        }

        using var response = await _client.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync(cancellationToken));

        if (!document.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException("嵌入接口返回格式不正确：缺少 data 数组");
        }

        var vectors = new List<(int Index, float[] Vector)>();
        var position = 0;
        foreach (var item in data.EnumerateArray())
        {
            var index = item.TryGetProperty("index", out var indexElement) && indexElement.TryGetInt32(out var parsed)
                ? parsed
                : position;
            if (!item.TryGetProperty("embedding", out var embedding) || embedding.ValueKind != JsonValueKind.Array)
            {
                Debug.WriteLine("[Embedding] 跳过缺少 embedding 的返回项");
                position++;
                continue;
            }

            var values = embedding.EnumerateArray().Select(value => (float)value.GetDouble()).ToArray();
            if (values.Length > 0) vectors.Add((index, values));
            position++;
        }

        if (vectors.Count == 0) throw new InvalidOperationException("嵌入接口没有返回向量");

        // 服务端按 index 标注顺序；乱序时按 index 还原，保证与输入一一对应。
        var ordered = new float[vectors.Max(item => item.Index) + 1][];
        foreach (var (index, vector) in vectors)
        {
            if (index >= 0 && index < ordered.Length) ordered[index] = vector;
        }

        return ordered.Where(vector => vector is { Length: > 0 }).ToArray();
    }
}
