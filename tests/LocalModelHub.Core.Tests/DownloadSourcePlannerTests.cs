using LocalModelHub.Core.Models;
using LocalModelHub.Core.Services;
using Xunit;

namespace LocalModelHub.Core.Tests;

public class DownloadSourcePlannerTests
{
    private static ModelEntry Builtin(string? hfRepo = "Qwen/Qwen3-8B-GGUF") => new()
    {
        Id = "qwen3-8b",
        Name = "Qwen3-8B",
        DisplayName = "Qwen3-8B",
        Developer = "Qwen",
        HfRepo = hfRepo
    };

    private static ModelFile File(string? digest = null) => new()
    {
        Path = "Qwen3-8B-Q4_K_M.gguf",
        Bytes = 100,
        Digest = digest
    };

    private static ModelQuant Quant(ModelSource[] sources) => new()
    {
        Label = "Q4_K_M",
        Files = [File()],
        Sources = sources
    };

    private static ModelSource Ms() => new()
    {
        Provider = "ModelScope",
        Repo = "Qwen/Qwen3-8B-GGUF",
        Revision = "master"
    };

    private static ModelSource Hf() => new()
    {
        Provider = "HuggingFace",
        Repo = "Qwen/Qwen3-8B-GGUF",
        Revision = "main"
    };

    private static ModelSource Ollama() => new()
    {
        Provider = "Ollama",
        Repo = "library/llama3.2",
        Revision = "3b"
    };

    [Fact]
    public void Auto_prefers_native_source_then_other_providers()
    {
        var settings = new AppSettings { DownloadAuto = true, UseHfMirror = true };
        var candidates = DownloadSourcePlanner.Build(
            Builtin(), Quant([Ms()]), File(), settings);

        Assert.Equal(["ModelScope", "HuggingFace", "HuggingFace"], candidates.Select(c => c.Provider));
        Assert.Contains("modelscope.cn", candidates[0].Url);
        Assert.Contains("hf-mirror.com", candidates[1].Url);
        Assert.Contains("huggingface.co", candidates[2].Url);
    }

    [Fact]
    public void Manual_single_source_without_switch_uses_only_that_source()
    {
        var settings = new AppSettings
        {
            DownloadAuto = false,
            AutoSwitchSource = false,
            DownloadProviders = ["HuggingFace"],
            UseHfMirror = false
        };
        var candidates = DownloadSourcePlanner.Build(
            Builtin(), Quant([Ms()]), File(), settings);

        var candidate = Assert.Single(candidates);
        Assert.Equal("HuggingFace", candidate.Provider);
        Assert.Contains("huggingface.co", candidate.Url);
        Assert.DoesNotContain("hf-mirror.com", candidate.Url);
        Assert.DoesNotContain("modelscope", candidate.Url);
    }

    [Fact]
    public void Manual_multi_source_with_switch_appends_remaining_sources()
    {
        var settings = new AppSettings
        {
            DownloadAuto = false,
            AutoSwitchSource = true,
            DownloadProviders = ["HuggingFace", "Ollama"],
            UseHfMirror = true
        };
        var file = File("sha256:abc");
        var candidates = DownloadSourcePlanner.Build(
            Builtin(), Quant([Ollama(), Hf()]), file, settings);

        Assert.Equal(
            ["HuggingFace", "HuggingFace", "Ollama", "ModelScope"],
            candidates.Select(c => c.Provider));
        Assert.Contains("registry.ollama.ai/v2/library/llama3.2/blobs/sha256:abc", candidates[2].Url);
    }

    [Fact]
    public void Manual_single_modelscope_uses_master_revision()
    {
        var settings = new AppSettings
        {
            DownloadAuto = false,
            AutoSwitchSource = false,
            DownloadProviders = ["ModelScope"]
        };
        var candidates = DownloadSourcePlanner.Build(
            Builtin(), Quant([Ms()]), File(), settings);

        var candidate = Assert.Single(candidates);
        Assert.Equal("ModelScope", candidate.Provider);
        Assert.Contains("Revision=master", candidate.Url);
        Assert.Contains("FilePath=Qwen3-8B-Q4_K_M.gguf", candidate.Url);
    }

    [Fact]
    public void Ollama_only_model_without_digest_returns_empty()
    {
        var settings = new AppSettings { DownloadAuto = true };
        var candidates = DownloadSourcePlanner.Build(
            Builtin(hfRepo: null), Quant([Ollama()]), File(), settings);

        Assert.Empty(candidates);
    }

    [Fact]
    public void Manual_mode_without_switch_keeps_selected_source_only()
    {
        var settings = new AppSettings
        {
            DownloadAuto = false,
            AutoSwitchSource = false,
            DownloadProviders = ["ModelScope"]
        };
        var candidates = DownloadSourcePlanner.Build(
            Builtin(), Quant([Hf()]), File(), settings);

        var candidate = Assert.Single(candidates);
        Assert.Equal("ModelScope", candidate.Provider);
    }
}
