using LocalModelHub.Core.Models;
using LocalModelHub.Core.Services;
using Xunit;

namespace LocalModelHub.Core.Tests;

public class InferenceOptionsTests
{
    private static LaunchOptions Options(
        int gpuLayers = 99,
        string flashAttention = "auto",
        string cacheTypeK = "q8_0",
        string cacheTypeV = "q8_0",
        int cpuMoeLayers = 0,
        int parallelSlots = 1,
        string loadMode = "mmap",
        string reasoningFormat = "deepseek",
        bool enableMetrics = false,
        string? apiKey = null) =>
        new(8090, 8192, gpuLayers, 0, "qwen3", ApiKey: apiKey,
            FlashAttention: flashAttention,
            CacheTypeK: cacheTypeK,
            CacheTypeV: cacheTypeV,
            CpuMoeLayers: cpuMoeLayers,
            ParallelSlots: parallelSlots,
            LoadMode: loadMode,
            ReasoningFormat: reasoningFormat,
            EnableMetrics: enableMetrics);

    [Fact]
    public void Defaults_emit_kv_quantization_single_slot_and_reasoning()
    {
        var arguments = LlamaServerService.BuildArguments(@"C:\models\a.gguf", Options());

        Assert.Contains("-m \"C:\\models\\a.gguf\"", arguments);
        Assert.Contains("-ngl 99", arguments);
        Assert.Contains("--jinja", arguments);
        Assert.Contains("-ctk q8_0 -ctv q8_0", arguments);
        Assert.Contains("-np 1", arguments);
        Assert.Contains("--reasoning-format deepseek", arguments);
        Assert.DoesNotContain("-fa ", arguments);
        Assert.DoesNotContain("-lm ", arguments);
        Assert.DoesNotContain("--metrics", arguments);
    }

    [Fact]
    public void Performance_flags_are_forwarded()
    {
        var arguments = LlamaServerService.BuildArguments(
            "model.gguf",
            Options(flashAttention: "on", cacheTypeK: "q4_0", cacheTypeV: "q4_0",
                cpuMoeLayers: 12, parallelSlots: 4, loadMode: "mlock",
                reasoningFormat: "none", enableMetrics: true));

        Assert.Contains("-fa on", arguments);
        Assert.Contains("-ctk q4_0 -ctv q4_0", arguments);
        Assert.Contains("--n-cpu-moe 12", arguments);
        Assert.Contains("-np 4", arguments);
        Assert.Contains("-lm mlock", arguments);
        Assert.Contains("--reasoning-format none", arguments);
        Assert.Contains("--metrics", arguments);
    }

    [Fact]
    public void Unknown_gpu_layers_fall_back_to_auto()
    {
        Assert.Contains("-ngl auto", LlamaServerService.BuildArguments("m.gguf", Options(gpuLayers: -1)));
    }

    [Fact]
    public void Invalid_enum_values_are_sanitized()
    {
        var arguments = LlamaServerService.BuildArguments(
            "m.gguf",
            Options(flashAttention: "on; rm -rf", cacheTypeK: "q3_k", cacheTypeV: "awq",
                loadMode: "dio", reasoningFormat: "xml"));

        Assert.DoesNotContain("rm -rf", arguments);
        Assert.DoesNotContain("-fa ", arguments);
        Assert.DoesNotContain("-ctk", arguments); // 非法值回退 f16，不输出量化参数
        Assert.DoesNotContain("-lm ", arguments);
        Assert.Contains("--reasoning-format deepseek", arguments);
    }

    [Fact]
    public void F16_cache_does_not_emit_quantization_flags()
    {
        var arguments = LlamaServerService.BuildArguments(
            "m.gguf", Options(cacheTypeK: "f16", cacheTypeV: "f16"));
        Assert.DoesNotContain("-ctk", arguments);
    }

    [Fact]
    public void Api_key_never_appears_on_command_line()
    {
        var arguments = LlamaServerService.BuildArguments("m.gguf", Options(apiKey: "secret"));
        Assert.DoesNotContain("secret", arguments);
        Assert.DoesNotContain("--api-key ", arguments);
    }

    [Fact]
    public void Api_key_file_is_passed_instead_of_plain_key()
    {
        var arguments = LlamaServerService.BuildArguments(
            "m.gguf", Options(apiKey: "secret"), @"C:\tmp\key.txt");
        Assert.Contains("--api-key-file \"C:\\tmp\\key.txt\"", arguments);
        Assert.DoesNotContain("secret", arguments);
    }

    [Fact]
    public void Api_key_file_is_readable_and_deletable()
    {
        var path = LlamaServerService.CreateApiKeyFile("sk-test-123");
        try
        {
            Assert.Equal("sk-test-123", File.ReadAllText(path).Trim());
        }
        finally
        {
            LlamaServerService.DeleteApiKeyFile(path);
        }

        Assert.False(File.Exists(path));
    }

    [Fact]
    public void Parser_splits_content_and_reasoning()
    {
        var content = ChatStreamParser.Parse(
            """{"choices":[{"delta":{"content":"你好"}}]}""");
        Assert.NotNull(content);
        Assert.Equal("你好", content!.Content);
        Assert.Null(content.Reasoning);

        var reasoning = ChatStreamParser.Parse(
            """{"choices":[{"delta":{"reasoning_content":"先分析"}}]}""");
        Assert.NotNull(reasoning);
        Assert.Equal("先分析", reasoning!.Reasoning);
        Assert.Null(reasoning.Content);
    }

    [Fact]
    public void Parser_supports_reasoning_alias_and_ignores_non_stream_payloads()
    {
        var alias = ChatStreamParser.Parse("""{"choices":[{"delta":{"reasoning":"想一想"}}]}""");
        Assert.NotNull(alias);
        Assert.Equal("想一想", alias!.Reasoning);

        Assert.Null(ChatStreamParser.Parse("[DONE]"));
        Assert.Null(ChatStreamParser.Parse(""));
        Assert.Null(ChatStreamParser.Parse("not-json"));
        Assert.Null(ChatStreamParser.Parse("""{"choices":[]}"""));
        Assert.Null(ChatStreamParser.Parse("""{"choices":[{"delta":{}}]}"""));
    }

    [Fact]
    public void Settings_defaults_match_recommended_performance_profile()
    {
        var settings = new AppSettings();

        Assert.Equal("q8_0", settings.KvCacheType);
        Assert.Equal(1, settings.ParallelSlots);
        Assert.Equal("mmap", settings.LoadMode);
        Assert.Equal("auto", settings.FlashAttention);
        Assert.True(settings.ReasoningEnabled);
    }
}
