using LocalModelHub.Core.Models;
using LocalModelHub.Core.Services;
using Xunit;

namespace LocalModelHub.Core.Tests;

/// <summary>P1-12：量化体积倒挂（升量化反而更小）必须被修正并标记为估算。</summary>
public sealed class CatalogMergeServiceTests
{
    private static ModelEntry Model(double parametersB, params (string Label, double SizeGb)[] quants) => new()
    {
        Id = "test-model",
        Name = "Test Model",
        DisplayName = "Test Model",
        ParametersB = parametersB,
        ContextLength = 8192,
        Quants = quants
            .Select(item => new ModelQuant
            {
                Label = item.Label,
                TotalBytes = (long)(item.SizeGb * Formatters.Gib),
                Files = [new ModelFile { Path = $"{item.Label}.gguf", Bytes = 1 }]
            })
            .ToArray()
    };

    private static ModelCatalog Merge(ModelEntry model) =>
        CatalogMergeService.Merge(
            new ModelCatalog(),
            Array.Empty<ModelEntry>(),
            new RemoteCatalogCache { Models = [model], UpdatedAt = "2026-11-01" });

    [Fact]
    public void Inverted_quant_sizes_are_recomputed_and_marked_estimated()
    {
        // 实测案例：LFM2.5-2.6B 的 Q4_0 2.97 GB 反而大于 Q8_0 2.68 GB。
        var merged = Merge(Model(2.6, ("Q4_0", 2.97), ("Q8_0", 2.68)));
        var quants = merged.Models.Single().Quants;
        var q4 = quants.Single(quant => quant.Label == "Q4_0");
        var q8 = quants.Single(quant => quant.Label == "Q8_0");

        Assert.True(q4.TotalBytes < q8.TotalBytes, "修正后低比特量化的体积必须小于高比特量化");
        Assert.Equal("估算", q4.Note);
        Assert.True(q4.TotalBytes > 0);
    }

    [Fact]
    public void Shard_aggregation_error_is_recomputed_from_parameters()
    {
        // 27B 的 Q4 不可能有 81 GB：分片被重复聚合。
        var merged = Merge(Model(27, ("Q4_K_M", 81.15), ("Q8_0", 53.26)));
        var quants = merged.Models.Single().Quants;
        var q4 = quants.Single(quant => quant.Label == "Q4_K_M");
        var q8 = quants.Single(quant => quant.Label == "Q8_0");

        Assert.True(q4.TotalBytes < q8.TotalBytes);
        Assert.True(q4.TotalBytes < 30L * 1024 * 1024 * 1024, "27B Q4 的估算体积应回到合理量级");
        Assert.Equal("估算", q4.Note);
    }

    [Fact]
    public void Monotonic_quant_sizes_are_left_untouched()
    {
        var merged = Merge(Model(7, ("Q4_K_M", 4.4), ("Q8_0", 7.8), ("F16", 14.2)));
        var quants = merged.Models.Single().Quants;

        Assert.All(quants, quant => Assert.Null(quant.Note));
        Assert.Equal(
            new[] { "Q4_K_M", "Q8_0", "F16" },
            quants.OrderBy(quant => quant.TotalBytes).Select(quant => quant.Label).ToArray());
    }
}
