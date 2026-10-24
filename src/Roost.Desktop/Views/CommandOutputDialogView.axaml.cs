using System.Collections.Specialized;
using Avalonia.Controls;
using Avalonia.Threading;
using Roost.Desktop.ViewModels;

namespace Roost.Desktop.Views;

/// <summary>
/// La salida sigue a la última línea mientras el usuario no suba a leer algo; si vuelve al final,
/// sigue otra vez.
/// </summary>
public partial class CommandOutputDialogView : UserControl
{
    private CommandOutputDialogViewModel? _viewModel;
    private bool _follow = true;

    public CommandOutputDialogView()
    {
        InitializeComponent();
        OutputScroll.ScrollChanged += (_, e) =>
        {
            // Si solo ha crecido el contenido, no es que el usuario se haya movido.
            if (e.OffsetDelta.Y == 0 && e.ExtentDelta.Y != 0) return;
            _follow = OutputScroll.Offset.Y >= OutputScroll.Extent.Height - OutputScroll.Viewport.Height - 8;
        };
        DataContextChanged += (_, _) =>
        {
            if (_viewModel is not null) _viewModel.Lines.CollectionChanged -= OnLinesChanged;
            _viewModel = DataContext as CommandOutputDialogViewModel;
            if (_viewModel is not null) _viewModel.Lines.CollectionChanged += OnLinesChanged;
            _follow = true;
            ScrollToEnd();
        };
    }

    private void OnLinesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_follow) ScrollToEnd();
    }

    private void ScrollToEnd() => Dispatcher.UIThread.Post(() => OutputScroll.ScrollToEnd(), DispatcherPriority.Background);
}
