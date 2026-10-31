using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LocalModelHub.App.Services;
using LocalModelHub.Core.Models;
using LocalModelHub.Core.Services;

namespace LocalModelHub.App.ViewModels;

public sealed partial class KnowledgeDocumentItemViewModel : ObservableObject
{
    private readonly Action<string> _delete;

    public KnowledgeDocumentItemViewModel(KnowledgeDocument document, Action<string> delete)
    {
        Document = document;
        _delete = delete;
    }

    public KnowledgeDocument Document { get; }
    public string Title => Document.Name;
    public string Detail => $"{Document.Chunks.Count} 个片段 · {Formatters.Bytes(Document.Bytes)} · {Document.AddedAt:MM-dd HH:mm}";
    public string PathText => Document.SourcePath;

    [RelayCommand]
    private void Delete() => _delete(Document.Id);
}

public sealed class KnowledgeHitItemViewModel
{
    public KnowledgeHitItemViewModel(int index, KnowledgeHit hit)
    {
        SourceText = $"[{index}] {hit.DocumentName} · 第 {hit.ChunkIndex + 1} 段";
        Snippet = hit.Text.Length <= 260 ? hit.Text : hit.Text[..260] + "…";
        ScoreText = $"匹配度 {hit.Score:0.0}";
    }

    public string SourceText { get; }
    public string Snippet { get; }
    public string ScoreText { get; }
}

public sealed partial class KnowledgeViewModel : ObservableObject
{
    private readonly AppServices _services = AppServices.Current;

    public KnowledgeViewModel()
    {
        KnowledgeEnabled = _services.Settings.KnowledgeEnabled;
        Refresh();
    }

    public ObservableCollection<KnowledgeDocumentItemViewModel> Documents { get; } = [];
    public ObservableCollection<KnowledgeHitItemViewModel> Results { get; } = [];

    [ObservableProperty] private bool? _knowledgeEnabled;
    [ObservableProperty] private string _countText = "";
    [ObservableProperty] private string _importStatus = "";
    [ObservableProperty] private bool _isImporting;
    [ObservableProperty] private bool _isEmpty = true;
    [ObservableProperty] private bool _hasDocuments;
    [ObservableProperty] private bool _hasResults;
    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private string _searchStatus = "导入文档后可在这里测试检索效果。";

    partial void OnKnowledgeEnabledChanged(bool? value)
    {
        var enabled = value != false;
        if (_services.Settings.KnowledgeEnabled == enabled) return;

        _services.Settings.KnowledgeEnabled = enabled;
        _services.SettingsService.Save();
        ImportStatus = enabled ? "已开启对话检索" : "已关闭对话检索";
    }

    [RelayCommand]
    private async Task ImportFileAsync()
    {
        if (IsImporting) return;
        var path = await FileDialogService.PickFileAsync("导入知识库文档", KnowledgeService.SupportedPatterns());
        if (path is null) return;

        IsImporting = true;
        ImportStatus = "正在切分并建立索引…";
        try
        {
            var document = await _services.Knowledge.ImportAsync(path, CancellationToken.None);
            ImportStatus = $"已导入 {document.Name} · {document.Chunks.Count} 个片段";
            Refresh();
        }
        catch (Exception ex)
        {
            ImportStatus = $"导入失败：{ex.Message}";
        }
        finally
        {
            IsImporting = false;
        }
    }

    [RelayCommand]
    private async Task ImportFolderAsync()
    {
        if (IsImporting) return;
        var folder = await FileDialogService.PickFolderAsync("选择要导入的文件夹");
        if (folder is null) return;

        IsImporting = true;
        ImportStatus = "正在扫描文件夹…";
        try
        {
            var progress = new Progress<KnowledgeImportProgress>(item =>
                ImportStatus = $"正在索引 {item.Current}/{item.Total} · {item.FileName}");
            var result = await _services.Knowledge.ImportFolderAsync(folder, progress, CancellationToken.None);
            ImportStatus = result.Imported == 0 && result.Skipped == 0 && result.Failed == 0
                ? "文件夹中没有可导入的文本文件"
                : $"导入 {result.Imported} 个文件" +
                  (result.Skipped > 0 ? $" · 跳过 {result.Skipped} 个" : "") +
                  (result.Failed > 0 ? $" · 失败 {result.Failed} 个" : "");
            Refresh();
        }
        catch (Exception ex)
        {
            ImportStatus = $"导入失败：{ex.Message}";
        }
        finally
        {
            IsImporting = false;
        }
    }

    [RelayCommand]
    private async Task ClearAsync()
    {
        if (Documents.Count == 0) return;

        var confirmed = await DialogService.ConfirmAsync(
            "清空知识库",
            $"将删除全部 {Documents.Count} 个文档与索引，此操作不可撤销。",
            "清空");
        if (!confirmed) return;

        _services.Knowledge.Clear();
        ImportStatus = "知识库已清空";
        Refresh();
        ClearResults("导入文档后可在这里测试检索效果。");
    }

    [RelayCommand]
    private void Search()
    {
        var query = SearchText.Trim();
        if (query.Length == 0)
        {
            ClearResults("请输入要检索的关键词或问题。");
            return;
        }

        var hits = _services.Knowledge.Search(query, 5);
        Results.Clear();
        for (var index = 0; index < hits.Count; index++)
        {
            Results.Add(new KnowledgeHitItemViewModel(index + 1, hits[index]));
        }

        HasResults = Results.Count > 0;
        SearchStatus = Results.Count == 0
            ? "没有匹配的片段，试试更具体的关键词。"
            : $"找到 {Results.Count} 个相关片段，对话时最多注入 {Math.Clamp(_services.Settings.KnowledgeTopK, 1, 8)} 个。";
    }

    public void Refresh()
    {
        Documents.Clear();
        foreach (var document in _services.Knowledge.Documents.OrderByDescending(document => document.AddedAt))
        {
            Documents.Add(new KnowledgeDocumentItemViewModel(document, DeleteDocument));
        }

        HasDocuments = Documents.Count > 0;
        IsEmpty = !HasDocuments;
        CountText = HasDocuments
            ? $"已索引 {Documents.Count} 个文档 · {_services.Knowledge.ChunkCount} 个片段 · 对话默认注入 {Math.Clamp(_services.Settings.KnowledgeTopK, 1, 8)} 个"
            : "还没有知识库文档";
    }

    private void DeleteDocument(string documentId)
    {
        if (!_services.Knowledge.Delete(documentId)) return;

        ImportStatus = "已删除文档";
        Refresh();
        ClearResults("已删除文档，可重新检索。");
    }

    private void ClearResults(string status)
    {
        Results.Clear();
        HasResults = false;
        SearchStatus = status;
    }
}
