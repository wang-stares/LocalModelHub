using Avalonia.Controls;

namespace LocalModelHub.App.Views;

public partial class SettingsView : UserControl
{
    public SettingsView() => InitializeComponent();

    private void SelectCategory(object? sender, SelectionChangedEventArgs e)
    {
        if (CategoryList is null) return;
        var key = (CategoryList.SelectedItem as ListBoxItem)?.Tag as string;
        if (string.IsNullOrEmpty(key)) return;

        AppearanceSection.IsVisible = key == "Appearance";
        DownloadSection.IsVisible = key == "Download";
        ModelSourceSection.IsVisible = key == "ModelSource";
        StorageSection.IsVisible = key == "Storage";
        ApiAccessSection.IsVisible = key == "ApiAccess";
        ChatParamsSection.IsVisible = key == "ChatParams";
        RuntimeSection.IsVisible = key == "Runtime";
        SettingsScroll.Offset = default;
    }
}
