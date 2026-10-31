using LocalModelHub.Core.Models;
using LocalModelHub.Core.Services;
using Xunit;

namespace LocalModelHub.Core.Tests;

public class GpuDatabaseTests
{
    [Fact]
    public void Unknown_vram_nvidia_is_not_treated_as_integrated()
    {
        var gpu = new GpuInfo("NVIDIA GeForce RTX 5060 Laptop GPU", GpuInfo.VendorNvidia, 0, 0, VramEstimated: true);

        Assert.False(GpuDatabase.IsIntegrated(gpu));
    }

    [Fact]
    public void Unknown_vram_discrete_gpu_gets_conservative_usable_memory()
    {
        var gpu = new GpuInfo("NVIDIA GeForce RTX 5060 Laptop GPU", GpuInfo.VendorNvidia, 0, 0, VramEstimated: true);
        var hw = HardwareProfile.Empty with { Gpus = [gpu] };

        var best = GpuDatabase.GetBestGpu(hw);

        Assert.NotNull(best);
        Assert.False(best!.IsIntegrated);
        Assert.Equal(
            GpuDatabase.UnknownDiscreteVramBytes - (long)(0.8 * Formatters.Gib),
            best.UsableVramBytes);
        Assert.True(best.UsableVramGb > 3.0);
    }

    [Fact]
    public void Unknown_vram_intel_uhd_stays_integrated()
    {
        var gpu = new GpuInfo("Intel(R) UHD Graphics 630", GpuInfo.VendorIntel, 0, 0, VramEstimated: true);
        var hw = HardwareProfile.Empty with { Gpus = [gpu] };

        var best = GpuDatabase.GetBestGpu(hw);

        Assert.NotNull(best);
        Assert.True(best!.IsIntegrated);
        Assert.Equal(0L, best.UsableVramBytes);
    }

    [Fact]
    public void Unknown_vram_intel_arc_is_discrete()
    {
        var gpu = new GpuInfo("Intel Arc B580", GpuInfo.VendorIntel, 0, 0, VramEstimated: true);

        Assert.False(GpuDatabase.IsIntegrated(gpu));
    }

    [Fact]
    public void Known_vram_is_used_instead_of_conservative_estimate()
    {
        const long vram = 8L * 1024 * 1024 * 1024;
        var gpu = new GpuInfo("NVIDIA GeForce RTX 4060", GpuInfo.VendorNvidia, vram, 0);
        var hw = HardwareProfile.Empty with { Gpus = [gpu] };

        var best = GpuDatabase.GetBestGpu(hw);

        Assert.NotNull(best);
        Assert.False(best!.IsIntegrated);
        Assert.Equal(vram - (long)(0.8 * Formatters.Gib), best.UsableVramBytes);
    }
}
