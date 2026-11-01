using LocalModelHub.Core.Models;
using LocalModelHub.Core.Services;
using Xunit;

namespace LocalModelHub.Core.Tests;

/// <summary>验证「全盘搜索」结果的原地引用：加入库不复制文件，删除只移除引用。</summary>
public class ModelLibraryExternalTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"lmh-ext-models-{Guid.NewGuid():N}");
    private readonly string _outside = Path.Combine(Path.GetTempPath(), $"lmh-ext-outside-{Guid.NewGuid():N}");
    private readonly string _settingsFile = Path.Combine(Path.GetTempPath(), $"lmh-ext-settings-{Guid.NewGuid():N}", "settings.json");
    private SettingsService _settings = null!;
    private ModelLibraryService _library = null!;

    public Task InitializeAsync()
    {
        _settings = new SettingsService(_settingsFile);
        _settings.Load();
        _settings.Settings.ModelsDirectory = _root;
        _settings.Settings.ExternalModelPaths.Clear();
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(_outside);
        Directory.CreateDirectory(Path.GetDirectoryName(_settingsFile)!);
        _library = new ModelLibraryService(_settings);
        return Task.CompletedTask;
    }

    [Fact]
    public void Adds_external_model_in_place()
    {
        var file = WriteExternal("Qwen3-8B-Q4_K_M.gguf");

        Assert.True(_library.AddExternalModel(file));

        var model = Assert.Single(_library.Scan(Catalog()));
        Assert.True(model.IsExternal);
        Assert.Equal(Path.GetFullPath(file), model.PrimaryPath);
        Assert.Equal(Path.GetFullPath(_outside), model.FolderPath);
        Assert.True(File.Exists(file), "加入库不应复制或移动文件");
        Assert.Empty(Directory.GetFiles(_root, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public void Rejects_duplicate_and_in_library_paths()
    {
        var external = WriteExternal("Qwen3-8B-Q4_K_M.gguf");
        Assert.True(_library.AddExternalModel(external));
        Assert.False(_library.AddExternalModel(external));

        var inside = Path.Combine(_root, "Qwen3-8B", "Qwen3-8B-Q8_0.gguf");
        Directory.CreateDirectory(Path.GetDirectoryName(inside)!);
        File.WriteAllText(inside, "gguf");
        Assert.False(_library.AddExternalModel(inside));
    }

    [Fact]
    public void Rejects_invalid_paths()
    {
        Assert.Throws<FileNotFoundException>(() =>
            _library.AddExternalModel(Path.Combine(_outside, "missing.gguf")));

        var notGguf = Path.Combine(_outside, "weights.bin");
        File.WriteAllText(notGguf, "weights");
        Assert.Throws<InvalidOperationException>(() => _library.AddExternalModel(notGguf));
    }

    [Fact]
    public void Persists_reference_across_reload()
    {
        var file = WriteExternal("Qwen3-8B-Q4_K_M.gguf");
        Assert.True(_library.AddExternalModel(file));

        var reloaded = new SettingsService(_settingsFile);
        reloaded.Load();

        Assert.Equal([Path.GetFullPath(file)], reloaded.Settings.ExternalModelPaths);
    }

    [Fact]
    public void Deleting_external_model_keeps_file_on_disk()
    {
        var file = WriteExternal("Qwen3-8B-Q4_K_M.gguf");
        _library.AddExternalModel(file);
        var model = Assert.Single(_library.Scan(Catalog()));

        var result = _library.Delete(model, useRecycleBin: false);

        Assert.True(result.Success);
        Assert.True(File.Exists(file), "外部引用的文件必须保留");
        Assert.Equal(0, result.DeletedFiles);
        Assert.Empty(_settings.Settings.ExternalModelPaths);
        Assert.Empty(_library.Scan(Catalog()));
    }

    [Fact]
    public void Missing_external_file_is_not_listed()
    {
        var file = WriteExternal("Qwen3-8B-Q4_K_M.gguf");
        _library.AddExternalModel(file);
        File.Delete(file);

        Assert.Empty(_library.Scan(Catalog()));
    }

    [Fact]
    public void Folder_reference_scans_one_level_of_quants()
    {
        var folder = Path.Combine(_outside, "org");
        Directory.CreateDirectory(Path.Combine(folder, "Q4_K_M"));
        File.WriteAllText(Path.Combine(folder, "root-Q8_0.gguf"), "gguf");
        File.WriteAllText(Path.Combine(folder, "Q4_K_M", "sub-Q4_K_M.gguf"), "gguf");
        _settings.Settings.ExternalModelPaths.Add(folder);

        var models = _library.Scan(Catalog());

        Assert.Equal(2, models.Count);
        Assert.All(models, model => Assert.True(model.IsExternal));
    }

    private string WriteExternal(string name)
    {
        var path = Path.Combine(_outside, name);
        File.WriteAllText(path, "gguf");
        return path;
    }

    private static ModelCatalog Catalog() => new CatalogService().LoadEmbedded();

    public Task DisposeAsync()
    {
        TryDelete(_root);
        TryDelete(_outside);
        TryDelete(Path.GetDirectoryName(_settingsFile)!);
        return Task.CompletedTask;
    }

    private static void TryDelete(string directory)
    {
        try
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
        catch
        {
            // 清理失败不影响测试结果
        }
    }
}
