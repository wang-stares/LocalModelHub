using LocalModelHub.Core.Services;
using Xunit;

namespace LocalModelHub.Core.Tests;

/// <summary>内置目录的覆盖度与可下载性：新增模型后避免出现缺字段、重复 ID 或空量化。</summary>
public class CatalogCoverageTests
{
    [Fact]
    public void Embedded_catalog_covers_more_than_sixty_models_and_many_families()
    {
        var catalog = new CatalogService().LoadEmbedded();

        Assert.True(catalog.Models.Count >= 60, $"内置目录应至少 60 个模型，当前 {catalog.Models.Count}");
        var families = catalog.Models
            .Select(model => model.Family)
            .Where(family => !string.IsNullOrWhiteSpace(family))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();
        Assert.True(families >= 20, $"模型家族应至少 20 个，当前 {families}");
        Assert.True(catalog.Models.Count(model => model.ParametersB <= 8) >= 25, "应保留足够多的小模型供普通电脑使用");
    }

    [Fact]
    public void Every_embedded_model_has_downloadable_quant()
    {
        var catalog = new CatalogService().LoadEmbedded();

        foreach (var model in catalog.Models)
        {
            Assert.False(string.IsNullOrWhiteSpace(model.Id), "模型缺少 id");
            Assert.False(string.IsNullOrWhiteSpace(model.HfRepo), $"{model.Id} 缺少仓库地址");
            Assert.False(string.IsNullOrWhiteSpace(model.DisplayName), $"{model.Id} 缺少显示名");
            Assert.NotEmpty(model.Quants);
            foreach (var quant in model.Quants)
            {
                Assert.True(quant.TotalBytes > 0, $"{model.Id} 的 {quant.Label} 体积为 0");
                Assert.NotEmpty(quant.Files);
                Assert.NotEmpty(quant.Sources);
            }
        }

        Assert.Equal(
            catalog.Models.Count,
            catalog.Models.Select(model => model.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal(
            catalog.Models.Count,
            catalog.Models.Select(model => model.HfRepo).Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }
}
