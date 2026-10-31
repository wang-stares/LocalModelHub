using LocalModelHub.Core.Models;
using LocalModelHub.Core.Services;
using Xunit;

namespace LocalModelHub.Core.Tests;

/// <summary>验证「删除模型」真正作用于磁盘，而不是只移除列表项。</summary>
public class ModelLibraryDeleteTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"lmh-delete-{Guid.NewGuid():N}");
    private readonly string _outside = Path.Combine(Path.GetTempPath(), $"lmh-outside-{Guid.NewGuid():N}");
    private ModelLibraryService _library = null!;

    public Task InitializeAsync()
    {
        var settings = new SettingsService();
        settings.Load();
        settings.Settings.ModelsDirectory = _root;
        Directory.CreateDirectory(_root);
        _library = new ModelLibraryService(settings);
        return Task.CompletedTask;
    }

    [Fact]
    public void Deletes_model_directory_and_empty_parent()
    {
        var quant = Path.Combine(_root, "Qwen3-8B", "Q4_K_M");
        Directory.CreateDirectory(quant);
        File.WriteAllText(Path.Combine(quant, "model-Q4_K_M.gguf"), "gguf");

        var result = _library.Delete(Model(quant));

        Assert.True(result.Success);
        Assert.False(Directory.Exists(quant));
        Assert.False(Directory.Exists(Path.Combine(_root, "Qwen3-8B")));
    }

    [Fact]
    public void Keeps_sibling_quants_when_deleting_one()
    {
        var repo = Path.Combine(_root, "Qwen3-8B");
        var q4 = Path.Combine(repo, "Q4_K_M");
        var q8 = Path.Combine(repo, "Q8_0");
        Directory.CreateDirectory(q4);
        Directory.CreateDirectory(q8);
        File.WriteAllText(Path.Combine(q4, "a.gguf"), "gguf");
        File.WriteAllText(Path.Combine(q8, "b.gguf"), "gguf");

        var result = _library.Delete(Model(q4));

        Assert.True(result.Success);
        Assert.False(Directory.Exists(q4));
        Assert.True(Directory.Exists(q8));
        Assert.True(Directory.Exists(repo));
    }

    [Fact]
    public void Deleting_one_quant_keeps_other_quant_in_same_folder()
    {
        var folder = Path.Combine(_root, "Qwen3-8B");
        Directory.CreateDirectory(folder);
        var q4 = Path.Combine(folder, "Qwen3-8B-Q4_K_M.gguf");
        var q8 = Path.Combine(folder, "Qwen3-8B-Q8_0.gguf");
        File.WriteAllText(q4, "q4-bytes");
        File.WriteAllText(q8, "q8-bytes");

        var model = Scanned(folder);
        var result = _library.Delete(model, useRecycleBin: false);

        Assert.True(result.Success);
        Assert.False(File.Exists(q4));
        Assert.True(File.Exists(q8), "同目录的其它量化不应被删除");
        Assert.True(Directory.Exists(folder));
        Assert.Equal(1, result.DeletedFiles);
        Assert.True(result.ReleasedBytes > 0);
    }

    [Fact]
    public void Deletes_all_shards_of_a_quant_as_one_group()
    {
        var folder = Path.Combine(_root, "Qwen3-14B", "Q4_K_M");
        Directory.CreateDirectory(folder);
        var shard1 = Path.Combine(folder, "model-Q4_K_M-00001-of-00002.gguf");
        var shard2 = Path.Combine(folder, "model-Q4_K_M-00002-of-00002.gguf");
        File.WriteAllText(shard1, "shard-1");
        File.WriteAllText(shard2, "shard-2");

        var model = Scanned(folder);
        Assert.Equal(2, model.Files.Count);

        var result = _library.Delete(model, useRecycleBin: false);

        Assert.True(result.Success);
        Assert.Equal(2, result.DeletedFiles);
        Assert.False(File.Exists(shard1));
        Assert.False(File.Exists(shard2));
        Assert.False(Directory.Exists(folder));
    }

    [Fact]
    public void Manifest_file_list_marks_owned_files_and_limits_deletion()
    {
        var folder = Path.Combine(_root, "Imported", "Q4_K_M");
        Directory.CreateDirectory(folder);
        var owned = Path.Combine(folder, "owned.gguf");
        var stray = Path.Combine(folder, "stray.gguf");
        File.WriteAllText(owned, "owned");
        File.WriteAllText(stray, "stray");
        File.WriteAllText(Path.Combine(folder, "manifest.json"),
            """{"modelId":"qwen3-8b","quant":"Q4_K_M","files":[{"relativePath":"owned.gguf","bytes":5}]}""");

        var model = Scanned(folder);
        Assert.Single(model.Files);

        var result = _library.Delete(model, useRecycleBin: false);

        Assert.True(result.Success);
        Assert.False(File.Exists(owned));
        Assert.True(File.Exists(stray));
        Assert.True(Directory.Exists(folder));
        Assert.Equal(1, result.DeletedFiles);
        Assert.Equal(1, result.RemainingSiblingFiles);
    }

    [Fact]
    public void Plan_lists_files_size_and_siblings()
    {
        var folder = Path.Combine(_root, "Qwen3-8B");
        Directory.CreateDirectory(folder);
        var q4 = Path.Combine(folder, "Qwen3-8B-Q4_K_M.gguf");
        var q8 = Path.Combine(folder, "Qwen3-8B-Q8_0.gguf");
        File.WriteAllText(q4, "q4-bytes");
        File.WriteAllText(q8, "q8-bytes");

        var plan = _library.PlanDelete(Scanned(folder));

        Assert.Single(plan.Files);
        Assert.Equal(new FileInfo(q4).Length, plan.TotalBytes);
        Assert.Equal(1, plan.RemainingSiblingFiles);
    }

    [Fact]
    public void Refuses_paths_outside_models_directory()
    {
        Directory.CreateDirectory(_outside);
        File.WriteAllText(Path.Combine(_outside, "keep.gguf"), "gguf");

        var result = _library.Delete(Model(_outside));

        Assert.False(result.Success);
        Assert.Contains("目录之外", result.Error);
        Assert.True(Directory.Exists(_outside));
        Assert.True(File.Exists(Path.Combine(_outside, "keep.gguf")));
    }

    [Fact]
    public void Reports_missing_model_instead_of_silent_success()
    {
        var ghost = Path.Combine(_root, "Ghost", "Q4_K_M");
        var result = _library.Delete(Model(ghost));

        Assert.False(result.Success);
        Assert.Contains("已不存在", result.Error);
    }

    private DownloadedModel Scanned(string folder)
    {
        var catalog = new CatalogService().LoadEmbedded();
        return _library.Scan(catalog)
            .Single(m => string.Equals(m.FolderPath, folder, StringComparison.OrdinalIgnoreCase));
    }

    private static DownloadedModel Model(string folder) => new()
    {
        ModelId = "qwen3-8b",
        DisplayName = "Qwen3 8B",
        QuantLabel = "Q4_K_M",
        FolderPath = folder,
        PrimaryPath = Path.Combine(folder, "model.gguf"),
        TotalBytes = 0
    };

    public Task DisposeAsync()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { /* ignore */ }
        try { if (Directory.Exists(_outside)) Directory.Delete(_outside, recursive: true); } catch { /* ignore */ }
        return Task.CompletedTask;
    }
}
