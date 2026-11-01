using Avalonia.Controls;
using Avalonia.Threading;
using LocalModelHub.App.ViewModels;

namespace LocalModelHub.App.Views;

public partial class PhoneView : UserControl
{
    private PhoneViewModel? _viewModel;

    public PhoneView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Attach();
    }

    private void Attach()
    {
        if (_viewModel is not null) _viewModel.MessageAdded -= ScrollToEnd;
        _viewModel = DataContext as PhoneViewModel;
        if (_viewModel is not null) _viewModel.MessageAdded += ScrollToEnd;
    }

    private void ScrollToEnd() =>
        Dispatcher.UIThread.Post(() => MessageScroll.ScrollToEnd(), DispatcherPriority.Background);
}
