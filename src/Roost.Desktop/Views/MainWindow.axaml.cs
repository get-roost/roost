using Avalonia.Controls;
using Avalonia.Input;
using Roost.Desktop.Controls;
using Roost.Desktop.ViewModels;

namespace Roost.Desktop.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is not MainViewModel main) return;
            main.Clipboard = Clipboard;
            main.StorageProvider = StorageProvider;
            // Las vistas de las pantallas de extensiones, como las DataTemplates de las de Roost.
            foreach (var page in main.ExtensionPages)
            {
                var create = page.CreateView;
                DataTemplates.Add(new Avalonia.Controls.Templates.FuncDataTemplate(page.Page.GetType(), (_, _) => create(), supportsRecycling: true));
            }
        };
        // Pulsar fuera del diálogo lo cierra, como Escape (salvo los que no se pueden dejar a medias).
        Shade.PointerPressed += (_, _) => (DataContext as MainViewModel)?.Dialog?.DismissCommand.Execute(null);

        // Botones normales en todos los SO (ver la barra de título en MainWindow.axaml). Cerrar
        // pasa por Closing: con la preferencia, la ventana se queda en la bandeja.
        MinimizeButton.Click += (_, _) => WindowState = WindowState.Minimized;
        MaximizeButton.Click += (_, _) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        CloseButton.Click += (_, _) => Close();
        PropertyChanged += (_, e) =>
        {
            if (e.Property != WindowStateProperty) return;
            var maximized = WindowState == WindowState.Maximized;
            MaximizeButton.Content = new Icon { Data = (Avalonia.Media.Geometry?)this.FindResource(maximized ? "IcRestoreWindow" : "IcMaximize"), Size = 13 };
            ToolTip.SetTip(MaximizeButton, maximized ? "Restaurar" : "Maximizar");
        };

        // Ajustes y sistema (J5): los atajos de la ventana. Van aquí y no en el XAML para no tocar
        // sus KeyBindings (Escape, de los diálogos). Los comandos no hacen nada con un diálogo abierto.
        DataContextChanged += (_, _) =>
        {
            if (DataContext is not MainViewModel main) return;
            foreach (var old in KeyBindings.Where(k => k.Gesture is { } g && Shortcuts.Contains(g)).ToList()) KeyBindings.Remove(old);
            KeyBindings.Add(new KeyBinding { Gesture = Shortcuts[0], Command = main.NewSiteShortcutCommand });
            KeyBindings.Add(new KeyBinding { Gesture = Shortcuts[1], Command = main.RefreshPageCommand });
            KeyBindings.Add(new KeyBinding { Gesture = Shortcuts[2], Command = main.LogsShortcutCommand });
            KeyBindings.Add(new KeyBinding { Gesture = Shortcuts[3], Command = main.SettingsShortcutCommand });
        };
    }

    /// <summary>Ctrl+N, F5, Ctrl+L y Ctrl+, (J5).</summary>
    internal static readonly KeyGesture[] Shortcuts =
    [
        new(Key.N, KeyModifiers.Control),
        new(Key.F5),
        new(Key.L, KeyModifiers.Control),
        new(Key.OemComma, KeyModifiers.Control),
    ];
}
