using LocalModelHub.Core.Services;
using Xunit;

namespace LocalModelHub.Core.Tests;

public sealed class KnowledgeServiceTests
{
    [Fact]
    public void SplitText_packs_paragraphs_and_overlaps_long_blocks()
    {
        var paragraphs = string.Join("\n\n", Enumerable.Range(0, 6).Select(index => $"第 {index} 段内容。"));
        var chunks = KnowledgeService.SplitText(paragraphs);
        Assert.NotEmpty(chunks);
        Assert.All(chunks, chunk => Assert.True(chunk.Length <= KnowledgeService.ChunkSize));

        var longText = string.Concat(Enumerable.Range(0, 2200).Select(index => (char)('a' + index % 26)));
        var parts = KnowledgeService.SplitText(longText);
        Assert.Equal(3, parts.Count);
        Assert.All(parts, part => Assert.True(part.Length <= KnowledgeService.ChunkSize));
        Assert.Equal(parts[0][^KnowledgeService.ChunkOverlap..], parts[1][..KnowledgeService.ChunkOverlap]);
    }

    [Fact]
    public async Task Imports_and_retrieves_chinese_and_english_documents()
    {
        var root = TempDirectory();
        try
        {
            var service = new KnowledgeService(Path.Combine(root, "knowledge.json"));
            var apiDoc = Path.Combine(root, "api.md");
            var cookingDoc = Path.Combine(root, "cooking.txt");
            await File.WriteAllTextAsync(apiDoc,
                "Local Model Hub 支持局域网 API 接入。开启 0.0.0.0 监听后，其他设备可以通过 Bearer API Key 调用 /v1/chat/completions。");
            await File.WriteAllTextAsync(cookingDoc,
                "番茄炒蛋的做法：先炒鸡蛋，再放番茄，最后加盐。");

            var first = await service.ImportAsync(apiDoc, CancellationToken.None);
            await service.ImportAsync(cookingDoc, CancellationToken.None);

            Assert.Equal(2, service.Documents.Count);
            Assert.True(first.Chunks.Count > 0);
            Assert.True(service.ChunkCount >= 2);

            var chinese = service.Search("局域网 API 怎么调用", 3);
            Assert.NotEmpty(chinese);
            Assert.Equal("api.md", chinese[0].DocumentName);

            var english = service.Search("bearer key", 3);
            Assert.NotEmpty(english);
            Assert.Equal("api.md", english[0].DocumentName);

            var context = service.BuildContext("局域网 API 怎么调用", 2);
            Assert.NotEmpty(context.Hits);
            Assert.Contains("api.md", context.Text);
            Assert.Contains("[1]", context.Text);

            // 重新导入同一路径应替换旧文档而不是重复计数
            await service.ImportAsync(apiDoc, CancellationToken.None);
            Assert.Equal(2, service.Documents.Count);

            // 持久化后重新加载
            var reloaded = new KnowledgeService(Path.Combine(root, "knowledge.json"));
            Assert.Equal(2, reloaded.Load().Count);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Rejects_binary_or_oversized_files_and_handles_corrupt_index()
    {
        var root = TempDirectory();
        try
        {
            var service = new KnowledgeService(Path.Combine(root, "knowledge.json"));
            var binary = Path.Combine(root, "model.gguf");
            await File.WriteAllBytesAsync(binary, [0, 1, 2, 3]);

            await Assert.ThrowsAsync<InvalidDataException>(() =>
                service.ImportAsync(binary, CancellationToken.None));

            var text = Path.Combine(root, "big.txt");
            await File.WriteAllTextAsync(text, new string('x', KnowledgeService.MaxFileBytes + 1));
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                service.ImportAsync(text, CancellationToken.None));

            var index = Path.Combine(root, "knowledge.json");
            await File.WriteAllTextAsync(index, "{ broken");
            Assert.Empty(new KnowledgeService(index).Load());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Imports_supported_files_from_folder_recursively()
    {
        var root = TempDirectory();
        try
        {
            var docs = Path.Combine(root, "docs");
            var nested = Path.Combine(docs, "nested");
            Directory.CreateDirectory(nested);
            await File.WriteAllTextAsync(Path.Combine(docs, "readme.md"), "# 项目说明\n\n这是一个本地模型客户端。");
            await File.WriteAllTextAsync(Path.Combine(nested, "Program.cs"), "class Program { static void Main() { } }");
            await File.WriteAllTextAsync(Path.Combine(docs, "image.png"), "not-really-an-image");
            await File.WriteAllTextAsync(Path.Combine(docs, "empty.txt"), "");

            var service = new KnowledgeService(Path.Combine(root, "knowledge.json"));
            var result = await service.ImportFolderAsync(docs, null, CancellationToken.None);

            Assert.Equal(2, result.Imported);
            Assert.Equal(0, result.Failed);
            Assert.Equal(2, service.Documents.Count);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string TempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "lmh-knowledge-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
