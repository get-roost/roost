using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Roost.Core;
using Roost.Core.Config;
using Roost.Core.Platform;
using Roost.Core.Shims;
using Roost.Desktop.Controls;
using Roost.Desktop.Services;

namespace Roost.Desktop.ViewModels;

/// <summary>
/// Lo que deja un cambio de raíz para la app que se abre después (en <c>state/</c> de la nueva):
/// arrancar el stack si estaba en marcha y contarlo.
/// </summary>
public sealed record RootChangeMarker(string From, string To, bool Moved, bool StackWasRunning, string? Retired)
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public static string FilePath(RoostPaths paths) => Path.Combine(paths.State, "root-changed.json");

    public void Save(RoostPaths paths)
    {
        Directory.CreateDirectory(paths.State);
        File.WriteAllText(FilePath(paths), JsonSerializer.Serialize(this, Json));
    }

    /// <summary>El marcador, que se borra al leerlo; null si no hay (o no se entiende).</summary>
    public static RootChangeMarker? Take(RoostPaths paths)
    {
        try
        {
            var file = FilePath(paths);
            if (!File.Exists(file)) return null;
            var marker = JsonSerializer.Deserialize<RootChangeMarker>(File.ReadAllText(file), Json);
            File.Delete(file);
            return marker;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }
}

/// <summary>
/// J2, Ajustes › Rutas › «Cambiar…»: otra raíz para Roost. Dos maneras:
/// <list type="bullet">
/// <item><b>Mover lo que hay</b>: se para todo, se copia a la nueva, se comprueba fichero a fichero
/// (<see cref="RootMover"/>), se apunta en roost.toml (<c>[paths] root</c>) y la de antes se queda
/// renombrada como <c>…antigua-&lt;fecha&gt;</c>: Roost nunca la borra.</item>
/// <item><b>Empezar de cero en la nueva</b>: solo se apunta; la de antes se queda como está y se
/// puede volver a ella. Si la nueva ya es una Roost, se usa la que hay.</item>
/// </list>
/// Al acabar, la app se vuelve a abrir (todo lo que tiene abierto apunta a la raíz de antes) y,
/// si el stack estaba en marcha, lo arranca en la nueva.
/// </summary>
public sealed partial class ChangeRootDialogViewModel : DialogViewModel
{
    private static readonly CultureInfo Es = CultureInfo.GetCultureInfo("es-ES");
    private readonly MainViewModel _main;
    private CancellationTokenSource? _measure;

    public ChangeRootDialogViewModel(MainViewModel main)
    {
        _main = main;
        From = main.App.Paths.Root;
        Target = "";
        _ = MeasureAsync();
    }

    public override bool Wide => true;

    public override bool CanDismiss => !Working;

    public string From { get; }

    [ObservableProperty] public partial string Target { get; set; }
    [ObservableProperty] public partial bool Move { get; set; } = true;
    [ObservableProperty] public partial bool Fresh { get; set; }
    [ObservableProperty] public partial string? Problem { get; private set; }
    [ObservableProperty] public partial string? Info { get; private set; }
    [ObservableProperty] public partial string SizeText { get; private set; } = "Mirando cuánto ocupa…";
    [ObservableProperty] public partial bool Working { get; private set; }
    [ObservableProperty] public partial string StepText { get; private set; } = "";
    [ObservableProperty] public partial double ProgressValue { get; private set; }
    [ObservableProperty] public partial bool Done { get; private set; }
    [ObservableProperty] public partial string? ResultText { get; private set; }
    [ObservableProperty] public partial string? Error { get; private set; }

    /// <summary>Lo que se mide de la raíz de ahora (para avisar de cuánto se va a copiar).</summary>
    public RootSize? Size { get; private set; }

    public bool HasProblem => Problem is not null;
    public bool HasInfo => Info is not null;
    public bool HasError => Error is not null;
    public bool Editing => !Working && !Done;
    public bool CanStart => Editing && Problem is null && Target.Length > 0;
    public bool StackRunning => _main.Stack.IsDaemonUp;

    public string StartLabel => Move ? StackRunning ? "Parar todo y mover" : "Mover"
        : StackRunning ? "Parar todo y cambiar" : "Cambiar";

    /// <summary>Lo que se mueve con la raíz, para el aviso.</summary>
    public string WhatMovesText =>
        $"En la raíz van los PHP y las bases de datos instalados y sus datos (data\\), la CA local y los certificados de los sitios (certs\\), la configuración generada (etc\\), los registros y la carpeta de proyectos (www\\). "
        + "Los proyectos que estén fuera de la www no se mueven.";

    partial void OnTargetChanged(string value) => Validate();

    partial void OnMoveChanged(bool value)
    {
        if (value) Fresh = false;
        else if (!Fresh) Fresh = true;
        Validate();
    }

    partial void OnFreshChanged(bool value)
    {
        if (value) Move = false;
        else if (!Move) Move = true;
        Validate();
    }

    partial void OnProblemChanged(string? value)
    {
        OnPropertyChanged(nameof(HasProblem));
        OnPropertyChanged(nameof(CanStart));
    }

    partial void OnInfoChanged(string? value) => OnPropertyChanged(nameof(HasInfo));
    partial void OnErrorChanged(string? value) => OnPropertyChanged(nameof(HasError));

    partial void OnWorkingChanged(bool value) => NotifyState();
    partial void OnDoneChanged(bool value) => NotifyState();

    private void NotifyState()
    {
        OnPropertyChanged(nameof(Editing));
        OnPropertyChanged(nameof(CanStart));
        OnPropertyChanged(nameof(CanDismiss));
    }

    private void Validate()
    {
        var (problem, info) = Check(From, Target, Move, Directory.Exists, dir => Directory.EnumerateFileSystemEntries(dir).Any(), LooksLikeRoost);
        Problem = Target.Trim().Length == 0 ? null : problem;
        Info = info;
        OnPropertyChanged(nameof(CanStart));
        OnPropertyChanged(nameof(StartLabel));
    }

    /// <summary>Una carpeta que ya es una raíz de Roost (tiene bin, etc o state).</summary>
    public static bool LooksLikeRoost(string dir) =>
        new[] { "bin", "etc", "state" }.Any(sub => Directory.Exists(Path.Combine(dir, sub)));

    /// <summary>
    /// PURO (con el disco por delegados): por qué no vale <paramref name="target"/> como raíz nueva,
    /// o null; y una nota si vale pero conviene saber algo (ya hay una Roost ahí).
    /// </summary>
    public static (string? Problem, string? Info) Check(string from, string target, bool move,
        Func<string, bool> exists, Func<string, bool> hasEntries, Func<string, bool> looksLikeRoost)
    {
        target = target.Trim();
        if (target.Length == 0) return ("Elige una carpeta.", null);
        if (target.StartsWith(@"\\", StringComparison.Ordinal)) return ("Tiene que estar en un disco de este equipo, no en una carpeta de red.", null);
        if (!Path.IsPathFullyQualified(target)) return (@"Tiene que ser una ruta completa, como D:\roost.", null);
        if (target.Any(c => c > 127 || char.IsWhiteSpace(c) || !(char.IsAsciiLetterOrDigit(c) || c is '\\' or ':' or '-' or '_' or '.')))
            return (@"Sin espacios, tildes ni símbolos: nginx, PHP y MySQL se llevan mal con ellos (por ejemplo, D:\roost o D:\dev\roost).", null);
        var full = Path.GetFullPath(target).TrimEnd('\\');
        if (Path.GetPathRoot(full)?.TrimEnd('\\') == full) return (@"Un disco entero no: una carpeta dentro (D:\roost).", null);
        var source = Path.GetFullPath(from).TrimEnd('\\');
        if (string.Equals(full, source, StringComparison.OrdinalIgnoreCase)) return ("Es la que ya usa Roost.", null);
        if (full.StartsWith(source + "\\", StringComparison.OrdinalIgnoreCase) || source.StartsWith(full + "\\", StringComparison.OrdinalIgnoreCase))
            return ("No puede estar dentro de la de ahora, ni al revés.", null);
        foreach (var system in new[] { Environment.GetFolderPath(Environment.SpecialFolder.Windows), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) })
            if (system.Length > 0 && (string.Equals(full, system, StringComparison.OrdinalIgnoreCase) || full.StartsWith(system.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)))
                return ("Ni en Windows ni en Archivos de programa (ROOST.md §6: Roost escribe en su raíz).", null);

        if (exists(full) && hasEntries(full))
        {
            if (move) return ("Para mover, la carpeta tiene que estar vacía o no existir.", null);
            if (!looksLikeRoost(full)) return ("Tiene cosas que no son de Roost: elige una vacía o que no exista.", null);
            return (null, "Ahí ya hay una Roost: se usará la que hay (sus PHP, sus datos y su CA).");
        }
        return (null, move ? null : "Roost empezará de cero ahí: habrá que instalar PHP y la base de datos, y confiar en una CA nueva. La raíz de ahora se queda como está y puedes volver a ella.");
    }

    private async Task MeasureAsync()
    {
        _measure?.Cancel();
        var cts = _measure = new CancellationTokenSource();
        try
        {
            var skip = Skip(_main.App.Paths);
            Size = await Task.Run(() => RootMover.Measure(From, skip, cts.Token), cts.Token);
            SizeText = $"Ahora ocupa {Bytes(Size.Bytes)} en {Size.Files.ToString("N0", Es)} ficheros"
                       + (Size.Links.Count > 0 ? $", con {Size.Links.Count} enlace{(Size.Links.Count == 1 ? "" : "s")} (se recrean; lo de detrás no se copia)." : ".");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            SizeText = Directory.Exists(From) ? "No se ha podido medir lo que ocupa." : "La raíz de ahora aún está vacía.";
        }
    }

    /// <summary>Lo que vive dentro de la raíz pero no es de ella (con ROOST_HOME: el roost.toml y la caché).</summary>
    public static IReadOnlyCollection<string> Skip(RoostPaths paths)
    {
        var skip = new List<string>();
        foreach (var path in new[] { paths.ConfigFile, paths.Cache })
        {
            var parent = Path.GetDirectoryName(Path.GetFullPath(path).TrimEnd('\\'));
            if (parent is not null && RoostPaths.SameFolder(parent, paths.Root)) skip.Add(Path.GetFileName(path.TrimEnd('\\')));
        }
        return skip;
    }

    public static string Bytes(long bytes) => bytes switch
    {
        < 1024 * 1024 => string.Format(Es, "{0:0} KB", bytes / 1024.0),
        < 1024L * 1024 * 1024 => string.Format(Es, "{0:0.0} MB", bytes / 1048576.0),
        _ => string.Format(Es, "{0:0.0} GB", bytes / 1073741824.0),
    };

    [RelayCommand]
    private async Task PickAsync()
    {
        if (await _main.PickFolderAsync("La carpeta raíz de Roost") is { } folder)
            Target = Move && Directory.Exists(folder) && Directory.EnumerateFileSystemEntries(folder).Any() && !LooksLikeRoost(folder)
                ? Path.Combine(folder, "roost")   // se elige dónde, y dentro va su carpeta
                : folder;
    }

    /// <summary>Hace el cambio. Si algo falla antes de apuntar la raíz nueva, todo sigue como estaba.</summary>
    [RelayCommand]
    private async Task StartAsync()
    {
        if (!CanStart) return;
        var paths = _main.App.Paths;
        var target = Path.GetFullPath(Target.Trim()).TrimEnd('\\');
        var move = Move;
        var wasRunning = _main.Stack.IsRunning;
        Working = true;
        Error = null;
        IDisposable? pause = null;
        var pointed = false;
        try
        {
            AppLog.Info($"Cambiando la raíz: {From} -> {target} ({(move ? "mover" : "empezar de cero")})");
            await StopEverythingAsync();

            pause = AppLog.Pause();   // logs/ se copia y se renombra: nadie escribe dentro
            if (move)
            {
                var skip = Skip(paths);
                var last = Stopwatch.StartNew();
                var progress = new Progress<RootMoveProgress>(p =>
                {
                    if (last.ElapsedMilliseconds < 100 && p.Done < p.Total) return;
                    last.Restart();
                    ProgressValue = p.Total == 0 ? 100 : 100.0 * p.Done / p.Total;
                    StepText = p.Step switch
                    {
                        RootMoveStep.Copying => $"Copiando… {Bytes(p.Done)} de {Bytes(p.Total)}",
                        RootMoveStep.Verifying => $"Comprobando la copia… {Bytes(p.Done)} de {Bytes(p.Total)}",
                        _ => "Dejándola en su sitio…",
                    };
                });
                StepText = "Copiando…";
                await Task.Run(() => RootMover.Copy(From, target, skip, progress));
            }
            else
            {
                Directory.CreateDirectory(target);
            }

            // Desde aquí, Roost usa la nueva.
            RoostConfig.SetRoot(paths.ConfigFile, RoostPaths.SameFolder(target, RoostPaths.HomeRoot()) ? null : target);
            pointed = true;
            // El roost.toml y la caché se quedan donde estaban (con ROOST_HOME, en su carpeta).
            var moved = new RoostPaths(target, paths.Cache, Path.GetDirectoryName(Path.GetFullPath(paths.ConfigFile)));
            var sites = move ? RootMover.Rebase(moved, From, target) : 0;
            FixShims(paths, moved, move);

            string? retired = null;
            var canRetire = Skip(paths).Count == 0;
            if (move && canRetire) retired = RootMover.Retire(From, DateTime.Now);
            // Empezando de cero no hay PHP: el stack solo se arranca si la nueva ya era una Roost.
            new RootChangeMarker(From, target, move, wasRunning && (move || LooksLikeRoost(target)), retired).Save(moved);

            ResultText = move
                ? $"Roost ya usa {target}{(sites > 0 ? $" ({sites} sitio{(sites == 1 ? "" : "s")} de la www con su carpeta nueva)" : "")}. "
                  + (retired is not null ? $"La de antes se queda como {retired}: bórrala tú cuando hayas comprobado que todo va bien."
                     : canRetire ? $"La de antes sigue en {From} (algo la tiene abierta): bórrala tú cuando no la use nada."
                     : $"La de antes sigue en {From}, donde también están el roost.toml y la caché: borra a mano lo que ya no quieras.")
                : $"Roost usará {target}. La de antes sigue en {From}, tal cual: puedes volver a ella desde aquí.";
            ResultText += wasRunning ? " Roost se vuelve a abrir y arranca el stack en la nueva." : " Roost se vuelve a abrir con la nueva.";
            AppLog.Info($"Raíz cambiada a {target}{(retired is not null ? $"; la de antes, en {retired}" : "")}");
            Done = true;
        }
        catch (Exception ex) when (ex is RoostException or IOException or UnauthorizedAccessException or HttpRequestException)
        {
            pause?.Dispose();
            pause = null;
            Error = pointed
                ? $"Roost ya apunta a {target}, pero algo no ha terminado: {ex.Message}"
                : $"No se ha cambiado nada (la raíz sigue en {From}): {ex.Message}";
            AppLog.Error("No se pudo cambiar la raíz", ex);
            if (!pointed && wasRunning) await _main.StartStackAsync();   // como estaba
        }
        finally
        {
            Working = false;
            // Tras cambiar, la app sigue apuntando a la raíz de antes hasta que se vuelve a abrir: no escribe más en ella.
            if (Done) AppLog.Stop();
            else pause?.Dispose();
        }
    }

    /// <summary>Para el stack y roostd, y espera a que el proceso del daemon salga (tiene abierto roostd.log).</summary>
    private async Task StopEverythingAsync()
    {
        if (await _main.App.Daemon.TryStatusAsync() is not { } status) return;
        StepText = "Parando el stack y roostd…";
        await _main.App.Daemon.DownAsync();
        try
        {
            using var daemon = Process.GetProcessById(status.DaemonPid);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            await daemon.WaitForExitAsync(timeout.Token);
        }
        catch (ArgumentException) { /* ya no está */ }
        catch (OperationCanceledException) { throw new RoostException("roostd no se ha parado en 20 s: páralo (roost down) y vuelve a intentarlo."); }
        await _main.Stack.RefreshAsync();
    }

    /// <summary>
    /// Si los shims de la raíz de antes estaban en el PATH del usuario: al mover, se cambian por los de
    /// la nueva; al empezar de cero, se quitan (la nueva aún no tiene PHP: se vuelven a poner en Ajustes).
    /// </summary>
    private static void FixShims(RoostPaths before, RoostPaths after, bool move)
    {
        try
        {
            var old = new ShimInstaller(before).Directory;
            if (!UserPath.Contains(UserPath.Read(), old)) return;
            UserPath.Remove(old);
            var now = new ShimInstaller(after);
            if (move && now.IsInstalled) UserPath.AddFirst(now.Directory);
            AppLog.Info($"PATH del usuario: {old} quitado{(move && now.IsInstalled ? $", {now.Directory} puesto" : "")}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            AppLog.Warn($"No se pudieron cambiar los shims del PATH: {ex.Message}");
        }
    }

    /// <summary>«Reiniciar Roost»: cierra este diálogo para que Ajustes vuelva a abrir la app.</summary>
    [RelayCommand]
    private void Finish() => Close(Done ? true : null);
}
