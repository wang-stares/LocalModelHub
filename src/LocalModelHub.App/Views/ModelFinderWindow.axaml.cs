using Avalonia.Controls;
using Avalonia.Interactivity;
using LocalModelHub.App.ViewModels;

namespace LocalModelHub.App.Views;

public partial class ModelFinderWindow : Window
{
    private readonly ModelFinderViewModel _viewModel = new();

    public ModelFinderWindow()
    {
        InitializeComponent();
        DataContext = _viewModel;
        Closed += (_, _) => _viewModel.Dispose();
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
