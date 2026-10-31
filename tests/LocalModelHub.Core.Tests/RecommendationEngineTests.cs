using LocalModelHub.Core.Models;
using LocalModelHub.Core.Services;
using Xunit;

namespace LocalModelHub.Core.Tests;

public class RecommendationEngineTests
{
    private static readonly RecommendationEngine Engine = new();

    private static HardwareProfile Hardware(double vramGb, double ramGb, bool avx2 = true)
    {
        var gpu = new GpuInfo("NVIDIA GeForce RTX 4060", GpuInfo.VendorNvidia,
            (long)(vramGb * Formatters.Gib), (long)(8 * Formatters.Gib));
        return new HardwareProfile(
            "TEST-PC", "Windows 11",
            new CpuInfo("Test CPU", 8, 16, 4200, 16384),
            (long)(ramGb * Formatters.Gib),
            (long)(ramGb * 0.5 * Formatters.Gib),
            [gpu],
            (long)(512 * Formatters.Gib),
            (long)(200 * Formatters.Gib),
            avx2, true,
            DateTime.Now);
    }

    private static HardwareProfile CpuOnly(double ramGb)
    {
        return new HardwareProfile(
            "CPU-PC", "Windows 10",
            new CpuInfo("Intel i5", 6, 12, 3600, 12288),
            (long)(ramGb * Formatters.Gib),
            (long)(ramGb * 0.6 * Formatters.Gib),
            Array.Empty<GpuInfo>(),
            (long)(512 * Formatters.Gib),
            (long)(100 * Formatters.Gib),
            true, false,
            DateTime.Now);
    }

    private static ModelCatalog Catalog() => new CatalogService().LoadEmbedded();

    [Fact]
    public void Catalog_ContainsDomesticAndForeignModels()
    {
        var catalog = Catalog();
        Assert.True(catalog.Models.Count >= 18);
        Assert.Contains(catalog.Models, m => m.Country == "CN");
        Assert.Contains(catalog.Models, m => m.Country != "CN");
        Assert.Equal(catalog.Models.Count, catalog.Models.Select(m => m.Id).Distinct().Count());
        Assert.All(catalog.Models, m =>
        {
            Assert.NotEmpty(m.Quants);
            Assert.True(m.PrimaryQuant.TotalBytes > 0);
            Assert.NotEmpty(m.PrimaryQuant.Files);
            Assert.Contains(m.PrimaryQuant.Sources, s => s.Repo.Length > 0);
            Assert.Equal(m.Quants.Length, m.Quants.Select(q => q.Label).Distinct().Count());
            Assert.Equal(m.Quants.Length, m.Quants.Select(q => q.Files[0].Path).Distinct().Count());
            Assert.All(m.Quants, q => Assert.True(q.TotalBytes > 0));
        });

        Assert.Contains(catalog.Models, m => m.Quants.Length >= 3);
    }

    [Fact]
    public void Catalog_CapabilitiesAreValidAndCoverCoding()
    {
        var catalog = Catalog();
        Assert.All(catalog.Models, m =>
        {
            Assert.NotEmpty(m.Capabilities);
            Assert.All(m.Capabilities, c => Assert.Contains(c, ModelCapabilities.Filters));
        });

        var coding = catalog.Models.Where(m => m.Capabilities.Contains(ModelCapabilities.Coding)).ToList();
        Assert.True(coding.Count >= 6);
        Assert.Contains(coding, m => m.Id == "qwen2.5-coder-14b");
        Assert.Contains(coding, m => m.Id == "qwen2.5-coder-32b");
        Assert.Contains(coding, m => m.Id == "deepseek-coder-v2-lite");
    }

    [Fact]
    public void Qwen3_8B_FitsIn8GbVram()
    {
        var hw = Hardware(8, 32);
        var score = new PerformanceScorer().Score(hw);
        var catalog = Catalog();
        var model = catalog.Models.Single(m => m.Id == "qwen3-8b");

        var recommendation = Engine.Evaluate(hw, score, model, model.PrimaryQuant);

        // 8 GB 卡的 8B Q4 仍可完整装入，但已接近九成预算，边界由 Different_quants 用例覆盖。
        Assert.Equal(FitLevel.Perfect, recommendation.Fit);
        Assert.InRange(recommendation.EstimatedTokensPerSecond, 20, 120);
    }

    [Fact]
    public void Different_quants_get_different_fit_levels()
    {
        var hw = Hardware(24, 64);
        var score = new PerformanceScorer().Score(hw);
        var template = Catalog().Models.Single(m => m.Id == "qwen2.5-coder-32b");
        var model = Variant(template, "q32", "Qwen2.5 Coder 32B", 1_000_000, 2025);
        model.Quants =
        [
            new ModelQuant { Label = "Q2_K", TotalBytes = (long)(9 * Formatters.Gib) },
            new ModelQuant { Label = "Q4_K_M", TotalBytes = (long)(19 * Formatters.Gib) },
            new ModelQuant { Label = "Q8_0", TotalBytes = (long)(34 * Formatters.Gib) }
        ];

        var small = Engine.Evaluate(hw, score, model, model.Quants[0]);
        var large = Engine.Evaluate(hw, score, model, model.Quants[1]);

        Assert.Equal(FitLevel.Perfect, small.Fit);
        Assert.NotEqual(FitLevel.Perfect, large.Fit);
        Assert.True(small.SortScore > large.SortScore);
    }

    [Fact]
    public void Large_model_kv_cache_is_not_underestimated()
    {
        Assert.True(RecommendationEngine.EstimateKvCacheGb(32, 8192) >= 1.3);
        Assert.True(RecommendationEngine.EstimateKvCacheGb(70, 8192) >= 2.0);
    }

    [Fact]
    public void Longer_context_lowers_fit_for_big_models()
    {
        var hw = Hardware(24, 64);
        var score = new PerformanceScorer().Score(hw);
        var template = Catalog().Models.Single(m => m.Id == "qwen2.5-coder-32b");
        var model = Variant(template, "q32ctx", "Qwen2.5 Coder 32B", 1_000_000, 2025);
        model.Quants = [new ModelQuant { Label = "Q4_K_M", TotalBytes = (long)(17 * Formatters.Gib) }];

        var shortCtx = Engine.Evaluate(hw, score, model, model.PrimaryQuant,
            new RecommendationOptions { ContextTokens = 4096 });
        var longCtx = Engine.Evaluate(hw, score, model, model.PrimaryQuant,
            new RecommendationOptions { ContextTokens = 32768 });

        Assert.True(longCtx.RequiredVramGb > shortCtx.RequiredVramGb);
        Assert.True(longCtx.Fit > shortCtx.Fit, "上下文变长后适配等级应下降");
    }

    [Fact]
    public void Gpu_prefill_is_faster_than_cpu()
    {
        var hw = Hardware(8, 32);
        var cpuOnly = CpuOnly(16);
        var model = Catalog().Models.Single(m => m.Id == "qwen3-8b");

        var onboard = Engine.Evaluate(hw, new PerformanceScorer().Score(hw), model, model.PrimaryQuant);
        var cpu = Engine.Evaluate(cpuOnly, new PerformanceScorer().Score(cpuOnly), model, model.PrimaryQuant);

        Assert.True(onboard.PrefillSeconds < cpu.PrefillSeconds);
        Assert.True(onboard.PrefillSeconds < 3, $"8B 在 8 GB 卡上首字不应超过 3 秒，实际 {onboard.PrefillSeconds}");
        Assert.True(cpu.PrefillSeconds > 0);
    }

    [Fact]
    public void Speed_label_reports_a_range()
    {
        var hw = Hardware(24, 64);
        var model = Catalog().Models.Single(m => m.Id == "qwen3-8b");
        var recommendation = Engine.Evaluate(hw, new PerformanceScorer().Score(hw), model, model.PrimaryQuant);

        Assert.Contains("–", recommendation.SpeedLabel);
        Assert.Contains("tok/s", recommendation.SpeedLabel);
    }

    [Fact]
    public void LargeMoeModel_FallsBackToCpuWith32GbRam()
    {
        var hw = Hardware(8, 32);
        var score = new PerformanceScorer().Score(hw);
        var catalog = Catalog();
        var model = catalog.Models.Single(m => m.Id == "qwen3-30b-a3b");

        var recommendation = Engine.Evaluate(hw, score, model, model.PrimaryQuant);

        Assert.Equal(FitLevel.Playable, recommendation.Fit);
        Assert.True(recommendation.EstimatedTokensPerSecond > 0);
    }

    [Fact]
    public void CpuOnlyMachine_CanRunSmallModel()
    {
        var hw = new HardwareProfile(
            "CPU-PC", "Windows 10",
            new CpuInfo("Intel i5", 6, 12, 3600, 12288),
            (long)(16 * Formatters.Gib),
            (long)(10 * Formatters.Gib),
            Array.Empty<GpuInfo>(),
            (long)(512 * Formatters.Gib),
            (long)(100 * Formatters.Gib),
            true, false,
            DateTime.Now);
        var score = new PerformanceScorer().Score(hw);
        var catalog = Catalog();
        var model = catalog.Models.Single(m => m.Id == "qwen2.5-1.5b");

        var recommendation = Engine.Evaluate(hw, score, model, model.PrimaryQuant);

        Assert.Equal(FitLevel.Playable, recommendation.Fit);
        Assert.Contains("CPU", recommendation.Reason);
    }

    [Fact]
    public void Ranking_PutsRunnableModelsFirst()
    {
        var hw = Hardware(8, 32);
        var score = new PerformanceScorer().Score(hw);
        var catalog = Catalog();
        var results = Engine.Recommend(hw, score, catalog);

        Assert.True(results.Count <= catalog.Models.Count, "去重后不应超过目录数量");
        Assert.Equal(FitLevel.Perfect, results[0].Fit);
        Assert.True(results.Zip(results.Skip(1)).All(pair => pair.First.SortScore >= pair.Second.SortScore));
    }

    [Fact]
    public void QuantQuality_RanksHigherPrecisionFirst()
    {
        Assert.True(RecommendationEngine.QuantQuality("Q8_0") > RecommendationEngine.QuantQuality("Q5_K_M"));
        Assert.True(RecommendationEngine.QuantQuality("Q5_K_M") > RecommendationEngine.QuantQuality("Q4_K_M"));
        Assert.True(RecommendationEngine.QuantQuality("Q4_K_M") > RecommendationEngine.QuantQuality("Q3_K_M"));
        Assert.True(RecommendationEngine.QuantQuality("Q3_K_M") > RecommendationEngine.QuantQuality("Q2_K"));
        Assert.True(RecommendationEngine.QuantQuality("F16") > RecommendationEngine.QuantQuality("Q8_0"));
    }

    [Fact]
    public void RecommendBestQuant_NeverPicksWorseFitThanPrimaryQuant()
    {
        var hw = Hardware(8, 32);
        var score = new PerformanceScorer().Score(hw);
        var catalog = Catalog();

        foreach (var model in catalog.Models)
        {
            var primary = Engine.Evaluate(hw, score, model, model.PrimaryQuant);
            var (index, best) = Engine.RecommendBestQuant(hw, score, model);

            Assert.True((int)best.Fit <= (int)primary.Fit, $"{model.Id} 推荐档位不应差于默认档位");
            Assert.Equal(model.Quants[index].Label, best.Quant.Label);
        }
    }

    [Fact]
    public void RecommendBestQuant_ChoosesHighestFitThenQuality()
    {
        var hw = Hardware(8, 32);
        var score = new PerformanceScorer().Score(hw);
        var model = Catalog().Models.Single(m => m.Id == "qwen3-8b");

        var evaluated = model.Quants.Select(q => Engine.Evaluate(hw, score, model, q)).ToList();
        var topFit = evaluated.Min(r => (int)r.Fit);
        var expected = evaluated
            .Where(r => (int)r.Fit == topFit)
            .OrderByDescending(r => RecommendationEngine.QuantQuality(r.Quant.Label))
            .First();

        var (index, best) = Engine.RecommendBestQuant(hw, score, model);

        Assert.Equal(topFit, (int)best.Fit);
        Assert.Equal(expected.Quant.Label, best.Quant.Label);
        Assert.Equal(model.Quants[index].Label, best.Quant.Label);
    }

    [Fact]
    public void RecommendBestQuant_WhenNothingFits_FallsBackToBalancedQuant()
    {
        var hw = new HardwareProfile(
            "SMALL-PC", "Windows 10",
            new CpuInfo("Intel i3", 4, 8, 3200, 8192),
            (long)(6 * Formatters.Gib),
            (long)(4 * Formatters.Gib),
            Array.Empty<GpuInfo>(),
            (long)(256 * Formatters.Gib),
            (long)(60 * Formatters.Gib),
            true, false,
            DateTime.Now);
        var score = new PerformanceScorer().Score(hw);
        var model = Catalog().Models.Single(m => m.Id == "qwen3-8b");

        var (_, best) = Engine.RecommendBestQuant(hw, score, model);

        Assert.True(best.Fit >= FitLevel.Tight);
        Assert.Equal("Q4_K_M", best.Quant.Label);
    }

    [Fact]
    public void GpuLayers_FullOffload_WhenVramLarge()
    {
        var hw = Hardware(24, 64);
        var score = new PerformanceScorer().Score(hw);
        var bytes = (long)(5 * Formatters.Gib);
        var layers = RecommendationEngine.ComputeGpuLayers(score, bytes, 36, 8192);
        Assert.Equal(999, layers);
    }

    [Fact]
    public void GpuLayers_PartialOffload_IsBoundedByBlockCount()
    {
        var hw = Hardware(6, 32);
        var score = new PerformanceScorer().Score(hw);
        var bytes = (long)(9 * Formatters.Gib);
        var layers = RecommendationEngine.ComputeGpuLayers(score, bytes, 40, 8192);
        Assert.InRange(layers, 1, 39);
    }

    [Fact]
    public void DownloadUrls_UseSelectedProvider()
    {
        var source = new ModelSource { Provider = "ModelScope", Repo = "Qwen/Qwen3-8B-GGUF" };
        var settings = new AppSettings();
        var ms = DownloadUrlBuilder.Build(source, "Qwen3-8B-Q4_K_M.gguf", settings);
        Assert.StartsWith("https://modelscope.cn/api/v1/models/Qwen/Qwen3-8B-GGUF/repo", ms);
        Assert.Contains("FilePath=Qwen3-8B-Q4_K_M.gguf", ms);

        var hfSource = new ModelSource { Provider = "HuggingFace", Repo = "unsloth/Llama-3.1-8B-Instruct-GGUF" };
        var hf = DownloadUrlBuilder.Build(hfSource, "Llama-3.1-8B-Instruct-Q4_K_M.gguf", settings);
        Assert.StartsWith("https://hf-mirror.com/unsloth/Llama-3.1-8B-Instruct-GGUF/resolve/main/", hf);
    }

    [Fact]
    public void Suggested_context_shrinks_when_32k_exceeds_memory()
    {
        // 32 GB 纯 CPU 机器跑 32B Q4：32K 上下文的 KV cache 会超出内存预算。
        var hw = CpuOnly(32);
        var score = new PerformanceScorer().Score(hw);
        var template = Catalog().Models.Single(m => m.Id == "qwen2.5-coder-32b");
        var model = Variant(template, "ctx32", "Qwen2.5 Coder 32B", 1_000_000, 2025);
        model.Quants = [new ModelQuant { Label = "Q4_K_M", TotalBytes = (long)(19 * Formatters.Gib) }];

        var rec = Engine.Evaluate(hw, score, model, model.PrimaryQuant,
            new RecommendationOptions { ContextTokens = 32768 });

        Assert.True(rec.Fit >= FitLevel.Tight, "32K 上下文下结论应至少是「勉强可跑」");
        Assert.True(rec.SuggestedContextTokens < 32768,
            $"32K 超出内存预算时应建议更小上下文，实际 {rec.SuggestedContextTokens}");
        Assert.True(rec.HasContextAdvice, "建议上下文可运行且低于当前设置时应给出提示");
        Assert.Contains($"{rec.SuggestedContextTokens / 1024}K", rec.ContextNote);

        var atSuggested = Engine.Evaluate(hw, score, model, model.PrimaryQuant,
            new RecommendationOptions { ContextTokens = rec.SuggestedContextTokens });
        Assert.True(atSuggested.Fit <= FitLevel.Playable, "建议上下文本身必须落在可运行档位");
    }

    [Fact]
    public void Suggested_context_matches_setting_when_budget_is_ample()
    {
        var hw = Hardware(24, 64);
        var score = new PerformanceScorer().Score(hw);
        var model = Catalog().Models.Single(m => m.Id == "qwen3-8b");

        var rec = Engine.Evaluate(hw, score, model, model.PrimaryQuant,
            new RecommendationOptions { ContextTokens = 8192 });

        Assert.Equal(FitLevel.Perfect, rec.Fit);
        Assert.Equal(8192, rec.SuggestedContextTokens);
        Assert.False(rec.HasContextAdvice);
        Assert.Contains("最佳适配档内", rec.ContextNote);
    }

    [Fact]
    public void Suggested_context_has_no_advice_when_even_2k_fails()
    {
        // 6 GB 内存跑 8B Q4：任何上下文都装不下，此时不应提示「建议 2K」。
        var hw = new HardwareProfile(
            "SMALL-PC", "Windows 10",
            new CpuInfo("Intel i3", 4, 8, 3200, 8192),
            (long)(6 * Formatters.Gib),
            (long)(4 * Formatters.Gib),
            Array.Empty<GpuInfo>(),
            (long)(256 * Formatters.Gib),
            (long)(60 * Formatters.Gib),
            true, false,
            DateTime.Now);
        var score = new PerformanceScorer().Score(hw);
        var model = Catalog().Models.Single(m => m.Id == "qwen3-8b");

        var rec = Engine.Evaluate(hw, score, model, model.PrimaryQuant,
            new RecommendationOptions { ContextTokens = 8192 });

        Assert.Equal(2048, rec.SuggestedContextTokens);
        Assert.False(rec.HasContextAdvice);
        Assert.Contains("2K", rec.ContextNote);
    }

    [Fact]
    public void Suggested_context_never_exceeds_declared_length()
    {
        var hw = Hardware(24, 64);
        var score = new PerformanceScorer().Score(hw);
        var template = Catalog().Models.Single(m => m.Id == "qwen3-8b");
        var model = Variant(template, "short-ctx", "Qwen3 8B 4K", 1_000_000, 2025);
        model.ContextLength = 4096;

        var rec = Engine.Evaluate(hw, score, model, model.PrimaryQuant,
            new RecommendationOptions { ContextTokens = 32768 });

        Assert.Equal(4096, rec.SuggestedContextTokens);
        Assert.Contains("4K", rec.ContextNote);
    }

    [Fact]
    public void Reserved_vram_from_other_apps_lowers_fit()
    {
        var hw = Hardware(12, 64);
        var score = new PerformanceScorer().Score(hw);
        var model = Catalog().Models.Single(m => m.Id == "qwen3-8b");

        var without = Engine.Evaluate(hw, score, model, model.PrimaryQuant, new RecommendationOptions());
        var with = Engine.Evaluate(hw, score, model, model.PrimaryQuant,
            new RecommendationOptions { ReservedVramGb = 6 });

        Assert.Equal(FitLevel.Perfect, without.Fit);
        Assert.True(with.Fit > without.Fit, "扣除其它程序占用的 6 GB 显存后应降级");
        Assert.Equal(6, with.ReservedVramGb);
        Assert.True(with.RequiredVramGb > with.ReservedVramGb);
    }

    [Fact]
    public void Reserved_vram_is_clamped_to_available_vram()
    {
        // 预留值大于整卡显存时要收敛到可用显存，不能把预算算成负数。
        var hw = Hardware(12, 64);
        var score = new PerformanceScorer().Score(hw);
        var model = Catalog().Models.Single(m => m.Id == "qwen3-8b");

        var rec = Engine.Evaluate(hw, score, model, model.PrimaryQuant,
            new RecommendationOptions { ReservedVramGb = 20 });

        Assert.Equal(score.BestGpu!.UsableVramGb, rec.ReservedVramGb, 3);
        Assert.Equal(FitLevel.Playable, rec.Fit);
    }

    [Fact]
    public void Suggested_context_is_evaluated_per_quant()
    {
        // 同一模型下，Q8 的权重大于 Q4，能开的上下文必须更短，不能把 Q4 的结论套到 Q8。
        var hw = Hardware(48, 64);
        var score = new PerformanceScorer().Score(hw);
        var template = Catalog().Models.Single(m => m.Id == "qwen2.5-coder-32b");
        var model = Variant(template, "per-quant-ctx", "Qwen2.5 Coder 32B", 1_000_000, 2025);
        model.Quants =
        [
            new ModelQuant { Label = "Q4_K_M", TotalBytes = (long)(19 * Formatters.Gib) },
            new ModelQuant { Label = "Q8_0", TotalBytes = (long)(34 * Formatters.Gib) }
        ];

        var options = new RecommendationOptions { ContextTokens = 32768 };
        var small = Engine.Evaluate(hw, score, model, model.Quants[0], options);
        var large = Engine.Evaluate(hw, score, model, model.Quants[1], options);

        Assert.True(large.SuggestedContextTokens < small.SuggestedContextTokens,
            $"Q8 建议上下文 {large.SuggestedContextTokens} 应小于 Q4 的 {small.SuggestedContextTokens}");
        Assert.Equal(32768, small.SuggestedContextTokens);
    }

    private static ModelEntry Variant(
        ModelEntry template,
        string id,
        string displayName,
        long downloads,
        int releaseYear) => new()
    {
        Id = id,
        Name = displayName,
        DisplayName = displayName,
        Developer = template.Developer,
        Country = template.Country,
        Family = template.Family,
        ParametersB = template.ParametersB,
        ActiveParametersB = template.ActiveParametersB,
        ContextLength = template.ContextLength,
        ReleaseYear = releaseYear,
        Tags = template.Tags,
        Capabilities = template.Capabilities,
        Quants = template.Quants,
        Downloads = downloads
    };

    private static ModelCatalog Only(params ModelEntry[] models) => new() { Models = [.. models] };

    [Fact]
    public void Kv_cache_grows_with_context_and_parameters()
    {
        var small = RecommendationEngine.EstimateKvCacheGb(0.8, 8192);
        var mid = RecommendationEngine.EstimateKvCacheGb(8, 8192);
        var large = RecommendationEngine.EstimateKvCacheGb(32, 8192);

        Assert.True(mid > small * 2, $"8B KV {mid:0.##} GB 应明显大于 0.8B {small:0.##} GB");
        Assert.True(large > mid);
        Assert.True(RecommendationEngine.EstimateKvCacheGb(8, 32768) > mid * 3);
    }

    [Fact]
    public void Mid_size_model_outranks_tiny_model_when_both_run()
    {
        var hw = Hardware(8, 32);
        var score = new PerformanceScorer().Score(hw);
        var results = Engine.Recommend(hw, score, Catalog());

        var tiny = results.First(r => r.Model.ParametersB > 0 && r.Model.ParametersB < 1.5 && r.Fit <= FitLevel.Playable);
        var mid = results.First(r => r.Model.ParametersB is >= 7 and < 10 && r.Fit <= FitLevel.Playable);

        Assert.True(mid.SortScore > tiny.SortScore, $"{mid.Model.Id} 应比 {tiny.Model.Id} 更靠前");
    }

    [Fact]
    public void Duplicate_repositories_are_collapsed()
    {
        var template = Catalog().Models.Single(m => m.Id == "qwen3-8b");
        var official = Variant(template, "official", "Qwen3 8B GGUF", 1_000_000, 2025);
        var thirdParty = Variant(template, "third", "Qwen3-8B-GGUF", 20_000, 2025);

        var results = Engine.Recommend(Hardware(8, 32), new PerformanceScorer().Score(Hardware(8, 32)),
            Only(official, thirdParty));

        Assert.Single(results);
        Assert.Equal("official", results[0].Model.Id);
    }

    [Fact]
    public void Experimental_and_unpopular_repositories_are_penalized()
    {
        var template = Catalog().Models.Single(m => m.Id == "qwen3-8b");
        var stable = Variant(template, "stable", "Qwen3 8B", 5_000_000, 2026);
        var rc = Variant(template, "rc", "Qwen3 8B RC2", 300, 2026);

        var hw = Hardware(8, 32);
        var score = new PerformanceScorer().Score(hw);

        Assert.True(
            Engine.Evaluate(hw, score, stable, stable.PrimaryQuant).SortScore >
            Engine.Evaluate(hw, score, rc, rc.PrimaryQuant).SortScore);
    }

    [Fact]
    public void Available_ram_limit_prevents_playable_verdict()
    {
        // 总内存 8 GB，只剩 2 GB 可用：5 GB 的模型不应被判定为「可运行」。
        var hw = new HardwareProfile(
            "LOW-RAM", "Windows 11",
            new CpuInfo("Intel i5", 6, 12, 3600, 12288),
            (long)(8 * Formatters.Gib),
            (long)(2 * Formatters.Gib),
            Array.Empty<GpuInfo>(),
            (long)(512 * Formatters.Gib),
            (long)(100 * Formatters.Gib),
            true, false,
            DateTime.Now);

        var template = Catalog().Models.Single(m => m.Id == "qwen3-8b");
        var model = Variant(template, "q4", "Qwen3 8B", 1_000_000, 2025);
        model.Quants = [new ModelQuant { Label = "Q4_K_M", TotalBytes = (long)(5 * Formatters.Gib) }];

        var recommendation = Engine.Evaluate(hw, new PerformanceScorer().Score(hw), model, model.PrimaryQuant);

        Assert.True(recommendation.Fit >= FitLevel.Tight);
    }

    [Fact]
    public void Low_speed_models_fall_behind_when_everything_fits()
    {
        var hw = Hardware(24, 64);
        var score = new PerformanceScorer().Score(hw);
        var list = Engine.Recommend(hw, score, Catalog()).ToList();

        var slowIndex = list.FindIndex(r =>
            r.Fit <= FitLevel.Playable && r.EstimatedTokensPerSecond < 3);
        if (slowIndex < 0) return;

        var fastIndex = list.FindIndex(r =>
            r.Fit == FitLevel.Perfect && r.EstimatedTokensPerSecond >= 10);
        Assert.True(fastIndex >= 0 && fastIndex < slowIndex, "低于 3 tok/s 的模型应排在可用速度模型之后");
    }
}

public class PerformanceScorerTests
{
    [Fact]
    public void HighEndMachine_BeatsEntryMachine()
    {
        var strong = new HardwareProfile(
            "A", "Windows 11",
            new CpuInfo("Ryzen 9", 16, 32, 5200, 65536),
            (long)(64 * Formatters.Gib), (long)(40 * Formatters.Gib),
            [new GpuInfo("NVIDIA GeForce RTX 4090", GpuInfo.VendorNvidia, (long)(24 * Formatters.Gib), 0)],
            (long)(1000 * Formatters.Gib), (long)(500 * Formatters.Gib), true, false, DateTime.Now);
        var weak = new HardwareProfile(
            "B", "Windows 10",
            new CpuInfo("Celeron", 2, 2, 2000, 4096),
            (long)(8 * Formatters.Gib), (long)(4 * Formatters.Gib),
            Array.Empty<GpuInfo>(),
            (long)(256 * Formatters.Gib), (long)(50 * Formatters.Gib), false, false, DateTime.Now);

        var scorer = new PerformanceScorer();
        var strongScore = scorer.Score(strong);
        var weakScore = scorer.Score(weak);

        Assert.True(strongScore.Total > weakScore.Total);
        Assert.Equal("发烧级", strongScore.Tier);
        Assert.NotNull(strongScore.BestGpu);
        Assert.Equal(1008, strongScore.BestGpu!.BandwidthGBps);
        Assert.Null(weakScore.BestGpu);
    }

    [Fact]
    public void Summary_uses_budget_limits_not_score_tiers()
    {
        var hw = new HardwareProfile(
            "D", "Windows 11",
            new CpuInfo("i5", 6, 12, 3600, 16384),
            (long)(16 * Formatters.Gib), (long)(10 * Formatters.Gib),
            [new GpuInfo("NVIDIA GeForce RTX 4060 Ti", GpuInfo.VendorNvidia, (long)(8 * Formatters.Gib), 0)],
            (long)(512 * Formatters.Gib), (long)(200 * Formatters.Gib), true, false, DateTime.Now);

        var score = new PerformanceScorer().Score(hw);

        // 文案必须来自「显存/内存预算扣掉 KV 与计算缓冲」的推导，而不是综合分档位。
        Assert.Contains("Q4_K_M", score.Summary);
        Assert.Contains("显存可完整装入约", score.Summary);
        Assert.DoesNotContain("流畅运行", score.Summary);
    }

    [Fact]
    public void IntegratedGpu_IsNotUsedForOffload()
    {
        var hw = new HardwareProfile(
            "C", "Windows 11",
            new CpuInfo("i7", 8, 16, 4000, 16384),
            (long)(16 * Formatters.Gib), (long)(10 * Formatters.Gib),
            [new GpuInfo("Intel(R) Iris(R) Xe Graphics", GpuInfo.VendorIntel, (long)(0.1 * Formatters.Gib), (long)(8 * Formatters.Gib))],
            (long)(512 * Formatters.Gib), (long)(200 * Formatters.Gib), true, false, DateTime.Now);

        var capability = GpuDatabase.GetBestGpu(hw);

        Assert.NotNull(capability);
        Assert.True(capability!.IsIntegrated);
        Assert.Equal(0, capability.UsableVramBytes);
    }

    [Fact]
    public void Score_PartsAddUpToHundredPointScale()
    {
        var hw = new HardwareProfile(
            "A", "Windows 11",
            new CpuInfo("Ryzen 9", 16, 32, 5200, 65536),
            (long)(64 * Formatters.Gib), (long)(40 * Formatters.Gib),
            [new GpuInfo("NVIDIA GeForce RTX 4090", GpuInfo.VendorNvidia, (long)(24 * Formatters.Gib), 0)],
            (long)(1000 * Formatters.Gib), (long)(500 * Formatters.Gib), true, false, DateTime.Now);

        var score = new PerformanceScorer().Score(hw);
        var parts = score.CpuPoints + score.MemoryPoints + score.GpuPoints + score.StoragePoints;

        Assert.Equal(100, PerformanceScoreScale.Total);
        Assert.InRange(parts, score.Total - 1, score.Total + 1);
        Assert.True(score.StoragePoints > 10, "磁盘容量与剩余空间应计入总分");
        Assert.Equal("发烧级", score.Tier);
    }

    [Fact]
    public void Score_RewardsWideInstructionSetsAndAvailableMemory()
    {
        static HardwareProfile Machine(bool avx2, bool avx512, double availableGb) => new(
            "B", "Windows 11",
            new CpuInfo("Test CPU", 8, 16, 3800, 16384),
            (long)(32 * Formatters.Gib), (long)(availableGb * Formatters.Gib),
            Array.Empty<GpuInfo>(),
            (long)(512 * Formatters.Gib), (long)(300 * Formatters.Gib),
            avx2, avx512, DateTime.Now);

        var scorer = new PerformanceScorer();
        var avx512Score = scorer.Score(Machine(true, true, 24));
        var avx2Score = scorer.Score(Machine(true, false, 24));
        var plainScore = scorer.Score(Machine(false, false, 24));
        var tightScore = scorer.Score(Machine(true, true, 3));

        Assert.True(avx512Score.Total > avx2Score.Total, "AVX512 的 CPU 分应高于 AVX2");
        Assert.True(avx2Score.Total > plainScore.Total, "支持 AVX2 的 CPU 分应高于无 AVX2");
        Assert.True(avx512Score.MemoryPoints > tightScore.MemoryPoints, "可用内存过低应拉低内存分");
    }

    [Fact]
    public void Score_IgnoresStorageWhenDiskIsUnknown()
    {
        var hw = new HardwareProfile(
            "D", "Windows 11",
            new CpuInfo("i5", 6, 12, 3600, 12288),
            (long)(16 * Formatters.Gib), (long)(10 * Formatters.Gib),
            Array.Empty<GpuInfo>(),
            0, 0, true, false, DateTime.Now);

        var score = new PerformanceScorer().Score(hw);

        Assert.Equal(0d, score.StoragePoints);
    }
}
