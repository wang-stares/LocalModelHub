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

    [Fact]
    public void ParseTotals_reads_gpu_name_and_total_memory_in_bytes()
    {
        var samples = GpuMemoryProbe.ParseTotals("NVIDIA GeForce RTX 4060, 8192\nNVIDIA GeForce RTX 3090, 24576\n");

        Assert.Equal(2, samples.Count);
        Assert.Equal("NVIDIA GeForce RTX 4060", samples[0].Name);
        Assert.Equal(8L * 1024 * 1024 * 1024, samples[0].Bytes);
        Assert.Equal(24L * 1024 * 1024 * 1024, samples[1].Bytes);
    }

    [Fact]
    public void ParseTotals_skips_na_and_invalid_rows()
    {
        var samples = GpuMemoryProbe.ParseTotals("NVIDIA GeForce RTX 4060, N/A\nNVIDIA GeForce RTX 3060, 0\nnot a row\n");

        Assert.Empty(samples);
    }

    [Fact]
    public void SelectTotalBytes_prefers_exact_then_model_token()
    {
        var samples = new[]
        {
            new GpuTotalSample("NVIDIA GeForce RTX 4060", 8L * 1024 * 1024 * 1024),
            new GpuTotalSample("NVIDIA GeForce RTX 3090", 24L * 1024 * 1024 * 1024)
        };

        Assert.Equal(8L * 1024 * 1024 * 1024, GpuMemoryProbe.SelectTotalBytes(samples, "NVIDIA GeForce RTX 4060"));
        Assert.Equal(24L * 1024 * 1024 * 1024, GpuMemoryProbe.SelectTotalBytes(samples, "NVIDIA GeForce RTX 3090"));
        // 名称多了后缀时按型号数字匹配，不能命中第一张卡
        Assert.Equal(8L * 1024 * 1024 * 1024, GpuMemoryProbe.SelectTotalBytes(samples, "RTX 4060 Laptop"));
    }

    [Fact]
    public void SelectTotalBytes_returns_zero_when_target_gpu_is_not_found()
    {
        var samples = new[]
        {
            new GpuTotalSample("NVIDIA GeForce RTX 4090", 24L * 1024 * 1024 * 1024),
            new GpuTotalSample("NVIDIA GeForce RTX 3060", 12L * 1024 * 1024 * 1024)
        };

        Assert.Equal(0, GpuMemoryProbe.SelectTotalBytes(samples, "NVIDIA GeForce RTX 5060"));
        Assert.Equal(24L * 1024 * 1024 * 1024, GpuMemoryProbe.SelectTotalBytes(samples, null));
        Assert.Equal(0, GpuMemoryProbe.SelectTotalBytes([], "NVIDIA GeForce RTX 5060"));
    }

    [Fact]
    public void QueryTotalBytes_returns_zero_when_nvidia_smi_is_missing()
    {
        Assert.Equal(0, GpuMemoryProbe.QueryTotalBytes("NVIDIA GeForce RTX 4060", "nvidia-smi-missing-for-test"));
    }

    [Fact]
    public void ParseDriverVersion_reads_first_line()
    {
        var version = GpuMemoryProbe.ParseDriverVersion("572.16\n590.10\n");

        Assert.NotNull(version);
        Assert.Equal(572, version!.Major);
        Assert.Equal(16, version.Minor);
    }

    [Theory]
    [InlineData("")]
    [InlineData("N/A")]
    [InlineData("not-a-version")]
    public void ParseDriverVersion_returns_null_for_invalid_output(string output)
    {
        Assert.Null(GpuMemoryProbe.ParseDriverVersion(output));
    }

    [Fact]
    public void QueryDriverVersion_returns_null_when_nvidia_smi_is_missing()
    {
        Assert.Null(GpuMemoryProbe.QueryDriverVersion("nvidia-smi-missing-for-test"));
    }
}
