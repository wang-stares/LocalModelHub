using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LocalModelHub.App.Services;
using LocalModelHub.App.Views;
using LocalModelHub.Core.Models;
using LocalModelHub.Core.Services;

namespace LocalModelHub.App.ViewModels;

public sealed partial class LibraryItemViewModel : ObservableObject
{
    private readonly Func<LibraryItemViewModel, Task> _launch;
    private readonly Func<LibraryItemViewModel, Task> _stop;
    private readonly Func<LibraryItemViewModel, Task> _delete;
    private readonly Action<LibraryItemViewModel> _reveal;

    public LibraryItemViewModel(
        DownloadedModel model,
        Func<LibraryItemViewModel, Task> launch,
        Func<LibraryItemViewModel, Task> stop,
        Func<LibraryItemViewModel, Task> delete,
        Action<LibraryItemViewModel> reveal)
    {
        Model = model;
        _launch = launch;
        _stop = stop;
        _delete = delete;
        _reveal = reveal;
    }

    public DownloadedModel Model { get; }
    public string Title => Model.DisplayName;
    public string Initial => Title.Length <= 2 ? Title : Title[..2];
    public string Detail => $"{Model.QuantLabel} · {Model.SizeText} · {Model.Developer ?? "本地导入"}"
        + (string.IsNullOrWhiteSpace(Model.SelectionNote) ? "" : $" · {Model.SelectionNote}");
    public string PathText => Model.PrimaryPath;
    public string OriginText => Model.Entry?.CountryLabel ?? "本地";
    public bool IsChinese => Model.Entry?.IsChinese ?? false;
    public bool IsLocal => Model.Entry is null;
    public bool IsExternal => Model.IsExternal;

    [ObservableProperty] private bool _isRunning;

    public string StatusText => IsRunning ? $"运行中 · 端口 {AppServices.Current.Server.Port}" : Detail;

    [RelayCommand]
    private Task LaunchAsync() => _launch(this);

    [RelayCommand]
    private Task StopAsync() => _stop(this);

    [RelayCommand]
    private Task DeleteAsync() => _delete(this);

    [RelayCommand]
    private void Reveal() => _reveal(this);

    partial void OnIsRunningChanged(bool value) => OnPropertyChanged(nameof(StatusText));
}

public sealed partial class LibraryViewModel : ObservableObject
{
    private readonly AppServices _services = AppServices.Current;
    private readonly Func<DownloadedModel, Task> _launch;
    private readonly Action? _openStore;

    public LibraryViewModel(Func<DownloadedModel, Task> launch, Action? openStore = null)
    {
        _launch = launch;
        _openStore = openStore;
        _services.LibraryUpdated += () => Dispatcher.UIThread.Post(Refresh);
        _services.Server.StateChanged += (_, _) => Dispatcher.UIThread.Post(RefreshRunning);
        Refresh();
        RefreshRuntime();
    }

    public ObservableCollection<LibraryItemViewModel> Items { get; } = [];

    [ObservableProperty] private bool _isEmpty = true;
    [ObservableProperty] private string _countText = "";
    [ObservableProperty] private bool _isServerRunning;
    [ObservableProperty] private string _serverText = "推理服务未运行";

    [ObservableProperty] private bool _runtimeInstalled;
    [ObservableProperty] private string _runtimeStateText = "未安装";
    [ObservableProperty] private string _runtimePath = "尚未安装 llama.cpp 运行时";
    [ObservableProperty] private bool _isInstallingRuntime;
    [ObservableProperty] private double _runtimeProgress;
    [ObservableProperty] private string _runtimeStatus = "";

    [RelayCommand]
    private void RefreshModels() => _services.RefreshLibrary();

    [RelayCommand]
    private void OpenStore() => _openStore?.Invoke();

    [RelayCommand]
    private async Task ViewLogsAsync()
    {
        var log = _services.Server.LogTail;
        if (string.IsNullOrWhiteSpace(log))
        {
            await DialogService.AlertAsync("推理服务日志",
                "暂无日志。启动模型后，这里会显示 llama-server 的最近输出。");
            return;
        }

        var lines = log.Split('\n').TakeLast(20);
        await DialogService.AlertAsync("推理服务日志（最近 20 行）", string.Join('\n', lines));
    }

    [RelayCommand]
    private void OpenModelsFolder() => ModelLauncher.OpenFolder(_services.Settings.ModelsDirectory);

    /// <summary>全盘搜索已下载的 GGUF：用户可能换过文件夹，忘了模型放在哪。</summary>
    [RelayCommand]
    private async Task FindModelsAsync()
    {
        if (App.MainWindow is not { } owner) return;
        var dialog = new ModelFinderWindow();
        await dialog.ShowDialog(owner);
    }

    [RelayCommand]
    private async Task StopServerAsync()
    {
        _services.RemoteApi.Deactivate();
        _services.ServerStoppedByUser = true;
        _services.IdleSleep.Stop();
        await _services.Server.StopAsync();
        RefreshRunning();
    }

    [RelayCommand]
    private async Task InstallRuntimeAsync()
    {
        if (IsInstallingRuntime) return;
        IsInstallingRuntime = true;
        RuntimeProgress = 0;
        RuntimeStatus = "准备下载 llama.cpp 运行时…";
        try
        {
            var gpu = _services.Hardware.Gpus.FirstOrDefault();
            var progress = new Progress<RuntimeProgress>(p =>
            {
                RuntimeProgress = p.Percent;
                RuntimeStatus = p.Message;
            });
            var install = await _services.Runtime.EnsureAsync(gpu, progress, CancellationToken.None);
            RuntimeStatus = install.Variant == "existing" ? "已找到现成的运行时" : $"运行时已就绪（{install.Tag} · {install.Variant}）";
            RefreshRuntime();
        }
        catch (Exception ex)
        {
            RuntimeStatus = $"安装失败：{ex.Message}";
        }
        finally
        {
            IsInstallingRuntime = false;
        }
    }

    public void Refresh()
    {
        var selectedPath = _services.Server.LoadedModelPath;
        Items.Clear();
        foreach (var model in _services.InstalledModels)
        {
            Items.Add(new LibraryItemViewModel(model, LaunchAsync, StopAsync, DeleteAsync, Reveal)
            {
                IsRunning = _services.Server.IsRunning &&
                           string.Equals(selectedPath, model.PrimaryPath, StringComparison.OrdinalIgnoreCase)
            });
        }

        IsEmpty = Items.Count == 0;
        var total = _services.InstalledModels.Sum(m => m.TotalBytes);
        CountText = Items.Count == 0
            ? "还没有下载任何模型"
            : $"已下载 {Items.Count} 个模型 · 占用 {Formatters.Bytes(total)}";
        RefreshRunning();
    }

    public void RefreshRunning()
    {
        IsServerRunning = _services.Server.IsRunning;
        ServerText = IsServerRunning
            ? $"运行中 · {_services.Server.LoadedModelName} · 端口 {_services.Server.Port}"
            : "推理服务未运行";
        var path = _services.Server.LoadedModelPath;
        foreach (var item in Items)
        {
            item.IsRunning = IsServerRunning &&
                             string.Equals(path, item.Model.PrimaryPath, StringComparison.OrdinalIgnoreCase);
        }
    }

    private async Task LaunchAsync(LibraryItemViewModel item) => await _launch(item.Model);

    private async Task StopAsync(LibraryItemViewModel item)
    {
        _services.RemoteApi.Deactivate();
        _services.ServerStoppedByUser = true;
        _services.IdleSleep.Stop();
        await _services.Server.StopAsync();
        RefreshRunning();
    }

    private async Task DeleteAsync(LibraryItemViewModel item)
    {
        if (item.Model.IsExternal)
        {
            var confirmed = await DialogService.ConfirmAsync(
                "移除外部模型",
                $"将从列表移除「{item.Title}」的引用，磁盘文件保持不动：\n{item.Model.PrimaryPath}",
                "移除引用");
            if (!confirmed) return;

            _services.Library.Delete(item.Model);
            Refresh();
            return;
        }

        var plan = _services.Library.PlanDelete(item.Model);
        if (plan.Files.Count == 0)
        {
            await DialogService.AlertAsync(
                "模型文件已不存在",
                $"磁盘上已找不到 {item.Title} 的模型文件，列表已刷新。\n路径：{item.Model.FolderPath}");
            Refresh();
            return;
        }

        var names = string.Join("、", plan.Files.Take(3).Select(Path.GetFileName));
        var more = plan.Files.Count > 3 ? $" 等 {plan.Files.Count} 个文件" : "";
        var keepText = plan.RemainingSiblingFiles > 0
            ? $"\n同目录还有 {plan.RemainingSiblingFiles} 个其它文件，将保留不动。"
            : "";

        var message =
            $"将删除 {item.Title}（{item.Model.QuantLabel}）：\n" +
            $"· {plan.Files.Count} 个文件，共 {Formatters.Bytes(plan.TotalBytes)}{more}\n" +
            $"· {names}{keepText}\n\n" +
            "「移入回收站」可随时恢复；「彻底删除」不进入回收站，无法恢复。";

        var choice = await DialogService.ConfirmWithAlternativeAsync(
            "删除模型", message, "移入回收站", "彻底删除");
        if (choice == ConfirmChoice.Cancel) return;

        if (item.IsRunning)
        {
            await _services.Server.StopAsync();
        }

        var result = _services.Library.Delete(item.Model, useRecycleBin: choice == ConfirmChoice.Confirm);
        Refresh();

        if (!result.Success)
        {
            await DialogService.AlertAsync(
                "删除未完成",
                $"{result.Error}\n\n模型文件仍保留在：{item.Model.FolderPath}\n" +
                "如果 llama-server 或其它程序正在读取这些文件，请先停止服务后重试。");
            return;
        }

        string freeText = "";
        if (DownloadService.TryCheckFreeSpace(_services.Settings.ModelsDirectory, 0, out var available))
        {
            freeText = $"，当前磁盘可用 {Formatters.Bytes(available)}";
        }

        var action = result.UsedRecycleBin ? "已移入回收站" : "已彻底删除";
        await DialogService.AlertAsync(
            "删除完成",
            $"删除 {result.DeletedFiles} 个文件，释放 {Formatters.Bytes(result.ReleasedBytes)}{freeText}。\n{action}。" +
            (result.RemainingSiblingFiles > 0 ? $"\n同目录保留 {result.RemainingSiblingFiles} 个其它文件。" : ""));
    }

    private void Reveal(LibraryItemViewModel item) => ModelLauncher.OpenFolder(item.Model.FolderPath);

    private void RefreshRuntime()
    {
        var server = _services.Runtime.FindExistingServer();
        RuntimeInstalled = server is not null;
        RuntimeStateText = RuntimeInstalled ? "已安装" : "未安装";
        RuntimePath = server ?? "尚未安装 llama.cpp 运行时（首次启动模型会自动下载）";
    }
}
