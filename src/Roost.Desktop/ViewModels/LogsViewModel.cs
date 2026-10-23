using System.Text;
using System.Text.RegularExpressions;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Roost.Core.Sites;
using Roost.Desktop.Controls;
using Roost.Desktop.Services;

namespace Roost.Desktop.ViewModels;

/// <summary>Un fichero de registro de la lista.</summary>
public sealed partial class LogFile(string title, string display, string path) : ObservableObject
{
    public string Title { get; } = title;
    public string Display { get; } = display;
    public string Path { get; } = path;

    [ObservableProperty] public partial string? Badge { get; set; }
    [ObservableProperty] public partial Tone BadgeTone { get; set; } = Tone.Neutral;
    [ObservableProperty] public partial bool IsSelected { get; set; }

    public bool HasBadge => Badge is not null;

    partial void OnBadgeChanged(string? value) => OnPropertyChanged(nameof(HasBadge));
}

public enum LogLevel
{
    Normal,
    Muted,
    Warn,
    Error,
}

/// <summary>Una línea del registro, con su color según lo que cuenta.</summary>
public sealed record LogLine(string Text, LogLevel Level)
{
    public bool IsError => Level == LogLevel.Error;
    public bool IsWarn => Level == LogLevel.Warn;
    public bool IsMuted => Level == LogLevel.Muted;
}

/// <summary>
/// Registros: lo que escriben roostd, nginx, cada PHP, la base de datos y roost-helper. Enseña
/// el final del fichero elegido, lo filtra y, con «Seguir», lo va leyendo según crece.
/// </summary>
public sealed partial class LogsViewModel : PageViewModel
{
    private const int TailLines = 500;
    private readonly DispatcherTimer _follow = new() { Interval = TimeSpan.FromSeconds(1) };
    private IReadOnlyList<LogLine> _all = [];
    private long _length = -1;

    public LogsViewModel(MainViewModel main) : base(main)
    {
        Subtitle = $"Lo que escriben Roost y cada servicio. Están en {main.App.Paths.Logs}.";
        _follow.Tick += (_, _) => Reload(onlyIfChanged: true);
    }

    public override string Title => "Registros";

    [ObservableProperty] public partial IReadOnlyList<LogFile> Files { get; private set; } = [];
    [ObservableProperty] public partial LogFile? Selected { get; private set; }
    [ObservableProperty] public partial IReadOnlyList<LogLine> Lines { get; private set; } = [];
    [ObservableProperty] public partial string Filter { get; set; } = "";
    [ObservableProperty] public partial bool Follow { get; set; } = true;
    [ObservableProperty] public partial string? Empty { get; private set; }

    public bool HasEmpty => Empty is not null;

    partial void OnEmptyChanged(string? value) => OnPropertyChanged(nameof(HasEmpty));
    partial void OnFilterChanged(string value) => ApplyFilter();

    partial void OnFollowChanged(bool value)
    {
        if (value && Main.Current == this) _follow.Start();
        else _follow.Stop();
    }

    public override Task ShownAsync()
    {
        BuildFiles();
        if (Selected is null || !Files.Contains(Selected)) Pick(Files.FirstOrDefault());
        else Reload(onlyIfChanged: false);
        if (Follow) _follow.Start();
        return Task.CompletedTask;
    }

    /// <summary>Elige un fichero por su nombre (desde otra pantalla: «Abrir» el log de nginx…).</summary>
    public Task SelectAsync(string fileName)
    {
        BuildFiles();
        Pick(Files.FirstOrDefault(f => f.Display.Equals(fileName, StringComparison.OrdinalIgnoreCase)) ?? Files.FirstOrDefault());
        return Task.CompletedTask;
    }

    /// <summary>Al salir de la pantalla deja de leer el fichero.</summary>
    public override void Hidden() => _follow.Stop();

    private void BuildFiles()
    {
        var logs = Main.App.Paths.Logs;
        var files = new List<LogFile>();
        if (Directory.Exists(logs))
        {
            foreach (var path in Directory.EnumerateFiles(logs, "*.log"))
            {
                var name = System.IO.Path.GetFileName(path);
                files.Add(new LogFile(TitleOf(name), name, path));
            }
        }
        var helper = HostsUpdater.HelperLog;
        if (File.Exists(helper)) files.Add(new LogFile("roost-helper (hosts)", @"%ProgramData%\Roost\roost-helper.log", helper));

        files = files.OrderBy(f => Order(f.Display)).ThenBy(f => f.Display, StringComparer.Ordinal).ToList();
        foreach (var file in files)
        {
            var (errors, warnings) = Count(ReadTail(file.Path, 64 * 1024));
            (file.Badge, file.BadgeTone) = errors > 0 ? ($"{errors} error{(errors == 1 ? "" : "es")}", Tone.Bad)
                : warnings > 0 ? ($"{warnings} aviso{(warnings == 1 ? "" : "s")}", Tone.Warn)
                : (null, Tone.Neutral);
        }

        // Conserva la selección si el fichero sigue ahí.
        var selected = Selected is null ? null : files.FirstOrDefault(f => f.Path == Selected.Path);
        Files = files;
        if (selected is not null)
        {
            selected.IsSelected = true;
            Selected = selected;
        }
        Empty = files.Count == 0 ? "Aún no hay registros: se crean al pulsar Iniciar todo." : null;
    }

    private static string TitleOf(string name)
    {
        var stem = System.IO.Path.GetFileNameWithoutExtension(name);
        if (stem == "roostd") return "Roost (daemon)";
        if (stem == "nginx-error") return "nginx: errores";
        if (stem == "nginx-access") return "nginx: accesos";
        if (stem == "nginx-stdout") return "nginx: salida";
        if (stem == "error") return "nginx: arranque";
        if (Regex.Match(stem, @"^php-(\d+\.\d+)-error$") is { Success: true } e) return $"PHP {e.Groups[1].Value}: errores";
        if (Regex.Match(stem, @"^php-(\d+\.\d+)-(\d+)$") is { Success: true } p) return $"PHP {p.Groups[1].Value}: php-cgi {p.Groups[2].Value}";
        if (Regex.Match(stem, @"^mariadb-(.+)$") is { Success: true } m) return $"MariaDB {m.Groups[1].Value}";
        if (Regex.Match(stem, @"^mysql-(.+)$") is { Success: true } y) return $"MySQL {y.Groups[1].Value}";
        return stem;
    }

    private static int Order(string name) => name switch
    {
        "roostd.log" => 0,
        "nginx-error.log" => 1,
        "nginx-access.log" => 2,
        _ when Regex.IsMatch(name, @"^php-\d+\.\d+-error\.log$") => 3,
        _ when name.StartsWith("mariadb-", StringComparison.Ordinal) || name.StartsWith("mysql-", StringComparison.Ordinal) => 4,
        _ when name.StartsWith('%') => 5,
        _ when name.StartsWith("php-", StringComparison.Ordinal) => 7,
        _ => 6,
    };

    private void Pick(LogFile? file)
    {
        foreach (var f in Files) f.IsSelected = f == file;
        Selected = file;
        _length = -1;
        Reload(onlyIfChanged: false);
    }

    private void Reload(bool onlyIfChanged)
    {
        if (Selected is not { } file)
        {
            _all = [];
            ApplyFilter();
            return;
        }

        long length;
        try { length = new FileInfo(file.Path).Length; } catch (IOException) { length = 0; }
        if (onlyIfChanged && length == _length) return;
        _length = length;

        var text = ReadTail(file.Path, 512 * 1024);
        var lines = text.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Length > 0).TakeLast(TailLines).ToList();
        _all = lines.Count == 0 ? [new LogLine("(vacío)", LogLevel.Muted)] : lines.Select(l => new LogLine(l, LevelOf(l))).ToList();
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        var filter = Filter.Trim();
        Lines = filter.Length == 0 ? _all : _all.Where(l => l.Text.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    /// <summary>El final del fichero (sin bloquearlo: los servicios siguen escribiendo).</summary>
    private static string ReadTail(string path, int maxBytes)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var start = Math.Max(0, stream.Length - maxBytes);
            stream.Seek(start, SeekOrigin.Begin);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var text = reader.ReadToEnd();
            // Si se ha empezado a mitad de línea, esa primera línea va incompleta: fuera.
            return start > 0 && text.IndexOf('\n') is var nl and >= 0 ? text[(nl + 1)..] : text;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return "";
        }
    }

    private static readonly Regex ErrorPattern = new(@"\[(error|emerg|crit|alert)\]|\bERROR\b|PHP (Fatal|Parse) error|\[ERROR\]|no arranca|up fallido", RegexOptions.Compiled);
    private static readonly Regex WarnPattern = new(@"\[warn\]|\bWarning\b|\[Warning\]|se cayo|cancelado|no se paro", RegexOptions.Compiled);
    private static readonly Regex MutedPattern = new(@"\[Note\]|\[notice\]|roostd en marcha", RegexOptions.Compiled);

    public static LogLevel LevelOf(string line) =>
        ErrorPattern.IsMatch(line) ? LogLevel.Error
        : WarnPattern.IsMatch(line) ? LogLevel.Warn
        : MutedPattern.IsMatch(line) ? LogLevel.Muted
        : LogLevel.Normal;

    private static (int Errors, int Warnings) Count(string text)
    {
        int errors = 0, warnings = 0;
        foreach (var line in text.Split('\n'))
        {
            switch (LevelOf(line))
            {
                case LogLevel.Error: errors++; break;
                case LogLevel.Warn: warnings++; break;
            }
        }
        return (errors, warnings);
    }

    // Acciones

    [RelayCommand]
    private void Choose(LogFile file) => Pick(file);

    [RelayCommand]
    private Task CopyAsync() => Main.CopyAsync(string.Join(Environment.NewLine, Lines.Select(l => l.Text)), $"Copiadas {Lines.Count} líneas");

    [RelayCommand]
    private Task OpenFolderAsync() => Main.RunAsync("No se pudo abrir la carpeta", () =>
    {
        Shell.OpenFolder(Main.App.Paths.Logs);
        return Task.CompletedTask;
    });

    [RelayCommand]
    private Task OpenFileAsync() => Main.RunAsync("No se pudo abrir el fichero", () =>
    {
        if (Selected is { } file) Shell.OpenFile(file.Path);
        return Task.CompletedTask;
    });
}
