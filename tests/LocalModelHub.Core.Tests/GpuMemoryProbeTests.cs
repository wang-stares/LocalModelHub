using LocalModelHub.Core.Services;
using Xunit;

namespace LocalModelHub.Core.Tests;

public class GpuMemoryProbeTests
{
    [Fact]
    public void Parse_reads_gpu_name_and_used_memory()
    {
        var samples = GpuMemoryProbe.Parse("NVIDIA GeForce RTX 4060, 1536\nNVIDIA GeForce RTX 3060, 512\n");

        Assert.Equal(2, samples.Count);
        Assert.Equal("NVIDIA GeForce RTX 4060", samples[0].Name);
        Assert.Equal(1.5, samples[0].UsedGb, 3);
        Assert.Equal(0.5, samples[1].UsedGb, 3);
    }

    [Fact]
    public void Parse_skips_unparsable_values()
    {
        var samples = GpuMemoryProbe.Parse("NVIDIA GeForce RTX 4060, N/A\nNVIDIA GeForce RTX 3060, 2048\n\n");

        Assert.Single(samples);
        Assert.Equal("NVIDIA GeForce RTX 3060", samples[0].Name);
        Assert.Equal(2.0, samples[0].UsedGb, 3);
    }

    [Fact]
    public void Select_prefers_matching_gpu_then_max()
    {
        var samples = new[]
        {
            new GpuMemorySample("NVIDIA GeForce RTX 4060", 3.0),
            new GpuMemorySample("NVIDIA GeForce RTX 3090", 8.0)
        };

        Assert.Equal(3.0, GpuMemoryProbe.SelectUsedGb(samples, "NVIDIA GeForce RTX 4060"), 3);
        Assert.Equal(8.0, GpuMemoryProbe.SelectUsedGb(samples, "Intel Arc A770"), 3);
        Assert.Equal(8.0, GpuMemoryProbe.SelectUsedGb(samples, null), 3);
        Assert.Equal(0.0, GpuMemoryProbe.SelectUsedGb([], "NVIDIA GeForce RTX 4060"), 3);
    }

    [Fact]
    public void ReservedVram_ignores_driver_baseline()
    {
        Assert.Equal(0, GpuMemoryProbe.ReservedFrom(0));
        Assert.Equal(0, GpuMemoryProbe.ReservedFrom(0.5));
        Assert.Equal(0.7, GpuMemoryProbe.ReservedFrom(1.5), 3);
        Assert.Equal(7.2, GpuMemoryProbe.ReservedFrom(8.0), 3);
    }

    [Fact]
    public void Probe_returns_zero_when_nvidia_smi_is_missing()
    {
        var probe = new GpuMemoryProbe(executable: "nvidia-smi-missing-for-test");

        Assert.Equal(0, probe.UsedVramGb());
        Assert.Equal(0, probe.ReservedVramGb());
        probe.Invalidate();
        Assert.Equal(0, probe.UsedVramGb());
    }
}
