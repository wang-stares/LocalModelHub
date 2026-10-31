using Avalonia.Controls;
using LocalModelHub.App.Services;
using LocalModelHub.App.ViewModels;

namespace LocalModelHub.App;

public partial class MainWindow : Window
{
    public MainWindow() => InitializeComponent();

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);
        try
        {
            AppServices.Current.Server.StopAsync().GetAwaiter().GetResult();
        }
        catch
        {
            // 关闭阶段忽略清理异常
        }

        try
        {
            AppServices.Current.SettingsService.Save();
        }
        catch
        {
            // ignore
        }

        try
        {
            if (DataContext is MainWindowViewModel viewModel)
            {
                viewModel.Chat.SaveCurrentSession();
            }
        }
        catch
        {
            // ignore
        }
    }
}
