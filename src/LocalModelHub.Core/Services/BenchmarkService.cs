using System.Diagnostics;
using System.Numerics;

namespace LocalModelHub.Core.Services;

using LocalModelHub.Core.Models;

/// <summary>短时 CPU 向量浮点与内存带宽测试，约 2 秒完成。</summary>
public sealed class BenchmarkService
{
    private const int CpuMatrixSize = 256;
    private const int CpuRounds = 5;
    private const int MemoryRounds = 5;
    private const int RoundMilliseconds = 300;

    /// <param name="l3CacheKb">CPU 的三级缓存大小（KB）；未知时按 64 MB 估算，保证工作集大于缓存。</param>
    public BenchmarkResult Run(
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default,
        int l3CacheKb = 0)
    {
        var watch = Stopwatch.StartNew();

        progress?.Report("运行 CPU 向量基准…");
        var (gflops, cpuStdev) = MeasureCpu(cancellationToken);

        progress?.Report("测量内存带宽…");
        var (bandwidth, memoryStdev) = MeasureMemory(cancellationToken, l3CacheKb);

        watch.Stop();
        return new BenchmarkResult(
            Math.Round(gflops, 1),
            Math.Round(bandwidth, 1),
            watch.Elapsed,
            Math.Round(Math.Max(cpuStdev, memoryStdev), 3));
    }

    /// <summary>
    /// 用 <see cref="Vector{T}"/> 写向量化矩阵乘法。原实现是朴素标量三重循环，
    /// 测到的是标量循环速度，比现代 CPU 的 AVX2/AVX512 实际算力低 5–10 倍。
    /// </summary>
    private static (double Gflops, double StdevRatio) MeasureCpu(CancellationToken ct)
    {
        const int n = CpuMatrixSize;
        var a = new float[n * n];
        var b = new float[n * n];
        var c = new float[n * n];
        var rnd = new Random(42);
        for (var i = 0; i < a.Length; i++)
        {
            a[i] = (float)rnd.NextDouble();
            b[i] = (float)rnd.NextDouble();
        }

        Multiply(a, b, c, n); // 预热 + JIT
        Array.Clear(c);

        var samples = new List<double>(CpuRounds);
        for (var round = 0; round < CpuRounds && !ct.IsCancellationRequested; round++)
        {
            var watch = Stopwatch.StartNew();
            var iterations = 0;
            while (watch.ElapsedMilliseconds < RoundMilliseconds)
            {
                Multiply(a, b, c, n);
                iterations++;
                if ((iterations & 15) == 0) Array.Clear(c);
                if (ct.IsCancellationRequested) break;
            }

            watch.Stop();
            if (iterations == 0 || watch.Elapsed.TotalSeconds <= 0) continue;
            samples.Add(2.0 * n * n * n * iterations / watch.Elapsed.TotalSeconds / 1e9);
        }

        return Summarize(samples);
    }

    private static void Multiply(float[] a, float[] b, float[] c, int n)
    {
        var width = Vector<float>.Count;
        for (var i = 0; i < n; i++)
        {
            var row = i * n;
            for (var k = 0; k < n; k++)
            {
                var aik = new Vector<float>(a[row + k]);
                var brow = k * n;
                var j = 0;
                for (; j <= n - width; j += width)
                {
                    var cv = new Vector<float>(c, row + j);
                    var bv = new Vector<float>(b, brow + j);
                    (cv + aik * bv).CopyTo(c, row + j);
                }

                for (; j < n; j++) c[row + j] += a[row + k] * b[brow + j];
            }
        }
    }

    private static (double Gbps, double StdevRatio) MeasureMemory(CancellationToken ct, int l3CacheKb)
    {
        // 工作集必须 ≥ 4 × L3：8–16 MB 的数组会整块落在 16–36 MB 的 L3 里，
        // 测到的是缓存带宽（约 42 GB/s）而不是内存带宽（约 20 GB/s）。
        var l3Mb = l3CacheKb > 0 ? Math.Max(8, l3CacheKb / 1024) : 64;
        var mb = Math.Clamp(l3Mb * 4, 128, 512);
        var size = mb * 1024 * 1024;

        var source = new byte[size];
        var target = new byte[size];
        new Random(1).NextBytes(source);

        for (var i = 0; i < 3; i++) Buffer.BlockCopy(source, 0, target, 0, size); // 预热

        var samples = new List<double>(MemoryRounds);
        for (var round = 0; round < MemoryRounds && !ct.IsCancellationRequested; round++)
        {
            var watch = Stopwatch.StartNew();
            var copies = 0;
            while (watch.ElapsedMilliseconds < RoundMilliseconds)
            {
                Buffer.BlockCopy(source, 0, target, 0, size);
                copies++;
                if (ct.IsCancellationRequested) break;
            }

            watch.Stop();
            if (copies == 0 || watch.Elapsed.TotalSeconds <= 0) continue;
            // 读 + 写 = 2 * size
            samples.Add(2.0 * size * copies / watch.Elapsed.TotalSeconds / 1e9);
        }

        return Summarize(samples);
    }

    /// <summary>多轮取中位数，并返回标准差 / 均值作为结果可信度。</summary>
    private static (double Median, double StdevRatio) Summarize(List<double> samples)
    {
        if (samples.Count == 0) return (0, 0);

        samples.Sort();
        var median = samples[samples.Count / 2];
        var mean = samples.Average();
        var variance = samples.Sum(x => (x - mean) * (x - mean)) / samples.Count;
        var stdev = Math.Sqrt(variance);
        return (median, mean <= 0 ? 0 : stdev / mean);
    }
}
