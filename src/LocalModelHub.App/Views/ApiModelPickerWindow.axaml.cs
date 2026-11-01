using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace LocalModelHub.App.Views;

public partial class ApiModelPickerWindow : Window
{
    private readonly IReadOnlyList<string> _models;

    public ApiModelPickerWindow(IReadOnlyList<string> models)
    {
        InitializeComponent();
        MainWindow.ApplyShellStyle(this);
        _models = models;
        ApplyFilter("");
        SearchBox.TextChanged += (_, _) => ApplyFilter(SearchBox.Text ?? "");
        ModelList.DoubleTapped += (_, _) => Confirm();
    }

    private void ApplyFilter(string query)
    {
        var text = query.Trim();
        var filtered = text.Length == 0
            ? _models
            : _models.Where(id => id.Contains(text, StringComparison.OrdinalIgnoreCase)).ToList();

        ModelList.ItemsSource = filtered;
        if (filtered.Count > 0) ModelList.SelectedIndex = 0;
        CountText.Text = $"共 {filtered.Count} 个模型";
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(null);

    private void OnConfirm(object? sender, RoutedEventArgs e) => Confirm();

    private void Confirm()
    {
        if (ModelList.SelectedItem is string selected && selected.Length > 0)
        {
            Close(selected);
        }
    }
}
