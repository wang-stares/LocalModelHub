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
