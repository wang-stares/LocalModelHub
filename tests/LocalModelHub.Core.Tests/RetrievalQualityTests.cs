using System.Text.Json;
using LocalModelHub.Core.Models;
using LocalModelHub.Core.Services;
using Xunit;

namespace LocalModelHub.Core.Tests;

/// <summary>P1-15：知识库倒排索引与查询缓存、记忆时间衰减与冲突检测。</summary>
public sealed class RetrievalQualityTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"lmh-retrieval-{Guid.NewGuid():N}");

    public RetrievalQualityTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task Knowledge_index_is_built_once_and_identical_queries_hit_cache()
    {
        var docPath = Path.Combine(_root, "guide.md");
        await File.WriteAllTextAsync(docPath,
            "# 本地推理\nllama.cpp 的 KV cache 量化可以显著降低显存占用。\n\n## 工具调用\n模型可以返回 tool_calls，Agent 据此执行文件操作。\n");

        var service = new KnowledgeService(Path.Combine(_root, "knowledge.json"));
        await service.ImportAsync(docPath, CancellationToken.None);

        var first = service.Search("KV cache 显存", 4);
        Assert.NotEmpty(first);
        Assert.Equal(1, service.IndexBuilds);

        var second = service.Search("KV cache 显存", 4);
        Assert.Equal(1, service.IndexBuilds);
        Assert.Equal(1, service.QueryCacheHits);
        Assert.Equal(first, second);

        // 不同 query 重新打分，但复用同一份索引
        var other = service.Search("tool_calls", 4);
        Assert.NotEmpty(other);
        Assert.Equal(1, service.IndexBuilds);
    }

    [Fact]
    public async Task Deleting_document_invalidates_the_index()
    {
        var docPath = Path.Combine(_root, "temp.md");
        await File.WriteAllTextAsync(docPath, "仅此文档包含 unique_token_xyz 这个词。");
        var service = new KnowledgeService(Path.Combine(_root, "knowledge.json"));
        var document = await service.ImportAsync(docPath, CancellationToken.None);

        Assert.NotEmpty(service.Search("unique_token_xyz", 3));
        Assert.Equal(1, service.IndexBuilds);

        Assert.True(service.Delete(document.Id));
        Assert.Empty(service.Search("unique_token_xyz", 3));
        Assert.Equal(2, service.IndexBuilds);
    }

    [Fact]
    public void Memory_search_prefers_recent_entries_over_stale_ones()
    {
        var path = Path.Combine(_root, "memory.json");
        var now = DateTime.Now;
        var stale = new MemoryEntry
        {
            Id = "stale",
            Text = "主题使用浅色模式",
            Source = "manual",
            CreatedAt = now.AddDays(-90),
            UpdatedAt = now.AddDays(-90)
        };
        var recent = new MemoryEntry
        {
            Id = "recent",
            Text = "主题使用深色模式",
            Source = "manual",
            CreatedAt = now.AddDays(-1),
            UpdatedAt = now.AddDays(-1)
        };
        File.WriteAllText(path, JsonSerializer.Serialize(new[] { stale, recent }));

        var service = new MemoryService(path);
        service.Load();

        var hits = service.Search("主题使用", 5, now);
        Assert.Equal("recent", hits[0].Id);

        // 30 天前的置顶记忆仍可通过权重胜出
        var pinned = new MemoryEntry
        {
            Id = "stale",
            Text = stale.Text,
            Source = stale.Source,
            CreatedAt = now.AddDays(-30),
            UpdatedAt = now.AddDays(-30),
            Pinned = true
        };
        File.WriteAllText(path, JsonSerializer.Serialize(new[] { pinned, recent }));
        service.Load();
        var pinnedHits = service.Search("主题使用", 5, now);
        Assert.Equal("stale", pinnedHits[0].Id);
    }

    [Fact]
    public void Memory_conflict_requires_opposite_negation_on_shared_subject()
    {
        var path = Path.Combine(_root, "memory.json");
        var service = new MemoryService(path);

        service.Add("使用深色主题");
        var conflicts = service.FindConflicts("不要使用深色主题");

        Assert.Contains(conflicts, entry => entry.Text.Contains("深色主题", StringComparison.Ordinal));
        Assert.Empty(service.FindConflicts("使用深色主题"));
        Assert.Empty(service.FindConflicts("周末喜欢去爬山"));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch
        {
            // 清理失败不影响测试
        }
    }
}
