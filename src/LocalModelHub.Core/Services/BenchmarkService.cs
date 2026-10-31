using System.Diagnostics;

namespace LocalModelHub.Core.Services;

using LocalModelHub.Core.Models;

/// <summary>短时 CPU 矩阵运算与内存带宽测试，约 1 秒完成。</summary>
public sealed class BenchmarkService
{
    public BenchmarkResult Run(IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        var watch = Stopwatch.StartNew();

        progress?.Report("运行 CPU 浮点基准…");
        var gflops = MeasureCpu(cancellationToken);

        progress?.Report("测量内存带宽…");
        var bandwidth = MeasureMemory(cancellationToken);

        watch.Stop();
        return new BenchmarkResult(Math.Round(gflops, 1), Math.Round(bandwidth, 1), watch.Elapsed);
    }

    private static double MeasureCpu(CancellationToken ct)
    {
        const int n = 256;
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

        var watch = Stopwatch.StartNew();
        var iterations = 0;
        while (watch.ElapsedMilliseconds < 350 && iterations < 2000)
        {
            Multiply(a, b, c, n);
            iterations++;
            if ((iterations & 15) == 0) Array.Clear(c);
            if (ct.IsCancellationRequested) break;
        }

        watch.Stop();
        if (iterations == 0 || watch.Elapsed.TotalSeconds <= 0) return 0;
        return 2.0 * n * n * n * iterations / watch.Elapsed.TotalSeconds / 1e9;
    }

    private static void Multiply(float[] a, float[] b, float[] c, int n)
    {
        for (var i = 0; i < n; i++)
        {
            var row = i * n;
            for (var k = 0; k < n; k++)
            {
                var aik = a[row + k];
                var brow = k * n;
                for (var j = 0; j < n; j++)
                {
                    c[row + j] += aik * b[brow + j];
                }
            }
        }
    }

    private static double MeasureMemory(CancellationToken ct)
    {
        const int size = 8 * 1024 * 1024; // 32 MB 数组
        var source = new byte[size];
        var target = new byte[size];
        new Random(1).NextBytes(source);

        Buffer.BlockCopy(source, 0, target, 0, size); // 预热

        var watch = Stopwatch.StartNew();
        var copies = 0;
        while (watch.ElapsedMilliseconds < 250 && copies < 20)
        {
            Buffer.BlockCopy(source, 0, target, 0, size);
            copies++;
            if (ct.IsCancellationRequested) break;
        }

        watch.Stop();
        if (copies == 0 || watch.Elapsed.TotalSeconds <= 0) return 0;
        // 读 + 写 = 2 * size
        return 2.0 * size * copies / watch.Elapsed.TotalSeconds / 1e9;
    }
}
