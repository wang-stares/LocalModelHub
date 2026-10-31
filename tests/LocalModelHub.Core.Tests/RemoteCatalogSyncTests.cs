using System.Collections.Concurrent;
using System.Net;
using LocalModelHub.Core.Models;
using LocalModelHub.Core.Services;
using Xunit;

namespace LocalModelHub.Core.Tests;

public class RemoteCatalogSyncTests
{
    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<string, string?> _respond;

        public StubHandler(Func<string, string?> respond) => _respond = respond;

        public ConcurrentBag<string> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.ToString();
            Requests.Add(url);
            var body = _respond(url);
            return Task.FromResult(body is null
                ? new HttpResponseMessage(HttpStatusCode.NotFound)
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
        }
    }

    [Fact]
    public async Task OllamaSync_SecondRun_OnlyRefreshesListing()
    {
        var handler = new StubHandler(url =>
        {
            if (url.Contains("/library?sort=popular")) return """<a href="/library/llama3.2">llama3.2</a>""";
            if (url.Contains("/library/llama3.2/tags")) return """<a href="/library/llama3.2:3.2b-q4_K_M">""";
            if (url.Contains("/manifests/")) return """
                {"layers":[{"mediaType":"application/vnd.ollama.image.model","digest":"sha256:abc","size":123456789}]}
                """;
            return null;
        });

        var service = new RemoteCatalogService(handler);
        var first = await service.SyncAsync([RemoteCatalogService.OllamaId], null, CancellationToken.None);
        Assert.NotEmpty(first.Models);
        Assert.True(handler.Requests.Count > 1);

        handler.Requests.Clear();
        var second = await service.SyncAsync([RemoteCatalogService.OllamaId], null, CancellationToken.None, first);

        Assert.Single(handler.Requests);
        Assert.Equal(first.Models.Count, second.Models.Count);
    }

    [Fact]
    public async Task HuggingFaceSync_ReusesCachedEntryWithoutTreeRequest()
    {
        var handler = new StubHandler(url =>
        {
            if (url.Contains("/api/models?filter=gguf"))
            {
                return """[{"id":"Qwen/Qwen3-8B-GGUF","downloads":5000,"tags":["gguf","text-generation"]}]""";
            }

            if (url.Contains("/tree/main")) return """[{"path":"Qwen3-8B-Q4_K_M.gguf","size":4980000000}]""";
            return null;
        });

        var service = new RemoteCatalogService(handler);
        var first = await service.SyncAsync([RemoteCatalogService.HuggingFaceId], null, CancellationToken.None);
        Assert.NotEmpty(first.Models);

        handler.Requests.Clear();
        var second = await service.SyncAsync([RemoteCatalogService.HuggingFaceId], null, CancellationToken.None, first);

        Assert.DoesNotContain(handler.Requests, url => url.Contains("/tree/main"));
        Assert.NotEmpty(second.Models);
    }

    [Fact]
    public async Task HuggingFaceSync_DropsNonTextRepos()
    {
        var handler = new StubHandler(url =>
        {
            if (url.Contains("/api/models?filter=gguf"))
            {
                return """
                    [
                      {"id":"Qwen/Qwen3-8B-GGUF","downloads":5000,"tags":["gguf"]},
                      {"id":"black-forest-labs/FLUX-GGUF","downloads":9000,"tags":["gguf","text-to-image"]}
                    ]
                    """;
            }

            if (url.Contains("/tree/main")) return """[{"path":"Qwen3-8B-Q4_K_M.gguf","size":4980000000}]""";
            return null;
        });

        var service = new RemoteCatalogService(handler);
        var cache = await service.SyncAsync([RemoteCatalogService.HuggingFaceId], null, CancellationToken.None);

        Assert.DoesNotContain(cache.Models, model => (model.HfRepo ?? "").Contains("FLUX"));
    }
}
