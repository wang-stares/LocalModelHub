using LocalModelHub.Core.Services;
using Xunit;

namespace LocalModelHub.Core.Tests;

/// <summary>P1-15：可选的 embedding 混合检索（RRF），默认关闭且失败时回退 BM25。</summary>
public sealed class HybridRetrievalTests
{
    [Fact]
    public async Task Hybrid_is_opt_in_and_does_not_call_embeddings_by_default()
    {
        using var fixture = new KnowledgeFixture();
        fixture.Embeddings.Calls = 0;

        var context = await fixture.Service.BuildContextAsync("番茄炒蛋", 2);

        Assert.Equal(0, fixture.Embeddings.Calls);
        Assert.NotEmpty(context.Hits);
        Assert.Equal("cooking.txt", context.Hits[0].DocumentName);
    }

    [Fact]
    public async Task Hybrid_adds_semantic_hit_that_keyword_search_cannot_find()
    {
        using var fixture = new KnowledgeFixture();

        var context = await fixture.Service.BuildContextAsync("部署", 2, hybrid: true);

        Assert.True(fixture.Embeddings.Calls > 0);
        Assert.NotEmpty(context.Hits);
        Assert.Equal("deploy.md", context.Hits[0].DocumentName);
        Assert.Contains("上线流程", context.Text);
    }

    [Fact]
    public async Task Hybrid_keeps_keyword_rank_ahead_when_both_match()
    {
        using var fixture = new KnowledgeFixture();

        var context = await fixture.Service.BuildContextAsync("番茄炒蛋", 2, hybrid: true);

        Assert.NotEmpty(context.Hits);
        Assert.Equal("cooking.txt", context.Hits[0].DocumentName);
        Assert.Contains(context.Hits, hit => hit.DocumentName == "deploy.md");
    }

    [Fact]
    public async Task Hybrid_falls_back_to_bm25_when_embeddings_fail()
    {
        using var fixture = new KnowledgeFixture();
        fixture.Embeddings.Fail = true;

        var context = await fixture.Service.BuildContextAsync("番茄炒蛋", 2, hybrid: true);

        Assert.NotEmpty(context.Hits);
        Assert.Equal("cooking.txt", context.Hits[0].DocumentName);
    }

    private sealed class KnowledgeFixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), $"lmh-hybrid-{Guid.NewGuid():N}");

        public KnowledgeFixture()
        {
            Directory.CreateDirectory(_root);
            Service = new KnowledgeService(Path.Combine(_root, "knowledge.json"));
            Embeddings = new FakeEmbeddings();
            Service.Embeddings = Embeddings;

            var cooking = Path.Combine(_root, "cooking.txt");
            File.WriteAllText(cooking, "番茄炒蛋的做法：先炒鸡蛋，再放番茄，最后加盐。");
            Service.ImportAsync(cooking, CancellationToken.None).GetAwaiter().GetResult();

            var deploy = Path.Combine(_root, "deploy.md");
            File.WriteAllText(deploy, "上线流程：先构建产物，再发布到服务器，最后做健康检查。");
            Service.ImportAsync(deploy, CancellationToken.None).GetAwaiter().GetResult();
        }

        public KnowledgeService Service { get; }
        public FakeEmbeddings Embeddings { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(_root, recursive: true);
            }
            catch
            {
                // 临时目录清理失败不影响测试
            }
        }
    }

    /// <summary>用极简二维向量模拟语义：包含「部署 / 上线」的文本靠近 query 向量。</summary>
    private sealed class FakeEmbeddings : IEmbeddingProvider
    {
        public bool Fail { get; set; }
        public int Calls { get; set; }

        public Task<IReadOnlyList<float[]>> EmbedAsync(
            IReadOnlyList<string> inputs,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            if (Fail) throw new HttpRequestException("嵌入端点不可用");
            return Task.FromResult<IReadOnlyList<float[]>>(
                inputs.Select(text => text.Contains("部署", StringComparison.Ordinal) ||
                                      text.Contains("上线", StringComparison.Ordinal)
                    ? new[] { 1f, 0.2f }
                    : new[] { 0f, 1f }).ToArray());
        }
    }
}
