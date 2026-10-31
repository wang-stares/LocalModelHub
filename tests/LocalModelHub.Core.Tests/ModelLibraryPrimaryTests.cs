using LocalModelHub.Core.Models;
using LocalModelHub.Core.Services;
using Xunit;

namespace LocalModelHub.Core.Tests;

/// <summary>扫描时主模型文件的选择：排除 mmproj / LoRA、分片从 00001 开始、无法判定时标注自动选择。</summary>
public sealed class ModelLibraryPrimaryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"lmh-primary-{Guid.NewGuid():N}");
    private readonly ModelLibraryService _library;

    public ModelLibraryPrimaryTests()
    {
        var settings = new SettingsService();
        settings.Load();
        settings.Settings.ModelsDirectory = _root;
        Directory.CreateDirectory(_root);
        _library = new ModelLibraryService(settings);
    }

    [Fact]
    public void Excludes_mmproj_and_lora_from_primary_but_keeps_them_owned()
    {
        var quant = Path.Combine(_root, "Qwen3-8B", "Q4_K_M");
        Directory.CreateDirectory(quant);
        var model = Path.Combine(quant, "model-Q4_K_M.gguf");
        var projector = Path.Combine(quant, "mmproj-Q4_K_M.gguf");
        var adapter = Path.Combine(quant, "model-lora-Q4_K_M-adapter.gguf");
        File.WriteAllText(model, "model");
        File.WriteAllText(projector, new string('p', 64));
        File.WriteAllText(adapter, new string('a', 64));

        var scanned = Scan(quant);

        Assert.Equal(model, scanned.PrimaryPath);
        Assert.Null(scanned.SelectionNote);
        Assert.Equal(3, scanned.Files.Count);
        Assert.Equal(
            new FileInfo(model).Length + new FileInfo(projector).Length + new FileInfo(adapter).Length,
            scanned.TotalBytes);
    }

    [Fact]
    public void Picks_first_shard_and_counts_whole_group()
    {
        var quant = Path.Combine(_root, "Qwen3-14B", "Q4_K_M");
        Directory.CreateDirectory(quant);
        var names = Enumerable.Range(1, 4)
            .Select(index => $"model-Q4_K_M-{index:00000}-of-00004.gguf")
            .ToArray();

        var sizes = new[] { 8, 40, 40, 40 };
        for (var index = 0; index < names.Length; index++)
        {
            File.WriteAllText(Path.Combine(quant, names[index]), new string('s', sizes[index]));
        }

        var scanned = Scan(quant);

        // 按体积排序会误选第 2 片；分片必须从 00001 开始加载
        Assert.Equal(Path.Combine(quant, names[0]), scanned.PrimaryPath);
        Assert.Equal(4, scanned.Files.Count);
        Assert.Equal(sizes.Sum(size => (long)size), scanned.TotalBytes);
        Assert.Null(scanned.SelectionNote);
    }

    [Fact]
    public void Marks_automatic_selection_when_quant_label_matches_no_file()
    {
        var quant = Path.Combine(_root, "Auto", "Q4_K_M");
        Directory.CreateDirectory(quant);
        File.WriteAllText(Path.Combine(quant, "alpha.gguf"), "a");
        var beta = Path.Combine(quant, "beta.gguf");
        File.WriteAllText(beta, new string('b', 40));

        var scanned = Scan(quant);

        Assert.Equal(beta, scanned.PrimaryPath);
        Assert.Equal("自动选择，可手动切换", scanned.SelectionNote);
    }

    private DownloadedModel Scan(string folder) =>
        _library.Scan(new ModelCatalog())
            .Single(model => string.Equals(model.FolderPath, folder, StringComparison.OrdinalIgnoreCase));

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
