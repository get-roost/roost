using System.Collections.Specialized;
using Avalonia.Controls;
using Avalonia.Threading;
using Roost.Desktop.ViewModels;

namespace Roost.Desktop.Views;

/// <summary>
/// Como la del diálogo de salida en vivo: la salida sigue a la última línea mientras el usuario no
/// suba a leer algo; si vuelve al final, sigue otra vez.
/// </summary>
public partial class NodeDevServerDialogView : UserControl
{
    private NodeDevServerDialogViewModel? _viewModel;
    private bool _follow = true;

    public NodeDevServerDialogView()
    {
        InitializeComponent();
        OutputScroll.ScrollChanged += (_, e) =>
        {
            if (e.OffsetDelta.Y == 0 && e.ExtentDelta.Y != 0) return;
            _follow = OutputScroll.Offset.Y >= OutputScroll.Extent.Height - OutputScroll.Viewport.Height - 8;
        };
        DataContextChanged += (_, _) =>
        {
            if (_viewModel is not null) _viewModel.Session.Lines.CollectionChanged -= OnLinesChanged;
            _viewModel = DataContext as NodeDevServerDialogViewModel;
            if (_viewModel is not null) _viewModel.Session.Lines.CollectionChanged += OnLinesChanged;
            _follow = true;
            ScrollToEnd();
        };
        DetachedFromVisualTree += (_, _) =>
        {
            // La sesión vive más que la ventana: que no se quede enganchada a una vista que ya no está.
            if (_viewModel is not null) _viewModel.Session.Lines.CollectionChanged -= OnLinesChanged;
        };
    }

    private void OnLinesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_follow) ScrollToEnd();
    }

    private void ScrollToEnd() => Dispatcher.UIThread.Post(() => OutputScroll.ScrollToEnd(), DispatcherPriority.Background);
}
