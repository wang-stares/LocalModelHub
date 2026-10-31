using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace LocalModelHub.Core.Services;

using LocalModelHub.Core.Models;

public sealed record DownloadProgress(long ReceivedBytes, long TotalBytes, double BytesPerSecond, TimeSpan? Eta);

public sealed record ModelDownloadProgress(
    double Percent,
    long ReceivedBytes,
    long TotalBytes,
    double BytesPerSecond,
    TimeSpan? Eta,
    string Status,
    int FileIndex,
    int FileCount);

public static class DownloadUrlBuilder
{
    public static string Build(ModelSource source, string filePath, AppSettings settings)
    {
        if (source.Provider.Equals("HuggingFace", StringComparison.OrdinalIgnoreCase))
        {
            var host = settings.UseHfMirror ? RemoteCatalogService.HuggingFaceMirrorHost : RemoteCatalogService.HuggingFaceHost;
            return BuildHuggingFace(source.Repo, filePath, host);
        }

        return BuildModelScope(source.Repo, filePath, source.Revision);
    }

    public static string Build(ModelSource source, ModelFile file, AppSettings settings)
    {
        if (!source.Provider.Equals("Ollama", StringComparison.OrdinalIgnoreCase))
        {
            return Build(source, file.Path, settings);
        }

        if (string.IsNullOrWhiteSpace(file.Digest))
        {
            throw new InvalidOperationException("Ollama 下载源缺少 blob 摘要");
        }

        return $"{RemoteCatalogService.OllamaRegistryHost}/v2/{source.Repo}/blobs/{file.Digest}";
    }

    public static string BuildHuggingFace(string repo, string filePath, string host)
    {
        var escapedPath = string.Join('/', filePath.Split('/').Select(Uri.EscapeDataString));
        return $"{host}/{repo}/resolve/main/{escapedPath}";
    }

    public static string BuildModelScope(string repo, string filePath, string revision)
    {
        var escapedPath = string.Join('/', filePath.Split('/').Select(Uri.EscapeDataString));
        return $"{RemoteCatalogService.ModelScopeHost}/api/v1/models/{repo}/repo" +
               $"?Revision={Uri.EscapeDataString(revision)}&FilePath={escapedPath}";
    }
}

public sealed class DownloadService
{
    private const int MaxAttempts = 3;
    private const long ResumeMargin = 256L * 1024 * 1024;

    /// <summary>等待响应头的上限；超时视为该源不可用，交给下载管理器静默换源。</summary>
    private static readonly TimeSpan ResponseHeaderTimeout = TimeSpan.FromSeconds(12);

    /// <summary>传输过程中长时间无数据视为断流，切源后续传。</summary>
    private static readonly TimeSpan ReadIdleTimeout = TimeSpan.FromSeconds(30);

    private static readonly JsonSerializerOptions MetaJson = new() { PropertyNameCaseInsensitive = true };

    public DownloadService(HttpMessageHandler? handler = null)
    {
        handler ??= new SocketsHttpHandler
        {
            AllowAutoRedirect = true,
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            ConnectTimeout = TimeSpan.FromSeconds(10)
        };

        Client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        Client.DefaultRequestHeaders.UserAgent.ParseAdd("LocalModelHub/1.3");
    }

    public HttpClient Client { get; }

    /// <summary>
    /// 下载文件；支持基于 .part + .part.meta 的断点续传。
    /// <paramref name="allowRetry"/> 为 false 时只请求一次，由调用方立即切换备用源。
    /// </summary>
    public async Task DownloadFileAsync(
        string url,
        string destinationPath,
        long expectedBytes,
        IProgress<DownloadProgress>? progress,
        CancellationToken cancellationToken,
        bool allowRetry = true)
    {
        var directory = Path.GetDirectoryName(destinationPath);
        if (directory is not null) Directory.CreateDirectory(directory);

        var partPath = destinationPath + ".part";
        var metaPath = partPath + ".meta";

        if (File.Exists(destinationPath))
        {
            var size = new FileInfo(destinationPath).Length;
            if (expectedBytes <= 0 || size == expectedBytes)
            {
                progress?.Report(new DownloadProgress(size, expectedBytes > 0 ? expectedBytes : size, 0, TimeSpan.Zero));
                return;
            }

            File.Delete(destinationPath);
        }

        PrepareResume(partPath, metaPath, url, expectedBytes);

        var attempts = allowRetry ? MaxAttempts : 1;
        Exception? lastError = null;
        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await DownloadOnceAsync(url, destinationPath, partPath, metaPath, expectedBytes, progress, cancellationToken);
                return;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (HttpRequestException ex) when (ex.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized)
            {
                throw;
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException)
            {
                lastError = ex;
                if (attempt < attempts)
                {
                    await Task.Delay(Backoff(attempt), cancellationToken);
                }
            }
        }

        throw new IOException($"下载失败：{lastError?.Message}", lastError);
    }

    /// <summary>预留空间检查：返回 true 表示可用空间不足。</summary>
    public static bool TryCheckFreeSpace(string directory, long requiredBytes, out long availableBytes)
    {
        availableBytes = long.MaxValue;
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(directory));
            if (string.IsNullOrEmpty(root)) return false;

            var drive = new DriveInfo(root);
            if (!drive.IsReady) return false;
            availableBytes = drive.AvailableFreeSpace;
            return availableBytes < requiredBytes + ResumeMargin;
        }
        catch
        {
            return false;
        }
    }

    private static void PrepareResume(string partPath, string metaPath, string url, long expectedBytes)
    {
        if (!File.Exists(partPath))
        {
            TryDelete(metaPath);
            return;
        }

        var valid = false;
        try
        {
            if (File.Exists(metaPath))
            {
                var meta = JsonSerializer.Deserialize<PartMeta>(File.ReadAllText(metaPath), MetaJson);
                // 不校验 URL：换源后仍可凭文件大小沿用断点，实现无感换源
                valid = meta is not null && meta.ExpectedBytes == expectedBytes;
            }
        }
        catch
        {
            valid = false;
        }

        if (!valid || new FileInfo(partPath).Length > (expectedBytes > 0 ? expectedBytes : long.MaxValue))
        {
            TryDelete(partPath);
            TryDelete(metaPath);
        }
    }

    private async Task DownloadOnceAsync(
        string url,
        string destinationPath,
        string partPath,
        string metaPath,
        long expectedBytes,
        IProgress<DownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        var existing = File.Exists(partPath) ? new FileInfo(partPath).Length : 0;

        if (expectedBytes > 0 && existing == expectedBytes)
        {
            Promote(partPath, metaPath, destinationPath);
            progress?.Report(new DownloadProgress(existing, expectedBytes, 0, TimeSpan.Zero));
            return;
        }

        if (expectedBytes > 0 && existing > expectedBytes)
        {
            TryDelete(partPath);
            existing = 0;
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (existing > 0) request.Headers.Range = new RangeHeaderValue(existing, null);

        using var headerCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        headerCts.CancelAfter(ResponseHeaderTimeout);

        HttpResponseMessage response;
        try
        {
            response = await Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, headerCts.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new HttpRequestException("连接下载源超时");
        }

        using (response)
        {
            if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable && existing > 0)
            {
                if (expectedBytes > 0 && existing == expectedBytes)
                {
                    Promote(partPath, metaPath, destinationPath);
                    progress?.Report(new DownloadProgress(existing, expectedBytes, 0, TimeSpan.Zero));
                    return;
                }

                TryDelete(partPath);
                TryDelete(metaPath);
                throw new InvalidDataException("续传校验失败，已删除断点并从零重新下载");
            }

            response.EnsureSuccessStatusCode();

            var resumed = response.StatusCode == HttpStatusCode.PartialContent && existing > 0;
            if (resumed)
            {
                var rangeStart = response.Content.Headers.ContentRange?.From;
                if (rangeStart is not null && rangeStart != existing)
                {
                    TryDelete(partPath);
                    TryDelete(metaPath);
                    throw new InvalidDataException("服务器返回的续传区间与本地断点不一致");
                }
            }
            else if (existing > 0)
            {
                existing = 0;
                TryDelete(partPath);
            }

            var contentLength = response.Content.Headers.ContentLength ?? 0;
            var total = expectedBytes > 0
                ? expectedBytes
                : response.Content.Headers.ContentRange?.Length ?? (contentLength + existing);

            try
            {
                File.WriteAllText(metaPath, JsonSerializer.Serialize(new PartMeta(url, expectedBytes)));
            }
            catch
            {
                // meta 仅用于校验续传，写入失败不阻断下载
            }

            await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using var target = new FileStream(
                partPath,
                resumed ? FileMode.Append : FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 1024 * 1024,
                useAsync: true);

            var buffer = new byte[128 * 1024];
            long received = existing;
            long lastReported = existing;
            var stopwatch = Stopwatch.StartNew();
            double speed = 0;

            while (true)
            {
                int read;
                using (var readCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                {
                    readCts.CancelAfter(ReadIdleTimeout);
                    try
                    {
                        read = await source.ReadAsync(buffer, readCts.Token);
                    }
                    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                    {
                        throw new HttpRequestException("下载连接中断");
                    }
                }

                if (read <= 0) break;
                await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                received += read;

                if (stopwatch.ElapsedMilliseconds >= 400)
                {
                    var instant = (received - lastReported) / stopwatch.Elapsed.TotalSeconds;
                    speed = speed <= 0 ? instant : speed * 0.7 + instant * 0.3;
                    lastReported = received;
                    stopwatch.Restart();
                    var eta = speed > 1 && total > received
                        ? TimeSpan.FromSeconds((total - received) / speed)
                        : (TimeSpan?)null;
                    progress?.Report(new DownloadProgress(received, total, speed, eta));
                }
            }

            await target.FlushAsync(cancellationToken);
            await target.DisposeAsync();

            if (expectedBytes > 0 && received != expectedBytes)
            {
                throw new IOException($"文件大小不匹配（{received}/{expectedBytes} 字节）");
            }

            Promote(partPath, metaPath, destinationPath);
            progress?.Report(new DownloadProgress(received, total > 0 ? total : received, 0, TimeSpan.Zero));
        }
    }

    private static void Promote(string partPath, string metaPath, string destinationPath)
    {
        File.Move(partPath, destinationPath, overwrite: true);
        TryDelete(metaPath);
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // ignore
        }
    }

    private static TimeSpan Backoff(int attempt) =>
        TimeSpan.FromSeconds(Math.Min(2 * Math.Pow(2, attempt - 1), 15));

    private sealed record PartMeta(string Url, long ExpectedBytes);
}
