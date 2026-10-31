using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using LocalModelHub.App.Services;
using LocalModelHub.App.ViewModels;

namespace LocalModelHub.App;

public partial class App : Application
{
    public static Window? MainWindow { get; private set; }

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    /// <summary>system / light / dark → Avalonia 主题变体；跟随系统时使用 Default。</summary>
    public static void ApplyTheme(string? theme)
    {
        if (Current is not { } app) return;
        app.RequestedThemeVariant = theme switch
        {
            "light" => ThemeVariant.Light,
            "dark" => ThemeVariant.Dark,
            _ => ThemeVariant.Default
        };
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            AppServices.Current.Initialize();
            ApplyTheme(AppServices.Current.Settings.Theme);

            var viewModel = new MainWindowViewModel();
            var window = new MainWindow { DataContext = viewModel };

            MainWindow = window;
            desktop.MainWindow = window;

            desktop.ShutdownRequested += (_, _) =>
            {
                try
                {
                    AppServices.Current.SettingsService.Save();
                }
                catch
                {
                    // ignore
                }
            };
        }

        base.OnFrameworkInitializationCompleted();
    }
}
