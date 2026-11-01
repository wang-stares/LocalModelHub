namespace LocalModelHub.Core.Models;

public sealed record CpuInfo(
    string Name,
    int PhysicalCores,
    int LogicalCores,
    int MaxClockMhz,
    int L3CacheKb)
{
    public static readonly CpuInfo Unknown = new("未知处理器", 0, 1, 0, 0);
}

public sealed record GpuInfo(
    string Name,
    int VendorId,
    long DedicatedVideoMemoryBytes,
    long SharedSystemMemoryBytes,
    bool VramEstimated = false)
{
    public const int VendorNvidia = 0x10DE;
    public const int VendorAmd = 0x1002;
    public const int VendorIntel = 0x8086;

    public string Vendor => VendorId switch
    {
        VendorNvidia => "NVIDIA",
        VendorAmd => "AMD",
        VendorIntel => "Intel",
        _ => Name.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase) ? "NVIDIA"
            : Name.Contains("AMD", StringComparison.OrdinalIgnoreCase) || Name.Contains("Radeon", StringComparison.OrdinalIgnoreCase) ? "AMD"
            : Name.Contains("Intel", StringComparison.OrdinalIgnoreCase) ? "Intel"
            : "Other"
    };

    public double DedicatedVramGb => DedicatedVideoMemoryBytes / Formatters.Gib;
}

public sealed record HardwareProfile(
    string MachineName,
    string OsDescription,
    CpuInfo Cpu,
    long TotalRamBytes,
    long AvailableRamBytes,
    IReadOnlyList<GpuInfo> Gpus,
    long DiskTotalBytes,
    long DiskFreeBytes,
    bool HasAvx2,
    bool HasAvx512,
    DateTime DetectedAt)
{
    public double TotalRamGb => TotalRamBytes / Formatters.Gib;
    public double AvailableRamGb => AvailableRamBytes / Formatters.Gib;

    public static HardwareProfile Empty { get; } = new(
        Environment.MachineName,
        "未知系统",
        CpuInfo.Unknown,
        0,
        0,
        Array.Empty<GpuInfo>(),
        0,
        0,
        false,
        false,
        DateTime.MinValue);
}

public sealed record BenchmarkResult(
    double CpuGflops,
    double MemoryBandwidthGBps,
    TimeSpan Duration,
    double StdevRatio = 0)
{
    /// <summary>标准差 / 均值超过 15%：测量期间系统负载波动大，结果仅供参考。</summary>
    public bool LowConfidence => StdevRatio > 0.15;

    public string QualityText => LowConfidence ? "系统负载高，结果仅供参考" : "测量稳定";
}

public sealed record GpuCapability(
    GpuInfo Gpu,
    double BandwidthGBps,
    bool IsIntegrated,
    long UsableVramBytes)
{
    public double UsableVramGb => UsableVramBytes / Formatters.Gib;
}

public sealed record PerformanceScore(
    int Total,
    double CpuPoints,
    double MemoryPoints,
    double GpuPoints,
    double StoragePoints,
    string Tier,
    string Summary,
    GpuCapability? BestGpu)
{
    /// <summary>四项能力分，带绝对指标、含义与来源（P1-20）。</summary>
    public IReadOnlyList<ScoreComponent> Components { get; init; } = [];

    /// <summary>能力画像；未检测时为空。</summary>
    public CapabilityProfile? Capability { get; init; }

    /// <summary>0–1：有实测基准约 0.9，纯查表 / 估算约 0.5。</summary>
    public double Confidence { get; init; } = 0.5;

    /// <summary>评分对应的采样时间；从缓存加载时是上次实测时间。</summary>
    public DateTime ScoredAt { get; init; }

    /// <summary>评分口径版本：常数或算法变化时递增，用于让缓存失效。</summary>
    public string CalibrationVersion { get; init; } = "v2.1";

    public string ConfidenceText => Confidence >= 0.8
        ? "实测口径"
        : Confidence >= 0.55 ? "部分实测" : "估算口径";
}

public static class Formatters
{
    public const double Gib = 1024d * 1024d * 1024d;

    public static string Bytes(long bytes)
    {
        if (bytes <= 0) return "0 B";
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var value = (double)bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit <= 1 ? $"{value:0} {units[unit]}" : $"{value:0.0} {units[unit]}";
    }

    public static string Gb(double bytes) => $"{bytes / Gib:0.#} GB";
}
