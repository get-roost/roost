using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Roost.Desktop.Services;

namespace Roost.Desktop.ViewModels;

/// <summary>Una opción de los desplegables de Ajustes › Programas: lo que se guarda y lo que se lee.</summary>
public sealed record ProgramChoice(string Id, string Name)
{
    public override string ToString() => Name;
}

/// <summary>
/// Ajustes › Programas (J3 + C4): el editor, la terminal y el navegador con los que Roost abre las
/// cosas (<see cref="Shell"/>). Se buscan en segundo plano al entrar en Ajustes; lo elegido va a
/// <see cref="DesktopPrefs"/>.
/// </summary>
public sealed partial class SettingsViewModel
{
    [ObservableProperty] public partial IReadOnlyList<ProgramChoice> EditorChoices { get; private set; } = [];
    [ObservableProperty] public partial IReadOnlyList<ProgramChoice> TerminalChoices { get; private set; } = [];
    [ObservableProperty] public partial IReadOnlyList<ProgramChoice> BrowserChoices { get; private set; } = [];
    [ObservableProperty] public partial ProgramChoice? EditorChoice { get; set; }
    [ObservableProperty] public partial ProgramChoice? TerminalChoice { get; set; }
    [ObservableProperty] public partial ProgramChoice? BrowserChoice { get; set; }

    /// <summary>«Se encontraron: VS Code, PhpStorm» o qué hacer si no hay ninguno.</summary>
    [ObservableProperty] public partial string ProgramsHint { get; private set; } = "Buscando los programas del equipo…";

    private bool _loadingPrograms;

    partial void OnEditorChoiceChanged(ProgramChoice? value) => SavePrograms();
    partial void OnTerminalChoiceChanged(ProgramChoice? value) => SavePrograms();
    partial void OnBrowserChoiceChanged(ProgramChoice? value) => SavePrograms();

    private void SavePrograms()
    {
        if (_loadingPrograms || _loading || EditorChoice is null || TerminalChoice is null || BrowserChoice is null) return;
        var prefs = Main.App.Prefs;
        if (prefs.Editor == EditorChoice.Id && prefs.Terminal == TerminalChoice.Id && prefs.Browser == BrowserChoice.Id) return;
        Main.App.SavePrefs(prefs with { Editor = EditorChoice.Id, Terminal = TerminalChoice.Id, Browser = BrowserChoice.Id });
        AppLog.Info($"Programas: editor «{EditorChoice.Name}», terminal «{TerminalChoice.Name}», navegador «{BrowserChoice.Name}»");
    }

    /// <summary>Busca los programas (registro y rutas, fuera del hilo de la UI) y deja elegido lo de las preferencias.</summary>
    private async Task LoadProgramsAsync()
    {
        var (editors, terminals, browsers) = await Task.Run(() => (ShellApps.Editors(Shell.Probe), ShellApps.Terminals(Shell.Probe), ShellApps.Browsers(Shell.Probe)));
        var auto = editors.FirstOrDefault();
        var autoTerminal = ShellApps.AutoTerminal(terminals);
        var prefs = Main.App.Prefs;

        _loadingPrograms = true;
        try
        {
            EditorChoices =
            [
                new(ShellApps.Auto, auto is null ? "Automático (el programa asociado: no hay ningún editor)" : $"Automático ({auto.Name})"),
                new(ShellApps.AssociatedEditor, "El programa asociado de Windows"),
                .. editors.Select(e => new ProgramChoice(e.Id, e.Name)),
            ];
            TerminalChoices =
            [
                new(ShellApps.Auto, autoTerminal is null ? "Automática (PowerShell)" : $"Automática ({autoTerminal.Name})"),
                .. terminals.Select(t => new ProgramChoice(t.Id, t.Id == "wt" ? $"{t.Name} (con {autoTerminal?.Name ?? "PowerShell"})" : t.Name)),
            ];
            BrowserChoices =
            [
                new(ShellApps.Auto, "El predeterminado de Windows"),
                .. browsers.Select(b => new ProgramChoice(b.Id, b.Name)),
            ];
            // Lo guardado, si sigue en el equipo; si no, automático (y así lo usa Shell).
            EditorChoice = EditorChoices.FirstOrDefault(c => c.Id == prefs.Editor) ?? EditorChoices[0];
            TerminalChoice = TerminalChoices.FirstOrDefault(c => c.Id == prefs.Terminal) ?? TerminalChoices[0];
            BrowserChoice = BrowserChoices.FirstOrDefault(c => c.Id == prefs.Browser) ?? BrowserChoices[0];
        }
        finally
        {
            _loadingPrograms = false;
        }

        ProgramsHint = editors.Count == 0
            ? "No se ha encontrado VS Code, Cursor, PhpStorm ni Sublime Text: los ficheros se abren con su programa asociado y las carpetas, en el explorador."
            : $"Se abren con él las carpetas de los sitios, el php.ini, los vhost, los registros y el usuario.cnf. En el equipo: {string.Join(", ", editors.Select(e => e.Name))}.";
    }

    /// <summary>«Probar»: la carpeta www en el editor elegido.</summary>
    [RelayCommand]
    private Task TryEditorAsync() => Main.RunAsync("No se pudo abrir el editor", () =>
    {
        Shell.OpenInEditor(Paths.Www);
        return Task.CompletedTask;
    });

    /// <summary>«Probar»: una terminal en la www, como el botón Terminal de Inicio.</summary>
    [RelayCommand]
    private Task TryTerminalAsync() => Main.RunAsync("No se pudo abrir la terminal", () =>
    {
        Shell.OpenTerminal(Paths);
        return Task.CompletedTask;
    });

    /// <summary>«Probar»: localhost en el navegador elegido.</summary>
    [RelayCommand]
    private Task TryBrowserAsync() => Main.RunAsync("No se pudo abrir el navegador", () =>
    {
        Shell.OpenUrl(Main.Stack.Status?.Url ?? Main.Home.WebUrl);
        return Task.CompletedTask;
    });
}
