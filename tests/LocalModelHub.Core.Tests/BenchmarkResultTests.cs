using LocalModelHub.Core.Models;
using Xunit;

namespace LocalModelHub.Core.Tests;

public class BenchmarkResultTests
{
    [Fact]
    public void Low_confidence_flags_relative_stdev_above_fifteen_percent()
    {
        var duration = TimeSpan.FromSeconds(2);

        Assert.False(new BenchmarkResult(40, 20, duration).LowConfidence);
        Assert.False(new BenchmarkResult(40, 20, duration, 0.15).LowConfidence);
        Assert.True(new BenchmarkResult(40, 20, duration, 0.2).LowConfidence);
        Assert.Equal("测量稳定", new BenchmarkResult(40, 20, duration).QualityText);
        Assert.Equal("系统负载高，结果仅供参考", new BenchmarkResult(40, 20, duration, 0.2).QualityText);
    }
}
