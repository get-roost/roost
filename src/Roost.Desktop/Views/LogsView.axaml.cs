using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Threading;
using Roost.Desktop.ViewModels;

namespace Roost.Desktop.Views;

public partial class LogsView : UserControl
{
    private LogsViewModel? _viewModel;

    public LogsView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (_viewModel is not null) _viewModel.PropertyChanged -= OnViewModelChanged;
            _viewModel = DataContext as LogsViewModel;
            if (_viewModel is not null) _viewModel.PropertyChanged += OnViewModelChanged;
            ScrollToEnd();
        };
    }

    // Con «Seguir», lo nuevo se ve sin tener que bajar a mano.
    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(LogsViewModel.Lines) && _viewModel?.Follow == true) ScrollToEnd();
    }

    private void ScrollToEnd() => Dispatcher.UIThread.Post(() => LogScroll.ScrollToEnd(), DispatcherPriority.Background);
}
