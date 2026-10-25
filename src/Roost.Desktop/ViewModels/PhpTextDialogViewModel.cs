using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Roost.Core;
using Roost.Core.Install;
using Roost.Core.Scanning;
using Roost.Core.Stack;
using Roost.Desktop.Controls;

namespace Roost.Desktop.ViewModels;

/// <summary>
/// Un texto largo de solo lectura con búsqueda: el php.ini que genera Roost, phpinfo (<c>php -i</c>)
/// o los módulos (<c>php -m</c>) de una instalación. Lo carga fuera del hilo de la UI (el texto
/// tarda lo que tarde php.exe) y filtra las líneas según se escribe. Con la ruta del fichero y
/// «Abrir carpeta» cuando la hay.
/// </summary>
public sealed partial class PhpTextDialogViewModel : DialogViewModel
{
    private readonly Func<CancellationToken, Task<string>> _load;
    private readonly Func<string, Task>? _copy;
    private readonly Action<string>? _openFolder;
    private readonly CancellationTokenSource _cancel = new();
    private IReadOnlyList<string> _all = [];

    /// <param name="load">Lo que da el texto; lo que lanza (<see cref="RoostException"/>) se enseña como error.</param>
    /// <param name="copy">«Copiar» (el portapapeles de la ventana); null lo oculta.</param>
    /// <param name="openFolder">«Abrir carpeta» con la carpeta de <paramref name="path"/>; null lo oculta.</param>
    public PhpTextDialogViewModel(string title, string subtitle, string? path, Func<CancellationToken, Task<string>> load,
        Func<string, Task>? copy = null, Action<string>? openFolder = null)
    {
        Title = title;
        Subtitle = subtitle;
        Path = path;
        _load = load;
        _copy = copy;
        _openFolder = openFolder;
    }

    /// <summary>phpinfo o los módulos: <c>php.exe -i|-m</c> de la instalación, con el php.ini y las conf.d de Roost.</summary>
    public static PhpTextDialogViewModel ForPhp(InstalledPhp php, PhpConfigurator configurator, ICommandRunner runner, bool modules,
        Func<string, Task>? copy = null)
    {
        var argument = modules ? "-m" : "-i";
        var ini = configurator.Planner.PhpIniPath(php);
        return new PhpTextDialogViewModel(
            modules ? $"Módulos de PHP {php.Version?.ToString() ?? php.Name}" : $"phpinfo de PHP {php.Version?.ToString() ?? php.Name}",
            $"php {argument} con el php.ini de Roost y sus conf.d · {php.Name}",
            null,
            async cancel =>
            {
                var spec = await Task.Run(() => configurator.Command(php, argument), cancel);
                var result = await Task.Run(() => runner.RunAsync(spec, null, cancel), cancel);
                if (result.TimedOut) throw new RoostException($"php {argument} no ha contestado en {spec.Timeout?.TotalSeconds:0} s.");
                if (result.Stdout.Trim().Length == 0)
                    throw new RoostException($"php {argument} no ha dado nada (código {result.ExitCode}). {result.Stderr.Trim()}");
                var warnings = PhpConfigurator.StartupErrors(result.Stdout + "\n" + result.Stderr);
                var text = result.Stdout.Replace("\r\n", "\n").TrimEnd();
                return warnings.Count == 0 ? text : string.Join('\n', warnings.Select(w => "Aviso al arrancar: " + w)) + "\n\n" + text;
            },
            copy)
        {
            Footnote = $"Lo mismo que ve la terminal (PHPRC={System.IO.Path.GetDirectoryName(ini)}). php-cgi lee el mismo php.ini.",
        };
    }

    public override bool Wide => true;

    /// <summary>Mientras carga, Escape no lo cierra a medias: «Cerrar» cancela.</summary>
    public override bool CanDismiss => true;

    public string Title { get; }

    public string Subtitle { get; }

    /// <summary>El fichero que se enseña (el php.ini); null con la salida de una orden.</summary>
    public string? Path { get; }

    public bool HasPath => Path is not null;

    public bool CanOpenFolder => Path is not null && _openFolder is not null;

    public bool CanCopy => _copy is not null;

    /// <summary>Una línea fija al pie (de dónde sale lo que se ve).</summary>
    public string? Footnote { get; init; }

    public bool HasFootnote => Footnote is not null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? Error { get; private set; }

    public bool HasError => Error is not null;

    [ObservableProperty] public partial bool Loading { get; private set; } = true;

    [ObservableProperty] public partial IReadOnlyList<LogLine> Lines { get; private set; } = [];

    [ObservableProperty] public partial string CountText { get; private set; } = "";

    [ObservableProperty]
    public partial string Query { get; set; } = "";

    partial void OnQueryChanged(string value) => Filter();

    /// <summary>Todo el texto (lo que copia «Copiar»).</summary>
    public string Text => string.Join(Environment.NewLine, _all);

    /// <summary>Carga el texto. Lanzarlo una vez, antes o después de enseñar el diálogo.</summary>
    public async Task LoadAsync()
    {
        try
        {
            var text = await _load(_cancel.Token);
            _all = text.Replace("\r\n", "\n").Split('\n');
            Error = null;
        }
        catch (OperationCanceledException)
        {
            _all = [];
        }
        catch (Exception ex) when (ex is RoostException or IOException or UnauthorizedAccessException)
        {
            _all = [];
            Error = ex.Message;
        }
        finally
        {
            Loading = false;
        }
        Filter();
    }

    private void Filter()
    {
        var q = Query.Trim();
        var shown = q.Length == 0 ? _all : _all.Where(l => l.Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();
        Lines = shown.Select(l => new LogLine(l, LevelOf(l))).ToList();
        CountText = Loading ? "Cargando…"
            : q.Length == 0 ? $"{_all.Count.ToString("N0", Roost.Desktop.Services.Es.Culture)} líneas"
            : $"{shown.Count.ToString("N0", Roost.Desktop.Services.Es.Culture)} de {_all.Count.ToString("N0", Roost.Desktop.Services.Es.Culture)} líneas con «{q}»";
    }

    // Los avisos de arranque en ámbar; los comentarios del php.ini, apagados.
    private static LogLevel LevelOf(string line) =>
        line.StartsWith("Aviso al arrancar", StringComparison.Ordinal) ? LogLevel.Warn
        : line.TrimStart().StartsWith(';') ? LogLevel.Muted
        : LogLevel.Normal;

    [RelayCommand]
    private async Task CopyAsync()
    {
        if (_copy is not null) await _copy(Text);
    }

    [RelayCommand]
    private void OpenFolder()
    {
        if (Path is not null && System.IO.Path.GetDirectoryName(Path) is { } dir) _openFolder?.Invoke(dir);
    }

    [RelayCommand]
    private void Finish()
    {
        _cancel.Cancel();
        Close();
    }
}
