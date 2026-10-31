namespace LocalModelHub.Core.Services;

using System.Text.RegularExpressions;
using LocalModelHub.Core.Models;

/// <summary>已知 GPU 的显存带宽近似值，用于估算推理速度与性能评分。</summary>
public static class GpuDatabase
{
    private static readonly (string Pattern, double BandwidthGBps)[] Table =
    [
        (@"RTX\s*5090", 1792),
        (@"RTX\s*5080", 960),
        (@"RTX\s*5070\s*Ti", 896),
        (@"RTX\s*5070", 672),
        (@"RTX\s*5060\s*Ti", 448),
        (@"RTX\s*5060", 448),
        (@"RTX\s*4090", 1008),
        (@"RTX\s*4080", 717),
        (@"RTX\s*4070\s*Ti", 672),
        (@"RTX\s*4070", 504),
        (@"RTX\s*4060\s*Ti", 288),
        (@"RTX\s*4060", 272),
        (@"RTX\s*3090", 936),
        (@"RTX\s*3080", 760),
        (@"RTX\s*3070", 448),
        (@"RTX\s*3060", 360),
        (@"RTX\s*3050", 224),
        (@"RTX\s*2080", 448),
        (@"RTX\s*2070", 448),
        (@"RTX\s*2060", 336),
        (@"GTX\s*1660", 288),
        (@"GTX\s*1650", 128),
        (@"GTX\s*1080", 352),
        (@"GTX\s*1070", 256),
        (@"GTX\s*1060", 192),
        (@"RX\s*9070\s*XT", 645),
        (@"RX\s*9070", 645),
        (@"RX\s*9060\s*XT", 322),
        (@"RX\s*7900\s*XTX", 960),
        (@"RX\s*7900\s*XT", 800),
        (@"RX\s*7800\s*XT", 624),
        (@"RX\s*7700\s*XT", 432),
        (@"RX\s*7600", 288),
        (@"RX\s*6950\s*XT", 576),
        (@"RX\s*6900\s*XT", 512),
        (@"RX\s*6800\s*XT", 512),
        (@"RX\s*6800", 512),
        (@"RX\s*6700\s*XT", 384),
        (@"RX\s*6650\s*XT", 224),
        (@"RX\s*6600", 224),
        (@"RX\s*5700\s*XT", 448),
        (@"Arc\s*B580", 456),
        (@"Arc\s*B570", 380),
        (@"Arc\s*A770", 560),
        (@"Arc\s*A750", 512),
        (@"Arc\s*A380", 186)
    ];

    public static double EstimateBandwidthGBps(GpuInfo gpu, bool isIntegrated)
    {
        var name = gpu.Name;
        foreach (var (pattern, bandwidth) in Table)
        {
            if (Regex.IsMatch(name, pattern, RegexOptions.IgnoreCase))
            {
                return name.Contains("Laptop", StringComparison.OrdinalIgnoreCase) ? bandwidth * 0.78 : bandwidth;
            }
        }

        if (isIntegrated)
        {
            return name.Contains("Iris", StringComparison.OrdinalIgnoreCase) ? 68 : 45;
        }

        // 兜底：按显存规模粗估（越大的卡通常带宽越高）
        var vram = gpu.DedicatedVramGb;
        return Math.Clamp(60 + vram * 32, 60, 1200);
    }

    /// <summary>未知显存容量时用于估算的保守值：按 4 GB 独显计，宁可少卸载也不要把独显当成核显。</summary>
    public const long UnknownDiscreteVramBytes = 4L * 1024 * 1024 * 1024;

    public static bool IsIntegrated(GpuInfo gpu)
    {
        // WMI 的 AdapterRAM 被 uint32 截断、nvidia-smi / 注册表也拿不到容量时，
        // 0 显存并不代表核显；此时只能按型号线索判断，否则独显会被整卡忽略。
        if (gpu.VramEstimated && gpu.DedicatedVideoMemoryBytes == 0)
        {
            return LooksIntegrated(gpu);
        }

        return gpu.DedicatedVideoMemoryBytes < 512L * 1024 * 1024 ||
            (LooksIntegrated(gpu) && gpu.DedicatedVramGb < 2);
    }

    private static bool LooksIntegrated(GpuInfo gpu)
    {
        var name = gpu.Name;
        if (name.Contains("Arc", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("RX", StringComparison.OrdinalIgnoreCase))
        {
            return false; // Intel Arc / AMD Radeon RX 均为独显
        }

        return gpu.Vendor == "Intel"
            || name.Contains("UHD", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Iris", StringComparison.OrdinalIgnoreCase)
            || name.Contains("HD Graphics", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Radeon", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Vega", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 估算 FP16 算力（TFLOPS），用于首字延迟（prefill）。未收录型号按显存档位保守兜底，
    /// 宁可低估也不要把老卡标成「首字秒出」。
    /// </summary>
    public static double EstimateTflops(GpuInfo gpu, bool integrated)
    {
        if (integrated) return 0;

        var name = gpu.Name.ToLowerInvariant();
        var table = new (string Key, double Tflops)[]
        {
            ("4090", 165), ("4080", 97), ("4070 ti", 80), ("4070", 58), ("4060 ti", 44), ("4060", 30),
            ("3090", 71), ("3080", 60), ("3070", 40), ("3060", 25), ("2080 ti", 36), ("2080", 29),
            ("1080 ti", 18), ("1080", 17), ("h100", 990), ("a100", 312), ("v100", 112), ("a6000", 155),
            ("l40", 181), ("7900 xtx", 123), ("7900 xt", 103), ("7800 xt", 75), ("7700 xt", 55),
            ("9070 xt", 97), ("arc a770", 39), ("arc a750", 34), ("arc b580", 46)
        };
        foreach (var (key, tflops) in table)
        {
            if (name.Contains(key, StringComparison.Ordinal)) return tflops;
        }

        return Math.Clamp(4 + gpu.DedicatedVramGb * 5, 6, 200);
    }

    public static GpuCapability? GetBestGpu(HardwareProfile hw)
    {
        GpuCapability? best = null;
        foreach (var gpu in hw.Gpus)
        {
            var integrated = IsIntegrated(gpu);
            var bandwidth = EstimateBandwidthGBps(gpu, integrated);
            var vram = gpu.DedicatedVideoMemoryBytes;
            if (!integrated && vram <= 0 && gpu.VramEstimated) vram = UnknownDiscreteVramBytes;
            var usable = integrated ? 0L : Math.Max(0L, vram - (long)(0.8 * Formatters.Gib));
            var capability = new GpuCapability(gpu, bandwidth, integrated, usable);
            if (best is null ||
                usable > best.UsableVramBytes ||
                (usable == best.UsableVramBytes && bandwidth > best.BandwidthGBps))
            {
                best = capability;
            }
        }

        return best;
    }
}
