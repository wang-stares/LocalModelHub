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
        ActualThemeVariantChanged += (_, _) =>
        {
            ApplyWindowMaterial();
            ApplyShellTheme(this);
        };
        DataContextChanged += OnDataContextChanged;
    }

    private static readonly string[] ShellPaletteKeys =
    [
        "AppBg", "BloomBrush", "BloomBrushAlt", "MicaBg", "MicaStroke", "SidebarBg",
        "ContentSurface", "NavBg", "NavActiveBg", "NavHoverBg",
        "CardBg", "CardHoverBg", "CardSunkenBg", "SegBg", "SegSelectedBg", "InputBg",
        "Stroke", "StrokeSubtle", "StrokeStrong", "TextPrimary", "TextSecondary", "TextMuted",
        "Accent", "AccentHover", "AccentPressed", "AccentSoft", "AccentSoftHover", "AccentInk", "OnAccent",
        "AvatarBg", "FocusRing", "FocusGlow", "OverlayBg",
        "Success", "Warning", "Danger", "SystemAccentColor",
        "ButtonFace", "ButtonFaceHover", "ButtonFacePressed", "ButtonBorder",
        "SelectionBg", "SelectionHoverBg", "PrimaryFace"
    ];

    /// <summary>
    /// 切换窗口主题：miuix 保持大圆角与光晕；win11 / win10 / win7 分别还原各自年代的形态与配色。
    /// 调色板写入窗口资源，按 DynamicResource 覆盖 App 级令牌；明暗切换时重新计算。
    /// </summary>
    public void SetShellStyle(string? style)
    {
        ApplyShellClass(this, style);
        ApplyShellTheme(this);
    }

    /// <summary>把同一套调色板应用到任意窗口，让对话框与主窗口保持一致。</summary>
    public static void ApplyShellStyle(Window window)
    {
        ApplyShellClass(window, AppServices.Current.Settings.ShellStyle);
        ApplyShellTheme(window);
    }

    private static string NormalizeShellStyle(string? style) => style switch
    {
        "win10" or "windows10" => "win10",
        "win7" or "windows7" => "win7",
        "windows" or "win11" => "win11",
        _ => "miuix"
    };

    private static void ApplyShellClass(Window window, string? style)
    {
        var key = NormalizeShellStyle(style);
        window.Classes.Set("windows", key != "miuix");
        window.Classes.Set("win11", key == "win11");
        window.Classes.Set("win10", key == "win10");
        window.Classes.Set("win7", key == "win7");
    }

    private static void ApplyShellTheme(Window window)
    {
        ResourceDictionary? dict = window.Resources as ResourceDictionary;
        if (dict is not null)
        {
            foreach (var key in ShellPaletteKeys)
            {
                dict.Remove(key);
            }
        }

        if (!window.Classes.Contains("windows"))
        {
            return;
        }

        dict ??= new ResourceDictionary();
        window.Resources = dict;
        var dark = (Application.Current?.ActualThemeVariant ?? window.ActualThemeVariant) == ThemeVariant.Dark;
        if (window.Classes.Contains("win10"))
        {
            FillWin10(dict, dark);
        }
        else if (window.Classes.Contains("win7"))
        {
            FillWin7(dict, dark);
        }
        else
        {
            FillWin11(dict, dark);
        }
    }

    /// <summary>Windows 11：Fluent 直角、实色表面、Mica 语义色。</summary>
    private static void FillWin11(ResourceDictionary dict, bool dark)
    {
        if (dark)
        {
            dict["AppBg"] = B("#202020");
            dict["BloomBrush"] = Brushes.Transparent;
            dict["BloomBrushAlt"] = Brushes.Transparent;
            dict["MicaBg"] = B("#F2202020");
            dict["MicaStroke"] = B("#2E2E2E");
            dict["SidebarBg"] = B("#202020");
            dict["ContentSurface"] = B("#202020");
            dict["NavBg"] = B("#202020");
            dict["NavActiveBg"] = B("#1AFFFFFF");
            dict["NavHoverBg"] = B("#0FFFFFFF");
            dict["CardBg"] = B("#2B2B2B");
            dict["CardHoverBg"] = B("#323232");
            dict["CardSunkenBg"] = B("#14000000");
            dict["SegBg"] = B("#1A1A1A");
            dict["SegSelectedBg"] = B("#3A3A3A");
            dict["InputBg"] = B("#14FFFFFF");
            dict["Stroke"] = B("#2E2E2E");
            dict["StrokeSubtle"] = B("#262626");
            dict["StrokeStrong"] = B("#3D3D3D");
            dict["TextPrimary"] = B("#FFFFFF");
            dict["TextSecondary"] = B("#C8C8C8");
            dict["TextMuted"] = B("#9E9E9E");
            dict["Accent"] = B("#60CDFF");
            dict["AccentHover"] = B("#7FD7FF");
            dict["AccentPressed"] = B("#4CC2FF");
            dict["AccentSoft"] = B("#1F60CDFF");
            dict["AccentSoftHover"] = B("#2E60CDFF");
            dict["AccentInk"] = B("#99EBFF");
            dict["OnAccent"] = B("#101010");
            dict["AvatarBg"] = B("#1F60CDFF");
            dict["FocusRing"] = B("#8A60CDFF");
            dict["FocusGlow"] = B("#3360CDFF");
            dict["OverlayBg"] = B("#CC202020");
            dict["Success"] = B("#6CCB5F");
            dict["Warning"] = B("#FCE100");
            dict["Danger"] = B("#FF99A4");
            dict["SystemAccentColor"] = Color.Parse("#60CDFF");
            return;
        }

        dict["AppBg"] = B("#F3F3F3");
        dict["BloomBrush"] = Brushes.Transparent;
        dict["BloomBrushAlt"] = Brushes.Transparent;
        dict["MicaBg"] = B("#FFFFFF");
        dict["MicaStroke"] = B("#E5E5E5");
        dict["SidebarBg"] = B("#F3F3F3");
        dict["ContentSurface"] = B("#F3F3F3");
        dict["NavBg"] = B("#F9F9F9");
        dict["NavActiveBg"] = B("#10000000");
        dict["NavHoverBg"] = B("#08000000");
        dict["CardBg"] = B("#FFFFFF");
        dict["CardHoverBg"] = B("#FAFAFA");
        dict["CardSunkenBg"] = B("#08000000");
        dict["SegBg"] = B("#0A000000");
        dict["SegSelectedBg"] = B("#FFFFFF");
        dict["InputBg"] = B("#0F000000");
        dict["Stroke"] = B("#E5E5E5");
        dict["StrokeSubtle"] = B("#F0F0F0");
        dict["StrokeStrong"] = B("#C9C9C9");
        dict["TextPrimary"] = B("#1A1A1A");
        dict["TextSecondary"] = B("#5B5B5B");
        dict["TextMuted"] = B("#8A8A8A");
        dict["Accent"] = B("#0067C0");
        dict["AccentHover"] = B("#0078D4");
        dict["AccentPressed"] = B("#005A9E");
        dict["AccentSoft"] = B("#1A0067C0");
        dict["AccentSoftHover"] = B("#290067C0");
        dict["AccentInk"] = B("#005FB8");
        dict["OnAccent"] = B("#FFFFFF");
        dict["AvatarBg"] = B("#1A0067C0");
        dict["FocusRing"] = B("#800067C0");
        dict["FocusGlow"] = B("#330067C0");
        dict["OverlayBg"] = B("#CCF3F3F3");
        dict["Success"] = B("#0F7B0F");
        dict["Warning"] = B("#9D5D00");
        dict["Danger"] = B("#C42B1C");
        dict["SystemAccentColor"] = Color.Parse("#0067C0");
    }

    /// <summary>Windows 10：2px 直角、扁平灰面、#0078D7 强调色。</summary>
    private static void FillWin10(ResourceDictionary dict, bool dark)
    {
        if (dark)
        {
            dict["AppBg"] = B("#1F1F1F");
            dict["BloomBrush"] = Brushes.Transparent;
            dict["BloomBrushAlt"] = Brushes.Transparent;
            dict["MicaBg"] = B("#F2202020");
            dict["MicaStroke"] = B("#3F3F3F");
            dict["SidebarBg"] = B("#1F1F1F");
            dict["ContentSurface"] = B("#1F1F1F");
            dict["NavBg"] = B("#1F1F1F");
            dict["NavActiveBg"] = B("#1AFFFFFF");
            dict["NavHoverBg"] = B("#0FFFFFFF");
            dict["CardBg"] = B("#2D2D2D");
            dict["CardHoverBg"] = B("#343434");
            dict["CardSunkenBg"] = B("#14000000");
            dict["SegBg"] = B("#1A1A1A");
            dict["SegSelectedBg"] = B("#203E5B");
            dict["InputBg"] = B("#2D2D2D");
            dict["Stroke"] = B("#3F3F3F");
            dict["StrokeSubtle"] = B("#333333");
            dict["StrokeStrong"] = B("#5A5A5A");
            dict["TextPrimary"] = B("#FFFFFF");
            dict["TextSecondary"] = B("#C5C5C5");
            dict["TextMuted"] = B("#A0A0A0");
            dict["Accent"] = B("#3AA0E0");
            dict["AccentHover"] = B("#4FB3F2");
            dict["AccentPressed"] = B("#2A8FCF");
            dict["AccentSoft"] = B("#1F3AA0E0");
            dict["AccentSoftHover"] = B("#2E3AA0E0");
            dict["AccentInk"] = B("#9CD3F2");
            dict["OnAccent"] = B("#0D1B24");
            dict["AvatarBg"] = B("#1F3AA0E0");
            dict["FocusRing"] = B("#8A3AA0E0");
            dict["FocusGlow"] = B("#333AA0E0");
            dict["OverlayBg"] = B("#CC1F1F1F");
            dict["Success"] = B("#6CCB5F");
            dict["Warning"] = B("#FCE100");
            dict["Danger"] = B("#FF8A80");
            dict["SystemAccentColor"] = Color.Parse("#3AA0E0");
            dict["ButtonFace"] = B("#333333");
            dict["ButtonFaceHover"] = B("#3E3E3E");
            dict["ButtonFacePressed"] = B("#4A4A4A");
            dict["ButtonBorder"] = B("#5A5A5A");
            dict["SelectionBg"] = B("#1F3AA0E0");
            dict["SelectionHoverBg"] = B("#2E3AA0E0");
            return;
        }

        dict["AppBg"] = B("#F2F2F2");
        dict["BloomBrush"] = Brushes.Transparent;
        dict["BloomBrushAlt"] = Brushes.Transparent;
        dict["MicaBg"] = B("#FFFFFF");
        dict["MicaStroke"] = B("#D9D9D9");
        dict["SidebarBg"] = B("#F2F2F2");
        dict["ContentSurface"] = B("#F2F2F2");
        dict["NavBg"] = B("#F2F2F2");
        dict["NavActiveBg"] = B("#1A000000");
        dict["NavHoverBg"] = B("#0A000000");
        dict["CardBg"] = B("#FFFFFF");
        dict["CardHoverBg"] = B("#FAFAFA");
        dict["CardSunkenBg"] = B("#08000000");
        dict["SegBg"] = B("#E6E6E6");
        dict["SegSelectedBg"] = B("#CCE4F7");
        dict["InputBg"] = B("#FFFFFF");
        dict["Stroke"] = B("#D9D9D9");
        dict["StrokeSubtle"] = B("#EDEDED");
        dict["StrokeStrong"] = B("#B3B3B3");
        dict["TextPrimary"] = B("#1A1A1A");
        dict["TextSecondary"] = B("#5B5B5B");
        dict["TextMuted"] = B("#8A8A8A");
        dict["Accent"] = B("#0078D7");
        dict["AccentHover"] = B("#106EBE");
        dict["AccentPressed"] = B("#005A9E");
        dict["AccentSoft"] = B("#1A0078D7");
        dict["AccentSoftHover"] = B("#290078D7");
        dict["AccentInk"] = B("#005A9E");
        dict["OnAccent"] = B("#FFFFFF");
        dict["AvatarBg"] = B("#1A0078D7");
        dict["FocusRing"] = B("#800078D7");
        dict["FocusGlow"] = B("#330078D7");
        dict["OverlayBg"] = B("#CCF2F2F2");
        dict["Success"] = B("#107C10");
        dict["Warning"] = B("#9D5D00");
        dict["Danger"] = B("#E81123");
        dict["SystemAccentColor"] = Color.Parse("#0078D7");
        dict["ButtonFace"] = B("#E1E1E1");
        dict["ButtonFaceHover"] = B("#E5F1FB");
        dict["ButtonFacePressed"] = B("#CCE4F7");
        dict["ButtonBorder"] = B("#CCCCCC");
        dict["SelectionBg"] = B("#CCE4F7");
        dict["SelectionHoverBg"] = B("#E5F1FB");
    }

    /// <summary>Windows 7：Aero 渐变面板、浅蓝高亮、圆角按钮。</summary>
    private static void FillWin7(ResourceDictionary dict, bool dark)
    {
        if (dark)
        {
            dict["AppBg"] = V("#2A313C", "#1B2028");
            dict["BloomBrush"] = Brushes.Transparent;
            dict["BloomBrushAlt"] = Brushes.Transparent;
            dict["MicaBg"] = B("#E6262C36");
            dict["MicaStroke"] = B("#3C4654");
            dict["SidebarBg"] = B("#232932");
            dict["ContentSurface"] = B("#2A303A");
            dict["NavBg"] = B("#232932");
            dict["NavActiveBg"] = B("#1F4C74");
            dict["NavHoverBg"] = B("#163A57");
            dict["CardBg"] = B("#303740");
            dict["CardHoverBg"] = B("#39414C");
            dict["CardSunkenBg"] = B("#14000000");
            dict["SegBg"] = B("#222831");
            dict["SegSelectedBg"] = B("#3A4A5C");
            dict["InputBg"] = B("#2A303A");
            dict["Stroke"] = B("#44505E");
            dict["StrokeSubtle"] = B("#333C47");
            dict["StrokeStrong"] = B("#5A6A7C");
            dict["TextPrimary"] = B("#F2F6FB");
            dict["TextSecondary"] = B("#C3CFDC");
            dict["TextMuted"] = B("#93A2B3");
            dict["Accent"] = B("#6FB3E8");
            dict["AccentHover"] = B("#8CC5F2");
            dict["AccentPressed"] = B("#4F97D1");
            dict["AccentSoft"] = B("#1F6FB3E8");
            dict["AccentSoftHover"] = B("#2E6FB3E8");
            dict["AccentInk"] = B("#A8D6F5");
            dict["OnAccent"] = B("#0A1A28");
            dict["AvatarBg"] = B("#1F6FB3E8");
            dict["FocusRing"] = B("#806FB3E8");
            dict["FocusGlow"] = B("#336FB3E8");
            dict["OverlayBg"] = B("#CC2A313C");
            dict["Success"] = B("#7BC67B");
            dict["Warning"] = B("#F0C674");
            dict["Danger"] = B("#F07A72");
            dict["SystemAccentColor"] = Color.Parse("#6FB3E8");
            dict["ButtonFace"] = V("#4A5461", "#39424E");
            dict["ButtonFaceHover"] = V("#5A6573", "#46515E");
            dict["ButtonFacePressed"] = V("#333C47", "#414B57");
            dict["ButtonBorder"] = B("#5A6675");
            dict["SelectionBg"] = B("#1F4C74");
            dict["SelectionHoverBg"] = B("#235A85");
            dict["PrimaryFace"] = V("#4E8FC4", "#2A628C");
            return;
        }

        dict["AppBg"] = V("#EDF4FC", "#D3E3F5");
        dict["BloomBrush"] = Brushes.Transparent;
        dict["BloomBrushAlt"] = Brushes.Transparent;
        dict["MicaBg"] = B("#F7FFFFFF");
        dict["MicaStroke"] = B("#A8C0D8");
        dict["SidebarBg"] = B("#E7F0FA");
        dict["ContentSurface"] = B("#F7FAFD");
        dict["NavBg"] = B("#EDF4FB");
        dict["NavActiveBg"] = B("#CDE8FF");
        dict["NavHoverBg"] = B("#E3F1FF");
        dict["CardBg"] = B("#FFFFFF");
        dict["CardHoverBg"] = B("#F5FAFF");
        dict["CardSunkenBg"] = B("#0A0066CC");
        dict["SegBg"] = B("#DDE9F5");
        dict["SegSelectedBg"] = B("#FFFFFF");
        dict["InputBg"] = B("#FFFFFF");
        dict["Stroke"] = B("#B8CBDE");
        dict["StrokeSubtle"] = B("#DCE6F1");
        dict["StrokeStrong"] = B("#8FAECB");
        dict["TextPrimary"] = B("#1A1A1A");
        dict["TextSecondary"] = B("#4A5A6B");
        dict["TextMuted"] = B("#6D7F92");
        dict["Accent"] = B("#3D8BC9");
        dict["AccentHover"] = B("#5AA0D8");
        dict["AccentPressed"] = B("#2F73A8");
        dict["AccentSoft"] = B("#1A3D8BC9");
        dict["AccentSoftHover"] = B("#293D8BC9");
        dict["AccentInk"] = B("#1E5F8F");
        dict["OnAccent"] = B("#FFFFFF");
        dict["AvatarBg"] = B("#1A3D8BC9");
        dict["FocusRing"] = B("#803D8BC9");
        dict["FocusGlow"] = B("#333D8BC9");
        dict["OverlayBg"] = B("#CCF2F6FB");
        dict["Success"] = B("#2E8B2E");
        dict["Warning"] = B("#B36B00");
        dict["Danger"] = B("#C42B1C");
        dict["SystemAccentColor"] = Color.Parse("#3D8BC9");
        dict["ButtonFace"] = V("#FCFFFF", "#DDE8F4");
        dict["ButtonFaceHover"] = V("#FFFFFF", "#E7F1FB");
        dict["ButtonFacePressed"] = V("#D6E4F0", "#EEF6FD");
        dict["ButtonBorder"] = B("#A0B9D3");
        dict["SelectionBg"] = B("#CDE8FF");
        dict["SelectionHoverBg"] = B("#E3F1FF");
        dict["PrimaryFace"] = V("#5FB3EA", "#2C7BB8");
    }

    private static SolidColorBrush B(string hex) => new(Color.Parse(hex));

    private static LinearGradientBrush V(string top, string bottom) => new()
    {
        StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
        EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
        GradientStops =
        {
            new GradientStop(Color.Parse(top), 0),
            new GradientStop(Color.Parse(bottom), 1)
        }
    };

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
