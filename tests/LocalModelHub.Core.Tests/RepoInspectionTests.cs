using System.Net;
using System.Net.Http.Headers;
using System.Text;
using LocalModelHub.Core.Models;
using LocalModelHub.Core.Services;
using Xunit;

namespace LocalModelHub.Core.Tests;

public class RepoInspectionTests
{
    [Fact]
    public void Parses_repo_references_from_urls_and_ids()
    {
        Assert.Equal(("HuggingFace", "Qwen/Qwen3-8B-GGUF"),
            RemoteCatalogService.ParseRepoReference("https://huggingface.co/Qwen/Qwen3-8B-GGUF/tree/main", "ModelScope"));
        Assert.Equal(("HuggingFace", "Qwen/Qwen3-8B-GGUF"),
            RemoteCatalogService.ParseRepoReference("https://hf-mirror.com/Qwen/Qwen3-8B-GGUF", "ModelScope"));
        Assert.Equal(("ModelScope", "Qwen/Qwen3-8B-GGUF"),
            RemoteCatalogService.ParseRepoReference("https://modelscope.cn/models/Qwen/Qwen3-8B-GGUF/files", "HuggingFace"));
        Assert.Equal(("HuggingFace", "unsloth/Qwen3-8B-GGUF"),
            RemoteCatalogService.ParseRepoReference("unsloth/Qwen3-8B-GGUF", "HuggingFace"));
        Assert.Equal(("ModelScope", "unsloth/Qwen3-8B-GGUF"),
            RemoteCatalogService.ParseRepoReference("unsloth/Qwen3-8B-GGUF", "ModelScope"));
        Assert.Null(RemoteCatalogService.ParseRepoReference("just-a-name", "HuggingFace"));
        Assert.Null(RemoteCatalogService.ParseRepoReference("", "HuggingFace"));
    }

    [Fact]
    public void Filters_auxiliary_split_and_orders_repo_files()
    {
        var files = new[]
        {
            new ModelFile { Path = "mmproj-F16.gguf", Bytes = 1 },
            new ModelFile { Path = "MTP/mtp-Q4_0.gguf", Bytes = 1 },
            new ModelFile { Path = "model-Q8_0-00001-of-00002.gguf", Bytes = 1 },
            new ModelFile { Path = "model-Q8_0.gguf", Bytes = 100 },
            new ModelFile { Path = "model-Q4_K_M.gguf", Bytes = 80 },
            new ModelFile { Path = "model-BF16.gguf", Bytes = 200 },
            new ModelFile { Path = "README.md", Bytes = 1 }
        };

        var options = RemoteCatalogService.BuildRepoFileOptions(files);

        Assert.Equal(["Q4_K_M", "Q8_0", "BF16"], options.Select(option => option.Label));
        Assert.Equal("model-Q4_K_M.gguf", options[0].Path);
        Assert.Contains("Q4_K_M", options[0].Display);
    }
}

public class RemoteApiServiceTests
{
    [Fact]
    public void Normalizes_base_urls()
    {
        Assert.Equal("https://api.deepseek.com/v1", RemoteApiService.NormalizeBaseUrl("https://api.deepseek.com"));
        Assert.Equal("https://api.deepseek.com/v1", RemoteApiService.NormalizeBaseUrl("https://api.deepseek.com/v1/"));
        Assert.Equal("https://api.deepseek.com/v1", RemoteApiService.NormalizeBaseUrl("https://api.deepseek.com/v1/models"));
        Assert.Equal("https://api.openai.com/v1", RemoteApiService.NormalizeBaseUrl("api.openai.com"));
        Assert.Equal("http://127.0.0.1:11434/v1", RemoteApiService.NormalizeBaseUrl("http://127.0.0.1:11434/v1/chat/completions"));
        Assert.Equal("", RemoteApiService.NormalizeBaseUrl("  "));
    }

    [Fact]
    public void Parses_model_ids_from_common_shapes()
    {
        Assert.Equal(["a", "b", "c"], RemoteApiService.ParseModelIds("""
        {"data":[{"id":"b"},{"id":"a"},{"name":"c"},{"id":"a"}]}
        """));
        Assert.Equal(["m1", "m2"], RemoteApiService.ParseModelIds("""
        {"models":[{"name":"m2"},{"model":"m1"}]}
        """));
        Assert.Equal(["x", "y"], RemoteApiService.ParseModelIds("""["y","x"]"""));
        Assert.Empty(RemoteApiService.ParseModelIds("not-json"));
    }

    [Fact]
    public async Task Lists_models_with_normalized_url_and_bearer_token()
    {
        string? uri = null;
        string? authorization = null;
        var handler = new StubHandler(request =>
        {
            uri = request.RequestUri?.ToString();
            authorization = request.Headers.Authorization?.ToString();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"data":[{"id":"deepseek-chat"}]}""", Encoding.UTF8, "application/json")
            };
        });

        var service = new RemoteApiService(handler);
        var models = await service.ListModelsAsync("https://api.example.com", "sk-test", CancellationToken.None);

        Assert.Equal("https://api.example.com/v1/models", uri);
        Assert.Equal("Bearer sk-test", authorization);
        Assert.Equal(["deepseek-chat"], models);
    }

    [Fact]
    public async Task Surfaces_api_error_status()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized)
        {
            Content = new StringContent("bad key")
        });

        var service = new RemoteApiService(handler);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.ListModelsAsync("https://api.example.com/v1", null, CancellationToken.None));

        Assert.Contains("401", error.Message);
        Assert.Contains("bad key", error.Message);
    }

    [Fact]
    public async Task Streams_remote_chat_deltas()
    {
        string? uri = null;
        string? authorization = null;
        string? body = null;
        var handler = new StubHandler(async request =>
        {
            uri = request.RequestUri?.ToString();
            authorization = request.Headers.Authorization?.ToString();
            body = request.Content is null ? null : await request.Content.ReadAsStringAsync();
            var sse = "data: {\"choices\":[{\"delta\":{\"content\":\"你\"}}]}\n\n" +
                      "data: {\"choices\":[{\"delta\":{\"content\":\"好\"}}]}\n\n" +
                      "data: [DONE]\n\n";
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(sse, Encoding.UTF8, "text/event-stream")
            };
        });

        var service = new RemoteApiService(handler);
        service.Activate("https://api.example.com", "sk-test", "deepseek-chat", "DeepSeek Chat");

        var chunks = new List<Core.Models.ChatChunk>();
        await foreach (var chunk in service.ChatStreamAsync(
                           [ChatMessage.User("hi")], new ChatOptions(0.7, 0.9, 128), CancellationToken.None))
        {
            chunks.Add(chunk);
        }

        Assert.Equal("https://api.example.com/v1/chat/completions", uri);
        Assert.Equal("Bearer sk-test", authorization);
        Assert.Contains("\"model\":\"deepseek-chat\"", body);
        Assert.Equal(["你", "好"], chunks.Select(chunk => chunk.Content));
        service.Deactivate();
        Assert.False(service.IsActive);
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, Task<HttpResponseMessage>> _responder;

        public StubHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> responder) => _responder = responder;

        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
            : this(request => Task.FromResult(responder(request)))
        {
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => _responder(request);
    }
}
