using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using LocalModelHub.App.ViewModels;

namespace LocalModelHub.App.Views;

public partial class ChatView : UserControl
{
    private const double MessageColumnMax = 720;

    private ChatViewModel? _viewModel;

    public ChatView()
    {
        InitializeComponent();
        MessagesScroll.LayoutUpdated += (_, _) => UpdateMessageColumnWidth();
        DataContextChanged += (_, _) => BindViewModel();
    }

    /// <summary>消息列居中：可用宽度不足时占满，超过 720 时固定 720 并居中。</summary>
    private void UpdateMessageColumnWidth()
    {
        var available = MessagesScroll.Viewport.Width;
        if (available <= 0) available = MessagesScroll.Bounds.Width;
        if (available <= 0) return;

        var target = Math.Min(MessageColumnMax, available);
        if (Math.Abs(MessagesList.Width - target) > 0.5) MessagesList.Width = target;
    }

    private void BindViewModel()
    {
        if (_viewModel is not null) _viewModel.ScrollRequested -= ScrollToEnd;
        _viewModel = DataContext as ChatViewModel;
        if (_viewModel is not null) _viewModel.ScrollRequested += ScrollToEnd;
    }

    private void ScrollToEnd() =>
        Dispatcher.UIThread.Post(() => MessagesScroll.ScrollToEnd(), DispatcherPriority.Background);

    private void OnInputKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || e.KeyModifiers.HasFlag(KeyModifiers.Shift)) return;
        e.Handled = true;
        if (_viewModel?.SendCommand.CanExecute(null) == true)
        {
            _viewModel.SendCommand.Execute(null);
        }
    }
}
