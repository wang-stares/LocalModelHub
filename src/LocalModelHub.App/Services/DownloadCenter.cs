using CommunityToolkit.Mvvm.ComponentModel;
using LocalModelHub.Core.Models;
using LocalModelHub.Core.Services;

namespace LocalModelHub.App.Services;

public sealed class DownloadState : ObservableObject
{
    private bool _isDownloading;
    private bool _isPaused;
    private double _progress;
    private string _status = "";
    private string _speedText = "";
    private string _etaText = "";
    private string? _error;
    private bool _isComplete;

    public bool IsDownloading
    {
        get => _isDownloading;
        set => SetProperty(ref _isDownloading, value);
    }

    public bool IsPaused
    {
        get => _isPaused;
        set => SetProperty(ref _isPaused, value);
    }

    public double Progress
    {
        get => _progress;
        set => SetProperty(ref _progress, value);
    }

    public string Status
    {
        get => _status;
        set => SetProperty(ref _status, value);
    }

    public string SpeedText
    {
        get => _speedText;
        set => SetProperty(ref _speedText, value);
    }

    public string EtaText
    {
        get => _etaText;
        set => SetProperty(ref _etaText, value);
    }

    public string? Error
    {
        get => _error;
        set
        {
            if (SetProperty(ref _error, value)) OnPropertyChanged(nameof(HasError));
        }
    }

    public bool HasError => !string.IsNullOrEmpty(Error);

    public bool IsComplete
    {
        get => _isComplete;
        set => SetProperty(ref _isComplete, value);
    }

    public void Reset()
    {
        Error = null;
        Status = "";
        SpeedText = "";
        EtaText = "";
        Progress = 0;
        IsPaused = false;
        IsComplete = false;
    }
}

public sealed class DownloadCenter
{
    private readonly ModelDownloadManager _manager;
    private readonly ModelLibraryService _library;
    private readonly Dictionary<string, DownloadState> _states = new();
    private readonly Dictionary<string, CancellationTokenSource> _running = new();

    public event Action? Changed;

    public DownloadCenter(ModelDownloadManager manager, ModelLibraryService library)
    {
        _manager = manager;
        _library = library;
    }

    public DownloadState GetState(string modelId)
    {
        if (_states.TryGetValue(modelId, out var state)) return state;
        state = new DownloadState();
        _states[modelId] = state;
        return state;
    }

    /// <summary>暂停下载：取消当前请求但保留 .part 断点，稍后可从断点继续。</summary>
    public void Pause(string modelId)
    {
        if (!_running.TryGetValue(modelId, out var cancellation)) return;

        GetState(modelId).IsPaused = true;
        cancellation.Cancel();
    }

    public async Task StartAsync(ModelEntry model, ModelQuant quant)
    {
        var state = GetState(model.Id);
        if (state.IsDownloading) return;

        state.Reset();
        state.IsDownloading = true;
        var queued = _running.Count > 0;
        state.Status = queued
            ? "排队中 · 等待其他模型下载完成"
            : _manager.GetPartialBytes(model, quant) > 0
                ? "正在从断点续传…"
                : "准备下载…";

        var cancellation = new CancellationTokenSource();
        _running[model.Id] = cancellation;

        var progress = new Progress<ModelDownloadProgress>(p =>
        {
            state.Progress = p.Percent;
            state.Status = p.Status;
            state.SpeedText = FormatSpeed(p.BytesPerSecond);
            state.EtaText = p.Eta is { TotalSeconds: > 0 } eta
                ? $"剩余 {FormatEta(eta)}"
                : "";
        });

        try
        {
            await _manager.DownloadAsync(model, quant, progress, cancellation.Token);
            state.IsComplete = true;
            state.Progress = 100;
            state.Status = "下载完成";
            state.SpeedText = "";
            state.EtaText = "";
            _library.NotifyChanged();
        }
        catch (OperationCanceledException) when (state.IsPaused)
        {
            state.Status = "已暂停 · 断点已保留，点击“继续下载”";
            state.SpeedText = "";
            state.EtaText = "";
        }
        catch (Exception ex)
        {
            state.Error = ex.Message;
            state.Status = "下载失败";
            state.SpeedText = "";
            state.EtaText = "";
        }
        finally
        {
            _running.Remove(model.Id);
            cancellation.Dispose();
            state.IsDownloading = false;
            Changed?.Invoke();
        }
    }

    private static string FormatSpeed(double bytesPerSecond) =>
        bytesPerSecond > 1024 * 1024
            ? $"{bytesPerSecond / 1024 / 1024:0.0} MB/s"
            : bytesPerSecond > 1024
                ? $"{bytesPerSecond / 1024:0} KB/s"
                : bytesPerSecond > 0
                    ? $"{bytesPerSecond:0} B/s"
                    : "";

    private static string FormatEta(TimeSpan eta) =>
        eta.TotalHours >= 1 ? eta.ToString(@"h\:mm\:ss") : eta.ToString(@"mm\:ss");
}
