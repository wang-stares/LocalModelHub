using System.Diagnostics;
using System.Text.Json;

namespace LocalModelHub.Core.Services;

using LocalModelHub.Core.Models;

public sealed record DownloadResult(string ModelId, string QuantLabel, string PrimaryPath, long TotalBytes);

public sealed class ModelDownloadManager
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly DownloadService _downloadService;
    private readonly SettingsService _settings;
    private readonly ModelLibraryService _library;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public ModelDownloadManager(DownloadService downloadService, SettingsService settings, ModelLibraryService library)
    {
        _downloadService = downloadService;
        _settings = settings;
        _library = library;
    }

    /// <summary>已有断点（.part）的总字节数，用于展示“继续下载”与空间检查。</summary>
    public long GetPartialBytes(ModelEntry model, ModelQuant quant)
    {
        var directory = Path.Combine(_settings.Settings.ModelsDirectory, model.Id, quant.Label);
        if (!Directory.Exists(directory)) return 0;

        long total = 0;
        foreach (var file in quant.Files)
        {
            var part = Path.Combine(directory, Path.GetFileName(file.Path) + ".part");
            try
            {
                if (File.Exists(part)) total += new FileInfo(part).Length;
            }
            catch
            {
                // 忽略无法读取的断点文件
            }
        }

        return total;
    }

    public async Task<DownloadResult> DownloadAsync(
        ModelEntry model,
        ModelQuant quant,
        IProgress<ModelDownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var settings = _settings.Settings;
            var targetDirectory = Path.Combine(settings.ModelsDirectory, model.Id, quant.Label);
            Directory.CreateDirectory(targetDirectory);

            var files = quant.Files.OrderBy(f => f.Path, StringComparer.OrdinalIgnoreCase).ToArray();
            var totalBytes = quant.TotalBytes > 0 ? quant.TotalBytes : files.Sum(f => f.Bytes);
            var partialBytes = GetPartialBytes(model, quant);
            long completedBytes = 0;
            var fileCount = files.Length;
            string? usedProvider = null;

            if (totalBytes - partialBytes > 0 &&
                DownloadService.TryCheckFreeSpace(targetDirectory, totalBytes - partialBytes, out var availableBytes))
            {
                throw new InvalidOperationException(
                    $"磁盘空间不足：还需 {Formatters.Bytes(totalBytes - partialBytes)}，" +
                    $"可用 {Formatters.Bytes(availableBytes)}。请清理空间或更换模型目录。");
            }

            if (partialBytes > 0)
            {
                progress?.Report(new ModelDownloadProgress(
                    totalBytes > 0 ? Math.Clamp(partialBytes * 100d / totalBytes, 0, 100) : 0,
                    partialBytes, totalBytes, 0, null,
                    $"检测到断点，将从 {Formatters.Bytes(partialBytes)} 处继续", 0, fileCount));
            }

            for (var index = 0; index < files.Length; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var file = files[index];
                var fileName = Path.GetFileName(file.Path);
                var destination = Path.Combine(targetDirectory, fileName);
                var indexCopy = index;
                var baseline = completedBytes;
                var candidates = DownloadSourcePlanner.Build(model, quant, file, settings);
                if (candidates.Count == 0)
                {
                    throw new InvalidOperationException("该模型没有可用的下载源");
                }

                usedProvider ??= candidates[0].Provider;

                var fileProgress = new Progress<DownloadProgress>(p =>
                {
                    var received = baseline + p.ReceivedBytes;
                    var percent = totalBytes > 0 ? Math.Clamp(received * 100d / totalBytes, 0, 100) : 0;
                    var status = fileCount > 1
                        ? $"正在下载 {indexCopy + 1}/{fileCount} · {fileName}"
                        : "正在下载";
                    progress?.Report(new ModelDownloadProgress(
                        percent, received, totalBytes, p.BytesPerSecond, p.Eta, status, indexCopy, fileCount));
                });

                Exception? lastError = null;
                var succeeded = false;

                foreach (var candidate in candidates)
                {
                    try
                    {
                        // 多源时每个源只试一次，失败立即静默切到下一源；单源才在内部重试
                        await _downloadService.DownloadFileAsync(
                            candidate.Url, destination, file.Bytes, fileProgress, cancellationToken,
                            allowRetry: candidates.Count == 1);

                        // 来源提供了 SHA-256 时必须比对：镜像被劫持或文件被替换时不能写入模型库。
                        if (!await FileIntegrity.VerifyFileAsync(destination, file.Digest, cancellationToken))
                        {
                            DeleteCorrupted(destination);
                            throw new InvalidDataException(
                                $"{fileName} 校验失败：SHA-256 与来源清单不一致");
                        }

                        succeeded = true;
                        usedProvider = candidate.Provider;
                        break;
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException)
                    {
                        lastError = ex;
                        if (candidates.Count > 1)
                        {
                            var kept = 0L;
                            try
                            {
                                var part = destination + ".part";
                                if (File.Exists(part)) kept = new FileInfo(part).Length;
                            }
                            catch
                            {
                                // 断点大小仅用于进度展示
                            }

                            var receivedSoFar = completedBytes + kept;
                            progress?.Report(new ModelDownloadProgress(
                                totalBytes > 0 ? Math.Clamp(receivedSoFar * 100d / totalBytes, 0, 100) : 0,
                                receivedSoFar, totalBytes, 0, null,
                                "正在切换下载线路，断点已保留…", indexCopy, fileCount));
                        }
                    }
                }

                if (!succeeded)
                {
                    if (lastError is InvalidDataException)
                    {
                        throw new InvalidDataException(
                            $"{fileName} 已下载但完整性校验失败，损坏文件已删除，请重试或更换下载源。", lastError);
                    }

                    throw new IOException($"下载 {fileName} 失败：{lastError?.Message}", lastError);
                }

                completedBytes = baseline + new FileInfo(destination).Length;
                progress?.Report(new ModelDownloadProgress(
                    totalBytes > 0 ? Math.Clamp(completedBytes * 100d / totalBytes, 0, 100) : 100,
                    completedBytes, totalBytes, 0, null,
                    $"已完成 {index + 1}/{fileCount}", index, fileCount));
            }

            var manifestPath = Path.Combine(targetDirectory, "manifest.json");
            var manifest = new
            {
                modelId = model.Id,
                displayName = model.DisplayName,
                quant = quant.Label,
                provider = usedProvider ?? "",
                imported = false,
                addedAt = DateTime.Now,
                totalBytes,
                files = files
                    .Select(f => new { relativePath = Path.GetFileName(f.Path), bytes = f.Bytes })
                    .ToArray()
            };
            await File.WriteAllTextAsync(manifestPath, JsonSerializer.Serialize(manifest, JsonOptions), cancellationToken);

            _library.NotifyChanged();
            var primary = Path.Combine(targetDirectory, Path.GetFileName(files[0].Path));
            return new DownloadResult(model.Id, quant.Label, primary, totalBytes);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>删除校验失败的最终文件；断点已由 DownloadService 合并，删除目标即可阻止其进入模型库。</summary>
    private static void DeleteCorrupted(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Debug.WriteLine($"删除校验失败的文件失败（{path}）：{ex.Message}");
        }
    }
}
