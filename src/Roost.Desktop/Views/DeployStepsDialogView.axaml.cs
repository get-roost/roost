using System.Collections.Specialized;
using Avalonia.Controls;
using Avalonia.Threading;
using Roost.Desktop.ViewModels;

namespace Roost.Desktop.Views;

/// <summary>Como la salida en vivo: el scroll sigue a la última línea mientras el usuario no suba a leer algo.</summary>
public partial class DeployStepsDialogView : UserControl
{
    private DeployStepsDialogViewModel? _viewModel;
    private bool _follow = true;

    public DeployStepsDialogView()
    {
        InitializeComponent();
        OutputScroll.ScrollChanged += (_, e) =>
        {
            // Si solo ha crecido el contenido (ya medido), se baja hasta el final si se estaba siguiendo.
            if (e.OffsetDelta.Y == 0 && e.ExtentDelta.Y != 0)
            {
                if (_follow) OutputScroll.ScrollToEnd();
                return;
            }
            _follow = OutputScroll.Offset.Y >= OutputScroll.Extent.Height - OutputScroll.Viewport.Height - 8;
        };
        DataContextChanged += (_, _) =>
        {
            if (_viewModel is not null) _viewModel.Lines.CollectionChanged -= OnLinesChanged;
            _viewModel = DataContext as DeployStepsDialogViewModel;
            if (_viewModel is not null) _viewModel.Lines.CollectionChanged += OnLinesChanged;
            _follow = true;
        };
    }

    private void OnLinesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_follow) Dispatcher.UIThread.Post(() => OutputScroll.ScrollToEnd(), DispatcherPriority.Background);
    }
}
