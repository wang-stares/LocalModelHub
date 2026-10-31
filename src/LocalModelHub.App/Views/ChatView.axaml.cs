using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using LocalModelHub.App.ViewModels;

namespace LocalModelHub.App.Views;

public partial class ChatView : UserControl
{
    private ChatViewModel? _viewModel;

    public ChatView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => BindViewModel();
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
