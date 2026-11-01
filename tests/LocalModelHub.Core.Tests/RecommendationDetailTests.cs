using LocalModelHub.Core.Models;
using LocalModelHub.Core.Services;
using Xunit;

namespace LocalModelHub.Core.Tests;

/// <summary>P1-21：推荐结果要能直接决策——启动参数、量化对比、置信度、场景与结构化理由。</summary>
public sealed class RecommendationDetailTests
{
    private static HardwareProfile GpuMachine(double vramGb, double ramGb) => new(
        "GPU-PC", "Windows 11",
        new CpuInfo("Test CPU", 8, 16, 4200, 16384),
        (long)(ramGb * Formatters.Gib), (long)(ramGb * 0.5 * Formatters.Gib),
        [new GpuInfo("NVIDIA GeForce RTX 4060", GpuInfo.VendorNvidia, (long)(vramGb * Formatters.Gib), 0)],
        (long)(512 * Formatters.Gib), (long)(200 * Formatters.Gib),
        true, true, DateTime.Now);

    private static HardwareProfile CpuOnlyMachine(double ramGb) => new(
        "CPU-PC", "Windows 10",
        new CpuInfo("Intel i5", 6, 12, 3600, 12288),
        (long)(ramGb * Formatters.Gib), (long)(ramGb * 0.6 * Formatters.Gib),
        Array.Empty<GpuInfo>(),
        (long)(512 * Formatters.Gib), (long)(100 * Formatters.Gib),
        true, false, DateTime.Now);

    private static ModelEntry Model14B() => new()
    {
        Id = "test-14b",
        Name = "Test-14B",
        DisplayName = "Test-14B",
        ParametersB = 14,
        ContextLength = 32768,
        Capabilities = [ModelCapabilities.Coding, ModelCapabilities.Chat],
        Quants =
        [
            new ModelQuant { Label = "Q4_K_M", TotalBytes = (long)(8.5 * Formatters.Gib), Files = [new ModelFile { Path = "q4.gguf", Bytes = 1 }] },
            new ModelQuant { Label = "Q5_K_M", TotalBytes = (long)(10.2 * Formatters.Gib), Files = [new ModelFile { Path = "q5.gguf", Bytes = 1 }] },
            new ModelQuant { Label = "Q3_K_M", TotalBytes = (long)(6.9 * Formatters.Gib), Files = [new ModelFile { Path = "q3.gguf", Bytes = 1 }] }
        ]
    };

    [Fact]
    public void Recommendation_includes_launch_command_tradeoffs_confidence_and_scenarios()
    {
        var hw = GpuMachine(8, 32);
        var score = new PerformanceScorer().Score(hw);
        var options = new RecommendationOptions
        {
            ContextTokens = 4096,
            CpuGflops = 42.5,
            CpuMemoryBandwidthGBps = 20.7,
            KvCacheTypeK = "q8_0",
            KvCacheTypeV = "q8_0",
            FlashAttention = true
        };

        var (index, recommendation) = new RecommendationEngine()
            .RecommendBestQuant(hw, score, Model14B(), options);

        Assert.NotNull(recommendation.Launch);
        Assert.Contains("-ngl", recommendation.Launch!.CommandLine);
        Assert.Contains("-c ", recommendation.Launch.CommandLine);
        Assert.Contains("-ctk q8_0", recommendation.Launch.CommandLine);
        Assert.Contains("-ctv q8_0", recommendation.Launch.CommandLine);
        Assert.Contains("-fa on", recommendation.Launch.CommandLine);

        Assert.Equal(3, recommendation.Alternatives.Count);
        Assert.Single(recommendation.Alternatives, item => item.IsRecommended);
        Assert.Equal(recommendation.Quant.Label, recommendation.Alternatives[index].Label);
        Assert.All(recommendation.Alternatives, item =>
        {
            Assert.True(item.SizeGb > 0);
            Assert.True(item.SpeedHigh >= item.SpeedLow);
            Assert.False(string.IsNullOrWhiteSpace(item.QualityStars));
        });

        Assert.NotNull(recommendation.Speed);
        Assert.True(recommendation.Speed!.Low <= recommendation.EstimatedTokensPerSecond);
        Assert.False(string.IsNullOrWhiteSpace(recommendation.Speed.Basis));
        Assert.True(recommendation.Confidence >= 0.7);
        Assert.Equal("高", recommendation.ConfidenceText);
        Assert.NotEmpty(recommendation.Reasons);
        Assert.Contains(ModelCapabilities.Coding, recommendation.Scenarios);
        Assert.Contains("长文档", recommendation.Scenarios);
    }

    [Fact]
    public void Cpu_only_recommendation_uses_zero_gpu_layers_and_lower_confidence()
    {
        var hw = CpuOnlyMachine(32);
        var score = new PerformanceScorer().Score(hw);
        var (_, recommendation) = new RecommendationEngine()
            .RecommendBestQuant(hw, score, Model14B(), new RecommendationOptions { ContextTokens = 4096 });

        Assert.NotNull(recommendation.Launch);
        Assert.Equal(0, recommendation.Launch!.GpuLayers);
        Assert.Contains("-ngl 0", recommendation.Launch.CommandLine);
        Assert.True(recommendation.Confidence <= 0.6, "没有实测且无独显时置信度应偏低");
        Assert.Contains(recommendation.Reasons, reason => reason.Contains("默认带宽", StringComparison.Ordinal));
        Assert.All(recommendation.Alternatives, item => Assert.NotEqual(FitLevel.Perfect, item.Fit));
    }
}
