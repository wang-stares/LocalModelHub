using System.Management;
using System.Runtime.InteropServices;

namespace LocalModelHub.Core.Services;

using LocalModelHub.Core.Models;
using Microsoft.Win32;

/// <summary>Windows 硬件探测：WMI + 注册表 + kernel32，不依赖第三方组件。</summary>
public sealed class HardwareDetector
{
    public HardwareProfile Detect(IProgress<string>? progress = null, string? storagePath = null)
    {
        if (!OperatingSystem.IsWindows())
        {
            return DetectUnix();
        }

        progress?.Report("读取处理器信息…");
        var cpu = ReadCpu();

        progress?.Report("读取内存信息…");
        var memory = WindowsNative.GetMemory();

        progress?.Report("识别显卡…");
        var gpus = ReadGpus();

        progress?.Report("检查磁盘空间…");
        var disk = ReadDisk(storagePath);

        return new HardwareProfile(
            Environment.MachineName,
            RuntimeInformation.OSDescription,
            cpu,
            memory.Total,
            memory.Available,
            gpus,
            disk.Total,
            disk.Free,
            WindowsNative.HasAvx2,
            WindowsNative.HasAvx512,
            DateTime.Now);
    }

    private static CpuInfo ReadCpu()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT Name, NumberOfCores, NumberOfLogicalProcessors, MaxClockSpeed, L3CacheSize FROM Win32_Processor");
            foreach (ManagementObject item in searcher.Get())
            {
                using (item)
                {
                    var name = (item["Name"] as string)?.Trim() ?? "未知处理器";
                    var physical = Convert.ToInt32(item["NumberOfCores"] ?? 0);
                    var logical = Convert.ToInt32(item["NumberOfLogicalProcessors"] ?? Environment.ProcessorCount);
                    var clock = Convert.ToInt32(item["MaxClockSpeed"] ?? 0);
                    var cache = Convert.ToInt32(item["L3CacheSize"] ?? 0);
                    if (physical <= 0) physical = Math.Max(1, logical / 2);
                    return new CpuInfo(name, physical, logical, clock, cache);
                }
            }
        }
        catch
        {
            // 回退到 Environment
        }

        return new CpuInfo("未知处理器", Math.Max(1, Environment.ProcessorCount / 2), Environment.ProcessorCount, 0, 0);
    }

    private static List<GpuInfo> ReadGpus()
    {
        var gpus = ReadGpusFromRegistry();
        if (gpus.Count == 0) gpus = ReadGpusFromWmi();

        // 过滤 Microsoft Basic Render Driver 等软件适配器
        return gpus
            .Where(g => g.Name.Length > 0 &&
                        !g.Name.Contains("Microsoft Basic", StringComparison.OrdinalIgnoreCase) &&
                        !g.Name.Contains("Remote Display", StringComparison.OrdinalIgnoreCase))
            .GroupBy(g => g.Name)
            .Select(g => g.First())
            .ToList();
    }

    private static List<GpuInfo> ReadGpusFromRegistry()
    {
        var result = new List<GpuInfo>();
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var classKey = baseKey.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}");
            if (classKey is null) return result;

            foreach (var subName in classKey.GetSubKeyNames())
            {
                if (subName.Length != 4 || !subName.All(char.IsDigit)) continue;
                using var sub = classKey.OpenSubKey(subName);
                if (sub is null) continue;

                var name = sub.GetValue("DriverDesc") as string;
                if (string.IsNullOrWhiteSpace(name)) continue;

                var raw = sub.GetValue("HardwareInformation.qwMemorySize");
                var vram = raw switch
                {
                    long l => l,
                    int i => (long)(uint)i,
                    byte[] bytes when bytes.Length >= 8 => BitConverter.ToInt64(bytes, 0),
                    _ => 0L
                };

                if (vram <= 0) continue;
                result.Add(new GpuInfo(name.Trim(), InferVendorId(name), vram, 0));
            }
        }
        catch
        {
            // 注册表不可读时忽略
        }

        return result;
    }

    private static List<GpuInfo> ReadGpusFromWmi()
    {
        var result = new List<GpuInfo>();
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT Name, AdapterRAM, PNPDeviceID FROM Win32_VideoController");
            foreach (ManagementObject item in searcher.Get())
            {
                using (item)
                {
                    var name = (item["Name"] as string)?.Trim();
                    if (string.IsNullOrWhiteSpace(name)) continue;
                    var ram = Convert.ToInt64(item["AdapterRAM"] ?? 0L);
                    var pnp = item["PNPDeviceID"] as string ?? "";
                    var vendor = pnp.Contains("VEN_10DE") ? GpuInfo.VendorNvidia
                        : pnp.Contains("VEN_1002") ? GpuInfo.VendorAmd
                        : pnp.Contains("VEN_8086") ? GpuInfo.VendorIntel
                        : InferVendorId(name);
                    result.Add(new GpuInfo(name, vendor, ram, 0));
                }
            }
        }
        catch
        {
            // 忽略
        }

        return result;
    }

    private static int InferVendorId(string name)
    {
        if (name.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase) || name.Contains("GeForce", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("RTX", StringComparison.OrdinalIgnoreCase) || name.Contains("GTX", StringComparison.OrdinalIgnoreCase))
            return GpuInfo.VendorNvidia;
        if (name.Contains("AMD", StringComparison.OrdinalIgnoreCase) || name.Contains("Radeon", StringComparison.OrdinalIgnoreCase))
            return GpuInfo.VendorAmd;
        if (name.Contains("Intel", StringComparison.OrdinalIgnoreCase) || name.Contains("Arc", StringComparison.OrdinalIgnoreCase))
            return GpuInfo.VendorIntel;
        return 0;
    }

    private static (long Total, long Free) ReadDisk(string? storagePath)
    {
        try
        {
            var root = storagePath is not null && storagePath.Length > 0
                ? Path.GetPathRoot(Path.GetFullPath(storagePath))
                : Path.GetPathRoot(Environment.SystemDirectory);
            if (string.IsNullOrEmpty(root))
            {
                root = Path.GetPathRoot(AppPaths.ModelsDirectory);
            }

            var drive = new DriveInfo(root!);
            return (drive.TotalSize, drive.AvailableFreeSpace);
        }
        catch
        {
            return (0, 0);
        }
    }

    /// <summary>
    /// 只读取当前物理内存总量与可用量（不触发 WMI / 显卡枚举），供下载前刷新预算使用。
    /// </summary>
    public static (long Total, long Available) ReadMemoryNow()
    {
        if (OperatingSystem.IsWindows()) return WindowsNative.GetMemory();
        return ReadUnixMemory();
    }

    private static (long Total, long Available) ReadUnixMemory()
    {
        long total = 0, available = 0;
        try
        {
            foreach (var line in File.ReadLines("/proc/meminfo"))
            {
                var parts = line.Split(':', 2, StringSplitOptions.TrimEntries);
                if (parts.Length != 2) continue;
                var kbText = parts[1].Replace("kB", "", StringComparison.OrdinalIgnoreCase).Trim();
                if (!long.TryParse(kbText, out var kb)) continue;
                if (parts[0] == "MemTotal") total = kb * 1024;
                else if (parts[0] == "MemAvailable") available = kb * 1024;
            }
        }
        catch
        {
            // ignore
        }

        return (total, available);
    }

    private static HardwareProfile DetectUnix()
    {
        var cpuName = "Unknown CPU";
        var physical = 0;
        try
        {
            var cpuInfo = File.ReadLines("/proc/cpuinfo").ToList();
            var model = cpuInfo.FirstOrDefault(l => l.StartsWith("model name", StringComparison.OrdinalIgnoreCase));
            if (model is not null) cpuName = model[(model.IndexOf(':') + 1)..].Trim();
            physical = cpuInfo.Count(l => l.StartsWith("physical id", StringComparison.OrdinalIgnoreCase));
            var cores = cpuInfo.Count(l => l.StartsWith("processor", StringComparison.OrdinalIgnoreCase));
            if (physical <= 0) physical = Math.Max(1, cores / 2);
        }
        catch
        {
            // ignore
        }

        var (totalRam, availRam) = ReadUnixMemory();

        var disk = ReadDisk(null);
        return new HardwareProfile(
            Environment.MachineName,
            RuntimeInformation.OSDescription,
            new CpuInfo(cpuName, Math.Max(1, physical), Environment.ProcessorCount, 0, 0),
            totalRam,
            availRam,
            Array.Empty<GpuInfo>(),
            disk.Total,
            disk.Free,
            true,
            false,
            DateTime.Now);
    }
}
