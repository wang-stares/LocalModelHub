using Avalonia.Platform.Storage;

namespace LocalModelHub.App.Services;

public static class FileDialogService
{
    public static async Task<string?> PickFileAsync(string title, params string[] patterns)
    {
        var window = App.MainWindow;
        if (window is null) return null;

        var files = await window.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            FileTypeFilter = patterns.Length > 0
                ? [new FilePickerFileType("文件") { Patterns = patterns }]
                : null
        });

        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }

    public static async Task<string?> PickSaveFileAsync(string title, string suggestedName, string typeName = "JSON 文件", params string[] patterns)
    {
        var window = App.MainWindow;
        if (window is null) return null;

        var file = await window.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = title,
            SuggestedFileName = suggestedName,
            ShowOverwritePrompt = true,
            FileTypeChoices = patterns.Length > 0
                ? [new FilePickerFileType(typeName) { Patterns = patterns }]
                : null
        });

        return file?.TryGetLocalPath();
    }

    public static async Task<string?> PickFolderAsync(string title)
    {
        var window = App.MainWindow;
        if (window is null) return null;

        var folders = await window.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = title,
            AllowMultiple = false
        });

        return folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
    }
}
