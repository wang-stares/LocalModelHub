using System.ComponentModel;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using LocalModelHub.App.Services;
using LocalModelHub.App.ViewModels;

namespace LocalModelHub.App;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        // 系统磨砂是否可用由平台决定，需在窗口显示后按实际结果切换视觉层
        PropertyChanged += (_, args) =>
        {
            if (args.Property == ActualTransparencyLevelProperty)
            {
                ApplyWindowMaterial();
            }
        };
        ActualThemeVariantChanged += (_, _) => ApplyWindowMaterial();
        DataContextChanged += OnDataContextChanged;
    }

    private MainWindowViewModel? _animatedViewModel;

    /// <summary>
    /// 页面切换：淡入 + 10px 浮起。变换与透明度都在页面宿主上执行，
    /// 不改变页面内部布局，也不参与命中测试，长列表页同样流畅。
    /// </summary>
    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_animatedViewModel is not null)
        {
            _animatedViewModel.PropertyChanged -= OnViewModelPropertyChanged;
        }

        _animatedViewModel = DataContext as MainWindowViewModel;
        if (_animatedViewModel is not null)
        {
            _animatedViewModel.PropertyChanged += OnViewModelPropertyChanged;
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainWindowViewModel.CurrentPage))
        {
            PlayPageTransition();
        }
    }

    private void PlayPageTransition()
    {
        if (PageHost is null || _pageTransitionRunning)
        {
            return;
        }

        _pageTransitionRunning = true;
        PageHost.Opacity = 0;
        PageHost.RenderTransform = new TranslateTransform(0, 10);

        // 等一帧再落到终态，让过渡从起始值开始插值
        Dispatcher.UIThread.Post(() =>
        {
            PageHost.Opacity = 1;
            PageHost.RenderTransform = new TranslateTransform(0, 0);
            _pageTransitionRunning = false;
        }, DispatcherPriority.Render);
    }

    private bool _pageTransitionRunning;

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        ApplyWindowMaterial();
    }

    /// <summary>
    /// 系统磨砂（Mica / 亚克力模糊）生效时，窗口底板透明、隐藏自绘光晕，让桌面透出；
    /// 否则回落到渐变材质底，保证任何系统版本下都可读。
    /// </summary>
    private void ApplyWindowMaterial()
    {
        var level = ActualTransparencyLevel;
        var frosted = level == WindowTransparencyLevel.Mica
            || level == WindowTransparencyLevel.AcrylicBlur
            || level == WindowTransparencyLevel.Blur;

        ShellRoot.Classes.Set("frosted", frosted);
        BloomLayer.IsVisible = !frosted;
        BloomLayerAlt.IsVisible = !frosted;

        if (OperatingSystem.IsWindows())
        {
            SetImmersiveDarkMode(ActualThemeVariant == ThemeVariant.Dark);
        }
    }

    /// <summary>深色主题下同步系统背板与标题栏配色，避免磨砂底发白。</summary>
    private void SetImmersiveDarkMode(bool dark)
    {
        var handle = TryGetPlatformHandle();
        if (handle is null)
        {
            return;
        }

        var value = dark ? 1 : 0;
        try
        {
            DwmSetWindowAttribute(handle.Handle, 20, ref value, sizeof(int));
        }
        catch
        {
            // 非 Windows 11 / 无 DWM 时忽略
        }
    }

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

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
