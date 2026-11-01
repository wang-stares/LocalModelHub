using LocalModelHub.Core.Models;
using LocalModelHub.Core.Services;
using Xunit;

namespace LocalModelHub.Core.Tests;

public class CatalogSourceSplitTests
{
    private sealed class StubSource(string id, CatalogFetchResult result) : ICatalogSource
    {
        public string Id => id;

        public Task<CatalogFetchResult> FetchAsync(
            CatalogFetchContext context,
            CancellationToken cancellationToken) => Task.FromResult(result);
    }

    private sealed class ThrowingSource(string id) : ICatalogSource
    {
        public string Id => id;

        public Task<CatalogFetchResult> FetchAsync(
            CatalogFetchContext context,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("boom");
    }

    private static ModelEntry Entry(string id, long downloads) => new()
    {
        Id = id,
        Name = id,
        DisplayName = id,
        Origin = "HuggingFace",
        Downloads = downloads,
        Quants = [new ModelQuant { Label = "Q4_K_M", TotalBytes = 100 }]
    };

    [Fact]
    public void MergeSources_keeps_first_occurrence_and_skips_failures()
    {
        var results = new CatalogFetchResult[]
        {
            new("broken", [Entry("failed-model", 7)], "连接失败"),
            new("first", [Entry("duplicate", 42)]),
            new("second", [Entry("duplicate", 999), Entry("another", 5)])
        };

        var merged = CatalogMergeService.MergeSources(results);

        Assert.Equal(2, merged.Count);
        Assert.DoesNotContain(merged, model => model.Id == "failed-model");
        Assert.Equal(42, merged.First(model => model.Id == "duplicate").Downloads);
        Assert.Contains(merged, model => model.Id == "another");
    }

    [Fact]
    public async Task Sync_continues_when_one_source_fails()
    {
        var service = new RemoteCatalogService(
            sources:
            [
                new StubSource("good", new CatalogFetchResult("good", [Entry("good-model", 3)])),
                new ThrowingSource("bad")
            ]);

        var cache = await service.SyncAsync(["good", "bad"], null, CancellationToken.None);

        Assert.Single(cache.Models);
        Assert.Equal("good-model", cache.Models[0].Id);
        Assert.Single(cache.Sources, source => source.Id == "bad");
        Assert.False(string.IsNullOrWhiteSpace(cache.Sources.First(source => source.Id == "bad").Error));
        Assert.Equal(0, cache.Sources.First(source => source.Id == "bad").Count);
        Assert.Null(cache.Sources.First(source => source.Id == "good").Error);
        Assert.Equal(1, cache.Sources.First(source => source.Id == "good").Count);
    }
}
