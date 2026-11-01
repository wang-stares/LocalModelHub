using LocalModelHub.Core.Models;
using LocalModelHub.Core.Services;
using Xunit;

namespace LocalModelHub.Core.Tests;

/// <summary>P1-11 / P1-19：实测档案指纹、过期与大版本失效，以及日志脱敏。</summary>
public sealed class ScoringSnapshotTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"lmh-profile-{Guid.NewGuid():N}");

    public ScoringSnapshotTests() => Directory.CreateDirectory(_root);

    private static HardwareProfile Machine(double ramGb, double availableGb, double vramGb) => new(
        "TEST-PC", "Windows 11",
        new CpuInfo("Test CPU", 8, 16, 4200, 16384),
        (long)(ramGb * Formatters.Gib), (long)(availableGb * Formatters.Gib),
        [new GpuInfo("NVIDIA GeForce RTX 4060", GpuInfo.VendorNvidia, (long)(vramGb * Formatters.Gib), 0)],
        (long)(512 * Formatters.Gib), (long)(200 * Formatters.Gib),
        true, true, DateTime.Now);

    [Fact]
    public void Fingerprint_ignores_available_memory_and_machine_name()
    {
        var first = Machine(32, 24, 8);
        var second = first with { AvailableRamBytes = (long)(3 * Formatters.Gib), MachineName = "OTHER-PC" };
        var gpu = GpuDatabase.GetBestGpu(first);

        Assert.Equal(
            MeasuredProfileStore.Fingerprint(first, gpu),
            MeasuredProfileStore.Fingerprint(second, gpu));
        Assert.Equal(
            MeasuredProfileStore.HardwareFingerprint(first, gpu),
            MeasuredProfileStore.HardwareFingerprint(second, gpu));
    }

    [Fact]
    public void Full_fingerprint_includes_app_version_but_hardware_fingerprint_does_not()
    {
        var hw = Machine(32, 24, 8);
        var gpu = GpuDatabase.GetBestGpu(hw);

        var current = MeasuredProfileStore.AppMajorVersion;
        Assert.NotEqual(
            MeasuredProfileStore.Fingerprint(hw, gpu, current),
            MeasuredProfileStore.Fingerprint(hw, gpu, current + 1));

        var moreRam = hw with { TotalRamBytes = (long)(64 * Formatters.Gib) };
        Assert.NotEqual(
            MeasuredProfileStore.HardwareFingerprint(hw, gpu),
            MeasuredProfileStore.HardwareFingerprint(moreRam, GpuDatabase.GetBestGpu(moreRam)));
    }

    [Fact]
    public void Fingerprint_changes_when_stable_hardware_changes()
    {
        var baseProfile = Machine(32, 24, 8);
        var moreRam = baseProfile with { TotalRamBytes = (long)(64 * Formatters.Gib) };
        var biggerVram = Machine(32, 24, 12);

        Assert.NotEqual(
            MeasuredProfileStore.Fingerprint(baseProfile, GpuDatabase.GetBestGpu(baseProfile)),
            MeasuredProfileStore.Fingerprint(moreRam, GpuDatabase.GetBestGpu(moreRam)));
        Assert.NotEqual(
            MeasuredProfileStore.Fingerprint(baseProfile, GpuDatabase.GetBestGpu(baseProfile)),
            MeasuredProfileStore.Fingerprint(biggerVram, GpuDatabase.GetBestGpu(biggerVram)));
    }

    [Fact]
    public void Measured_profile_roundtrips_and_expires()
    {
        var path = Path.Combine(_root, "measured-profile.json");
        var major = MeasuredProfileStore.AppMajorVersion;
        var measuredAt = new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc);
        const string fingerprint = "ABC123";

        Assert.True(MeasuredProfileStore.Save(
            new MeasuredProfile(fingerprint, 42.5, 20.7, 0.03, measuredAt, major), path));

        var loaded = MeasuredProfileStore.TryLoad(fingerprint, path, measuredAt.AddHours(2));
        Assert.NotNull(loaded);
        Assert.Equal(42.5, loaded!.CpuGflops);
        Assert.Equal(20.7, loaded.MemoryBandwidthGBps);

        // 换设备：指纹不匹配
        Assert.Null(MeasuredProfileStore.TryLoad("OTHER", path, measuredAt.AddHours(2)));
        // 超过 30 天
        Assert.Null(MeasuredProfileStore.TryLoad(fingerprint, path, measuredAt.AddDays(31)));
        // 跨大版本
        MeasuredProfileStore.Save(
            new MeasuredProfile(fingerprint, 42.5, 20.7, 0.03, measuredAt, major + 1), path);
        Assert.Null(MeasuredProfileStore.TryLoad(fingerprint, path, measuredAt.AddHours(2)));
    }

    [Fact]
    public void AppLog_masks_api_key_values()
    {
        Assert.Equal("--api-key ***", AppLog.Mask("--api-key sk-secret"));
        Assert.Equal("--api-key=***", AppLog.Mask("--api-key=\"sk-secret\""));
        Assert.Equal("--api-key *** --port 8090", AppLog.Mask("--api-key sk-secret --port 8090"));
        Assert.Equal("正常日志", AppLog.Mask("正常日志"));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch
        {
            // 清理失败不影响测试
        }
    }
}
