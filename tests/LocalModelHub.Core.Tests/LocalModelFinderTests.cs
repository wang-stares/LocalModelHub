using LocalModelHub.Core.Services;
using Xunit;

namespace LocalModelHub.Core.Tests;

/// <summary>验证全盘搜索：只认真正的 GGUF，跳过系统/构建目录，分片只保留首片。</summary>
public class LocalModelFinderTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"lmh-find-{Guid.NewGuid():N}");
    private readonly LocalModelFinder _finder = new();

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        return Task.CompletedTask;
    }

    [Fact]
    public void Finds_real_gguf_and_extracts_quant()
    {
        var path = Path.Combine(_root, "Qwen3-8B-Q4_K_M.gguf");
        WriteGguf(path);

        var found = Assert.Single(_finder.Search([_root]));

        Assert.Equal(Path.GetFullPath(path), found.Path);
        Assert.Equal("Qwen3-8B", found.Name);
        Assert.Equal("Q4_K_M", found.Quant);
        Assert.True(found.Bytes >= LocalModelFinder.MinFileBytes);
    }

    [Fact]
    public void Searches_nested_directories()
    {
        var deep = Path.Combine(_root, "models", "llama", "7b");
        Directory.CreateDirectory(deep);
        WriteGguf(Path.Combine(deep, "llama-7b-Q8_0.gguf"));

        var found = Assert.Single(_finder.Search([_root]));

        Assert.Equal("llama-7b", found.Name);
        Assert.Equal("Q8_0", found.Quant);
    }

    [Fact]
    public void Rejects_files_without_gguf_magic()
    {
        var path = Path.Combine(_root, "fake-Q4_K_M.gguf");
        File.WriteAllBytes(path, new byte[80 * 1024]);

        Assert.Empty(_finder.Search([_root]));
    }

    [Fact]
    public void Rejects_tiny_download_fragments()
    {
        var path = Path.Combine(_root, "partial-Q4_K_M.gguf");
        var bytes = new byte[8 * 1024];
        "GGUF"u8.CopyTo(bytes);
        File.WriteAllBytes(path, bytes);

        Assert.Empty(_finder.Search([_root]));
    }

    [Fact]
    public void Skips_auxiliary_weights()
    {
        WriteGguf(Path.Combine(_root, "mmproj-model-f16.gguf"));
        WriteGguf(Path.Combine(_root, "model-Q4_K_M-adapter.gguf"));
        WriteGguf(Path.Combine(_root, "model-Q4_K_M-lora.gguf"));
        WriteGguf(Path.Combine(_root, "real-Q4_K_M.gguf"));

        var found = Assert.Single(_finder.Search([_root]));

        Assert.Equal("real", found.Name);
    }

    [Fact]
    public void Keeps_only_first_shard()
    {
        WriteGguf(Path.Combine(_root, "model-Q4_K_M-00001-of-00002.gguf"));
        WriteGguf(Path.Combine(_root, "model-Q4_K_M-00002-of-00002.gguf"));

        var found = Assert.Single(_finder.Search([_root]));

        Assert.EndsWith("00001-of-00002.gguf", found.Path, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("Q4_K_M", found.Quant);
    }

    [Fact]
    public void Skips_system_and_build_directories()
    {
        Directory.CreateDirectory(Path.Combine(_root, "node_modules"));
        WriteGguf(Path.Combine(_root, "node_modules", "pkg-Q4_K_M.gguf"));

        var hidden = Path.Combine(_root, "hidden");
        Directory.CreateDirectory(hidden);
        File.SetAttributes(hidden, FileAttributes.Directory | FileAttributes.Hidden);
        WriteGguf(Path.Combine(hidden, "secret-Q4_K_M.gguf"));

        WriteGguf(Path.Combine(_root, "visible-Q4_K_M.gguf"));

        var found = Assert.Single(_finder.Search([_root]));

        Assert.Equal("visible", found.Name);
    }

    [Fact]
    public void Honours_max_depth()
    {
        var depth10 = _root;
        for (var i = 1; i <= 10; i++) depth10 = Path.Combine(depth10, $"d{i}");
        Directory.CreateDirectory(depth10);
        WriteGguf(Path.Combine(depth10, "ten-Q4_K_M.gguf"));

        var depth11 = Path.Combine(_root, "deep");
        for (var i = 1; i <= 11; i++) depth11 = Path.Combine(depth11, $"d{i}");
        Directory.CreateDirectory(depth11);
        WriteGguf(Path.Combine(depth11, "eleven-Q4_K_M.gguf"));

        var found = _finder.Search([_root]);

        Assert.Single(found);
        Assert.Equal("ten", found[0].Name);
    }

    [Fact]
    public void Reports_progress_and_completion()
    {
        WriteGguf(Path.Combine(_root, "a-Q4_K_M.gguf"));
        var reports = new List<FinderProgress>();

        _finder.Search([_root], new InlineProgress<FinderProgress>(reports.Add));

        Assert.NotEmpty(reports);
        Assert.Equal("扫描完成", reports[^1].CurrentDirectory);
        Assert.Equal(1, reports[^1].Found);
    }

    [Fact]
    public void Stops_on_cancellation()
    {
        WriteGguf(Path.Combine(_root, "a-Q4_K_M.gguf"));
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.Throws<OperationCanceledException>(() => _finder.Search([_root], null, cts.Token));
    }

    [Fact]
    public void Ignores_missing_roots()
    {
        var missing = Path.Combine(_root, "missing");

        Assert.Empty(_finder.Search([missing, _root]));
    }

    private static void WriteGguf(string path)
    {
        var bytes = new byte[70 * 1024];
        "GGUF"u8.CopyTo(bytes);
        File.WriteAllBytes(path, bytes);
    }

    private sealed class InlineProgress<T>(Action<T> handler) : IProgress<T>
    {
        public void Report(T value) => handler(value);
    }

    public Task DisposeAsync()
    {
        ForceDelete(_root);
        return Task.CompletedTask;
    }

    /// <summary>隐藏目录直接 Directory.Delete 会失败，先把属性恢复再递归删除。</summary>
    private static void ForceDelete(string directory)
    {
        try
        {
            if (!Directory.Exists(directory)) return;
            var options = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0 };
            foreach (var file in Directory.EnumerateFiles(directory, "*", options))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }
            foreach (var child in Directory.EnumerateDirectories(directory, "*", options))
            {
                File.SetAttributes(child, FileAttributes.Directory);
            }
            Directory.Delete(directory, recursive: true);
        }
        catch
        {
            // 清理失败不影响测试结果
        }
    }
}
