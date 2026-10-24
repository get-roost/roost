using System.Collections.Specialized;
using Avalonia.Controls;
using Avalonia.Threading;
using Roost.Desktop.ViewModels;

namespace Roost.Desktop.Views;

/// <summary>BD-2: la salida del cliente o del volcador sigue a la última línea.</summary>
public partial class DbTransferDialogView : UserControl
{
    private DbTransferDialogViewModel? _viewModel;

    public DbTransferDialogView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (_viewModel is not null) _viewModel.Lines.CollectionChanged -= OnLinesChanged;
            _viewModel = DataContext as DbTransferDialogViewModel;
            if (_viewModel is not null) _viewModel.Lines.CollectionChanged += OnLinesChanged;
        };
    }

    private void OnLinesChanged(object? sender, NotifyCollectionChangedEventArgs e) =>
        Dispatcher.UIThread.Post(() => OutputScroll.ScrollToEnd(), DispatcherPriority.Background);
}
