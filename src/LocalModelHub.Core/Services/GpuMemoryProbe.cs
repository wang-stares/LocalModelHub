using System.Diagnostics;
using System.Globalization;

namespace LocalModelHub.Core.Services;

/// <summary>一张显卡的显存占用采样（nvidia-smi 的 MiB 已换算成 GB）。</summary>
public sealed record GpuMemorySample(string Name, double UsedGb);

/// <summary>
/// 读取 NVIDIA 驱动报告的「已用显存」，用于评估前扣掉浏览器 / 播放器等程序占用的部分。
/// 未安装 nvidia-smi（AMD / Intel / 无独显）时返回 0，不改变原有估算；结果缓存一段时间，避免频繁启动进程。
/// </summary>
public sealed class GpuMemoryProbe
{
    /// <summary>驱动与桌面默认占用的约 0.8 GB 已在 GpuDatabase 的可用显存里预留，这里不重复扣。</summary>
    public const double SystemBaselineGb = 0.8;

    private static readonly TimeSpan DefaultTtl = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromMilliseconds(1500);

    private readonly TimeSpan _ttl;
    private readonly TimeSpan _timeout;
    private readonly string _executable;
    private readonly object _gate = new();

    private IReadOnlyList<GpuMemorySample> _samples = [];
    private bool _sampled;
    private DateTime _sampledAt;

    public GpuMemoryProbe(TimeSpan? ttl = null, string? executable = null, TimeSpan? timeout = null)
    {
        _ttl = ttl ?? DefaultTtl;
        _executable = executable ?? "nvidia-smi";
        _timeout = timeout ?? DefaultTimeout;
    }

    /// <summary>指定显卡的已用显存（GB）；名称匹配不到时取所有卡的最大值，探测失败时返回 0。</summary>
    public double UsedVramGb(string? gpuName = null)
    {
        lock (_gate)
        {
            if (!_sampled || DateTime.UtcNow - _sampledAt > _ttl)
            {
                _samples = ReadSamples();
                _sampled = true;
                _sampledAt = DateTime.UtcNow;
            }

            return SelectUsedGb(_samples, gpuName);
        }
    }

    /// <summary>扣掉系统基线后的额外占用，直接作为评估时的显存预留。</summary>
    public double ReservedVramGb(string? gpuName = null) => ReservedFrom(UsedVramGb(gpuName));

    /// <summary>让下一次读取重新采样（下载前刷新预算时使用）。</summary>
    public void Invalidate()
    {
        lock (_gate)
        {
            _sampled = false;
            _samples = [];
        }
    }

    private IReadOnlyList<GpuMemorySample> ReadSamples()
    {
        try
        {
            var info = new ProcessStartInfo(
                _executable,
                "--query-gpu=name,memory.used --format=csv,noheader,nounits")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(info);
            if (process is null) return [];

            var output = process.StandardOutput.ReadToEndAsync();
            if (!process.WaitForExit((int)_timeout.TotalMilliseconds))
            {
                process.Kill(true);
                return [];
            }

            return Parse(output.GetAwaiter().GetResult());
        }
        catch
        {
            // 驱动不可用、进程启动失败或权限受限时不做判断
            return [];
        }
    }

    /// <summary>解析 `nvidia-smi --query-gpu=name,memory.used` 的 CSV 输出；`N/A` 等无法解析的行直接跳过。</summary>
    public static IReadOnlyList<GpuMemorySample> Parse(string? output)
    {
        if (string.IsNullOrWhiteSpace(output)) return [];

        var samples = new List<GpuMemorySample>();
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;

            // 显卡名里可能带逗号，从最后一个逗号切开取值。
            var comma = line.LastIndexOf(',');
            if (comma <= 0) continue;

            var name = line[..comma].Trim();
            var value = line[(comma + 1)..].Trim();
            if (name.Length == 0) continue;
            if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var mib) || mib < 0) continue;

            samples.Add(new GpuMemorySample(name, mib / 1024d));
        }

        return samples;
    }

    /// <summary>按名称匹配同一张卡；匹配不到时取最高占用，宁可保守也不漏掉被占用的显存。</summary>
    public static double SelectUsedGb(IReadOnlyList<GpuMemorySample> samples, string? gpuName)
    {
        if (samples.Count == 0) return 0;

        if (!string.IsNullOrWhiteSpace(gpuName))
        {
            var name = gpuName.Trim();
            var exact = samples.FirstOrDefault(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (exact is not null) return exact.UsedGb;

            var partial = samples.FirstOrDefault(s =>
                s.Name.Contains(name, StringComparison.OrdinalIgnoreCase) ||
                name.Contains(s.Name, StringComparison.OrdinalIgnoreCase));
            if (partial is not null) return partial.UsedGb;
        }

        return samples.Max(s => s.UsedGb);
    }

    /// <summary>系统基线内的占用属于驱动/桌面常态，不作为额外预留。</summary>
    public static double ReservedFrom(double usedGb) =>
        Math.Max(0, usedGb - SystemBaselineGb);
}
