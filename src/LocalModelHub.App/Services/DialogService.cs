using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;

namespace LocalModelHub.App.Services;

public static class DialogService
{
    private const double MessageWidth = 460;
    private const double MessageMaxHeight = 340;

    public static async Task<bool> ConfirmAsync(string title, string message, string confirmText = "确认", string cancelText = "取消")
    {
        var owner = App.MainWindow;
        if (owner is null) return false;

        var result = false;
        var window = CreateWindow(title, message, out var buttons);

        var confirmButton = buttons[0];
        var cancelButton = buttons[1];
        confirmButton.Content = confirmText;
        cancelButton.Content = cancelText;
        confirmButton.Click += (_, _) => { result = true; window.Close(); };
        cancelButton.Click += (_, _) => window.Close();

        await window.ShowDialog(owner);
        return result;
    }

    public static async Task AlertAsync(string title, string message)
    {
        var owner = App.MainWindow;
        if (owner is null) return;

        var window = CreateWindow(title, message, out var buttons);
        buttons[0].Content = "知道了";
        buttons[0].Click += (_, _) => window.Close();
        buttons[1].IsVisible = false;
        await window.ShowDialog(owner);
    }

    /// <summary>构造消息区域：按固定宽度换行，超长内容改为纵向滚动，避免撑破对话框或横向溢出。</summary>
    public static Control CreateMessageView(string message)
    {
        var text = new TextBlock
        {
            Text = message,
            TextWrapping = TextWrapping.Wrap,
            Width = MessageWidth,
            Foreground = Brush("#94A3B8"),
            LineHeight = 21,
            FontSize = 13
        };

        return new ScrollViewer
        {
            Content = text,
            MaxHeight = MessageMaxHeight,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        };
    }

    public static Window CreateWindow(string title, string message, out Button[] buttons)
    {
        var panel = new StackPanel { Spacing = 16, Margin = new Avalonia.Thickness(22, 18) };
        panel.Children.Add(CreateMessageView(message));

        var buttonRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 10
        };

        var cancel = new Button
        {
            Content = "取消",
            Background = Brush("#16233A"),
            Foreground = Brush("#CBD5E1"),
            Padding = new Avalonia.Thickness(18, 8),
            CornerRadius = new Avalonia.CornerRadius(8)
        };
        var confirm = new Button
        {
            Content = "确认",
            Background = Brush("#4F8CFF"),
            Foreground = Brushes.White,
            Padding = new Avalonia.Thickness(18, 8),
            CornerRadius = new Avalonia.CornerRadius(8)
        };
        buttonRow.Children.Add(cancel);
        buttonRow.Children.Add(confirm);
        panel.Children.Add(buttonRow);

        buttons = [confirm, cancel];

        return new Window
        {
            Title = title,
            Content = panel,
            Width = MessageWidth + 44,
            MaxHeight = 520,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = Brush("#101827"),
            ShowInTaskbar = false
        };
    }

    private static IBrush Brush(string hex) => new SolidColorBrush(Color.Parse(hex));
}
