using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LocalModelHub.App.Services;
using LocalModelHub.Core.Models;
using LocalModelHub.Core.Services;

namespace LocalModelHub.App.ViewModels;

public sealed partial class ModelFinderItem : ObservableObject
{
    private readonly Action<ModelFinderItem> _add;
    private readonly Action<ModelFinderItem> _reveal;

    public ModelFinderItem(
        FoundLocalModel model,
        bool added,
        Action<ModelFinderItem> add,
        Action<ModelFinderItem> reveal)
    {
        Model = model;
        _added = added;
        _add = add;
        _reveal = reveal;
    }

    public FoundLocalModel Model { get; }
    public string Name => Model.Name;
    public string Detail => Model.Detail;
    public string PathText => Model.Path;

    [ObservableProperty] private bool _added;

    public string AddText => Added ? "已在库中" : "添加到我的模型";
    partial void OnAddedChanged(bool value) => OnPropertyChanged(nameof(AddText));

    [RelayCommand]
    private void Add() => _add(this);

    [RelayCommand]
    private void Reveal() => _reveal(this);
}

/// <summary>全盘搜索本地模型：扫描固定磁盘，列出可直接加入库的 GGUF（原地引用，不复制文件）。</summary>
public sealed partial class ModelFinderViewModel : ObservableObject, IDisposable
{
    private readonly AppServices _services = AppServices.Current;
    private readonly LocalModelFinder _finder = new();
    private CancellationTokenSource? _cancellation;

    public ModelFinderViewModel()
    {
        _ = ScanAsync();
    }

    public ObservableCollection<ModelFinderItem> Items { get; } = [];

    [ObservableProperty] private bool _isScanning;
    [ObservableProperty] private string _statusText = "正在准备扫描…";
    [ObservableProperty] private string _countText = "";

    [RelayCommand]
    private async Task ScanAsync()
    {
        if (IsScanning) return;

        _cancellation?.Cancel();
        _cancellation?.Dispose();
        var cancellation = new CancellationTokenSource();
        _cancellation = cancellation;

        IsScanning = true;
        Items.Clear();
        CountText = "";
        StatusText = "正在扫描固定磁盘…";

        var installed = _services.InstalledModels
            .Select(model => model.PrimaryPath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var progress = new Progress<FinderProgress>(report =>
        {
            if (!ReferenceEquals(cancellation, _cancellation)) return; // 重新扫描时忽略旧进度
            StatusText = $"已检查 {report.FilesScanned} 个文件 · 找到 {report.Found} 个模型";
        });

        try
        {
            var roots = LocalModelFinder.FixedDrives();
            var found = await Task.Run(
                () => _finder.Search(roots, progress, cancellation.Token),
                cancellation.Token);

            foreach (var model in found.OrderByDescending(model => model.Bytes).ThenBy(model => model.Name))
            {
                Items.Add(new ModelFinderItem(
                    model,
                    installed.Contains(model.Path),
                    AddItem,
                    RevealItem));
            }

            CountText = Items.Count == 0
                ? "没有找到 .gguf 模型"
                : $"找到 {Items.Count} 个本地模型";
            StatusText = Items.Count == 0
                ? "已完成扫描：所有固定磁盘都没有可用的 GGUF 模型"
                : "点「添加到我的模型」即可原地引用，不会复制或移动文件";
        }
        catch (OperationCanceledException)
        {
            StatusText = "已取消扫描";
        }
        catch (Exception ex)
        {
            StatusText = $"扫描失败：{ex.Message}";
        }
        finally
        {
            if (ReferenceEquals(cancellation, _cancellation)) _cancellation = null;
            cancellation.Dispose();
            IsScanning = false;
        }
    }

    [RelayCommand]
    private void Cancel()
    {
        _cancellation?.Cancel();
        StatusText = "正在取消…";
    }

    private void AddItem(ModelFinderItem item)
    {
        if (item.Added) return;

        try
        {
            _services.Library.AddExternalModel(item.Model.Path);
            item.Added = true;
            StatusText = $"已加入「{item.Name}」（原地引用，文件仍保留在原路径）";
        }
        catch (Exception ex)
        {
            StatusText = $"添加失败：{ex.Message}";
        }
    }

    private static void RevealItem(ModelFinderItem item) =>
        ModelLauncher.OpenFolder(item.Model.Directory);

    public void Dispose()
    {
        _cancellation?.Cancel();
        _cancellation?.Dispose();
        _cancellation = null;
    }
}
