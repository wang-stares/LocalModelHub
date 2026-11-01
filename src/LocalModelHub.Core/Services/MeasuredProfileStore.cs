using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LocalModelHub.Core.Models;

namespace LocalModelHub.Core.Services;

/// <summary>一次实测的 CPU 算力与内存带宽，带硬件指纹与采样时间。</summary>
public sealed record MeasuredProfile(
    string Fingerprint,
    double CpuGflops,
    double MemoryBandwidthGBps,
    double StdevRatio,
    DateTime MeasuredAt,
    int AppMajorVersion)
{
    /// <summary>不含应用版本的硬件指纹：用于区分「换了硬件」与「只是升级了应用」。</summary>
    public string HardwareFingerprint { get; init; } = "";
}

/// <summary>
/// 实测结果落盘：指纹只取稳定项（不含可用内存 / 当前占用 / 机器名），
/// 换设备、换硬件或大版本升级时自动失效，避免把上一台机器的结论套过来。
/// </summary>
public static class MeasuredProfileStore
{
    public const int ValidDays = 30;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    private static readonly object Sync = new();

    public static string FilePath => AppPaths.MeasuredProfileFile;

    /// <summary>应用主版本；0.x 版本用 minor（0.34 → 34），保证每次升级都能让实测口径失效。</summary>
    public static int AppMajorVersion => CurrentMajorVersion();

    /// <summary>稳定项指纹：CPU 型号 / 物理核数 / 总内存 / 显卡型号 / 可用显存 / 应用大版本。</summary>
    public static string Fingerprint(HardwareProfile hw, GpuCapability? gpu, int appMajorVersion = 0)
    {
        var major = appMajorVersion > 0 ? appMajorVersion : CurrentMajorVersion();
        var raw = $"{HardwareFingerprint(hw, gpu)}|{major}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw)))[..16];
    }

    /// <summary>只含硬件稳定项（不含可用内存、机器名与应用版本）。</summary>
    public static string HardwareFingerprint(HardwareProfile hw, GpuCapability? gpu)
    {
        var raw = string.Join('|',
            hw.Cpu.Name,
            hw.Cpu.PhysicalCores,
            (long)Math.Round(hw.TotalRamGb),
            gpu?.Gpu.Name ?? "none",
            gpu?.UsableVramBytes ?? 0L);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw)))[..16];
    }

    /// <summary>读取与指纹匹配、且未过期 / 未跨大版本的实测结果；不匹配返回 null。</summary>
    public static MeasuredProfile? TryLoad(string fingerprint, string? filePath = null, DateTime? now = null)
    {
        if (fingerprint.Length == 0) return null;
        lock (Sync)
        {
            var profile = Read(filePath);
            if (profile is null) return null;
            if (!profile.Fingerprint.Equals(fingerprint, StringComparison.Ordinal)) return null;

            var current = now ?? DateTime.UtcNow;
            if (current - profile.MeasuredAt > TimeSpan.FromDays(ValidDays)) return null;
            if (profile.AppMajorVersion != CurrentMajorVersion()) return null;
            return profile;
        }
    }

    /// <summary>读取档案原文（不校验指纹 / 时效），用于区分「换了硬件」与「档案过期」。</summary>
    public static MeasuredProfile? ReadLatest(string? filePath = null)
    {
        lock (Sync)
        {
            return Read(filePath);
        }
    }

    private static MeasuredProfile? Read(string? filePath)
    {
        try
        {
            var path = filePath ?? FilePath;
            if (!File.Exists(path)) return null;
            return JsonSerializer.Deserialize<MeasuredProfile>(File.ReadAllText(path), JsonOptions);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            System.Diagnostics.Debug.WriteLine($"[MeasuredProfileStore] 读取实测档案失败：{ex.Message}");
            return null;
        }
    }

    public static bool Save(MeasuredProfile profile, string? filePath = null)
    {
        lock (Sync)
        {
            try
            {
                var path = filePath ?? FilePath;
                var directory = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

                var temp = path + ".tmp";
                File.WriteAllText(temp, JsonSerializer.Serialize(profile, JsonOptions));
                File.Move(temp, path, overwrite: true);
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                System.Diagnostics.Debug.WriteLine($"[MeasuredProfileStore] 写入实测档案失败：{ex.Message}");
                return false;
            }
        }
    }

    private static int CurrentMajorVersion()
    {
        var version = typeof(MeasuredProfileStore).Assembly.GetName().Version;
        if (version is null) return 0;
        // 0.x 阶段 Major 恒为 0：用 Minor 作为「大版本」，否则任何升级都不会让档案失效。
        return version.Major > 0 ? version.Major : version.Minor;
    }
}
