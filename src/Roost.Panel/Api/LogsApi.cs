using Microsoft.AspNetCore.Builder;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Roost.Core;
using Roost.Core.Sites;

namespace Roost.WebPanel.Api;

/// <summary>
/// Registros: los ficheros de <c>logs/</c> (y el del helper, y los de cada sitio en <c>logs/sites</c>),
/// con sus errores y avisos contados, y el final de cada uno sin bloquearlo. Los mismos títulos, el mismo
/// orden y el mismo color por nivel que la pantalla de la app (<c>LogsViewModel</c>).
/// </summary>
public static partial class LogsApi
{
    public sealed record LogEntry(string Id, string Title, string Display, string Path, string? Site, int Order, long Size, int Errors, int Warnings);

    public static void Map(RouteGroupBuilder api, PanelApp app)
    {
        api.MapGet("/logs", () => PanelRoutes.Guard(() => PanelRoutes.Ok(new
        {
            logs = app.Paths.Logs,
            files = List(app).Select(f => new { f.Id, f.Title, f.Display, f.Path, f.Site, f.Size, f.Errors, f.Warnings }).ToList(),
        })));
        api.MapGet("/logs/read", (string id, int? tail) => PanelRoutes.Guard(() =>
        {
            var file = List(app).FirstOrDefault(f => f.Id == id) ?? throw new RoostException("Ese registro ya no existe.");
            var (lines, size) = Tail(file.Path, tail ?? 400);
            return PanelRoutes.Ok(new { id, size, lines = lines.Select(l => new { text = l, level = LevelOf(l) }).ToList() });
        }));
    }

    public static IReadOnlyList<LogEntry> List(PanelApp app)
    {
        var paths = app.Paths;
        var files = new List<LogEntry>();
        if (Directory.Exists(paths.Logs))
        {
            foreach (var path in Directory.EnumerateFiles(paths.Logs, "*.log"))
            {
                var name = Path.GetFileName(path);
                files.Add(Entry(name, TitleOf(name), name, path, null, Order(name)));
            }
        }
        var helper = HostsUpdater.HelperLog;
        if (File.Exists(helper))
            files.Add(Entry("helper", "roost-helper (hosts)", OperatingSystem.IsWindows() ? @"%ProgramData%\Roost\roost-helper.log" : helper, helper, null, 5));
        var sitesDir = Path.Combine(paths.Logs, "sites");
        if (Directory.Exists(sitesDir))
        {
            foreach (var path in Directory.EnumerateFiles(sitesDir, "*.log"))
            {
                var name = Path.GetFileName(path);
                var dash = name.LastIndexOf('-');
                if (dash <= 0) continue;
                var site = name[..dash];
                var what = name.EndsWith("-access.log", StringComparison.OrdinalIgnoreCase) ? "accesos" : "errores";
                files.Add(Entry("sites/" + name, $"{site}.test: {what}", "sites" + Path.DirectorySeparatorChar + name, path, site, what == "errores" ? 8 : 9));
            }
        }
        return files.OrderBy(f => f.Order).ThenBy(f => f.Display, StringComparer.Ordinal).ToList();
    }

    private static LogEntry Entry(string id, string title, string display, string path, string? site, int order)
    {
        long size = 0;
        int errors = 0, warnings = 0;
        try
        {
            var (lines, length) = Tail(path, 2000);
            size = length;
            foreach (var line in lines)
            {
                switch (LevelOf(line))
                {
                    case "e": errors++; break;
                    case "w": warnings++; break;
                }
            }
        }
        catch (IOException) { }
        return new LogEntry(id, title, display, path, site, order, size, errors, warnings);
    }

    /// <summary>Las últimas <paramref name="count"/> líneas, leyendo con el fichero compartido (nginx y el daemon lo tienen abierto).</summary>
    public static (IReadOnlyList<string> Lines, long Size) Tail(string path, int count)
    {
        if (!File.Exists(path)) return ([], 0);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var size = stream.Length;
        // Como mucho el último medio mega: basta para unas cientos de líneas.
        const long window = 512 * 1024;
        if (size > window) stream.Seek(size - window, SeekOrigin.Begin);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var text = reader.ReadToEnd();
        var lines = text.Split('\n').Select(l => l.TrimEnd('\r')).ToList();
        if (lines.Count > 0 && lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1);
        if (size > window && lines.Count > 0) lines.RemoveAt(0);   // la primera puede estar partida
        if (lines.Count > count) lines = lines.Skip(lines.Count - count).ToList();
        return (lines, size);
    }

    [GeneratedRegex(@"\[(error|emerg|crit|alert)\]|\bERROR\b|PHP (Fatal|Parse) error|\[ERROR\]|no arranca|up fallido", RegexOptions.Compiled)]
    private static partial Regex ErrorPattern();

    [GeneratedRegex(@"\[warn\]|\bWarning\b|\[Warning\]|se cayo|cancelado|no se paro", RegexOptions.Compiled)]
    private static partial Regex WarnPattern();

    [GeneratedRegex(@"\[Note\]|\[notice\]|roostd en marcha|panel en marcha", RegexOptions.Compiled)]
    private static partial Regex MutedPattern();

    /// <summary>e (error), w (aviso), m (apagado) o n (normal), como <c>LogsViewModel.LevelOf</c>.</summary>
    public static string LevelOf(string line) =>
        ErrorPattern().IsMatch(line) ? "e" : WarnPattern().IsMatch(line) ? "w" : MutedPattern().IsMatch(line) ? "m" : "n";

    public static string TitleOf(string name)
    {
        var stem = Path.GetFileNameWithoutExtension(name);
        if (stem == "roostd") return "Roost (daemon)";
        if (stem == "panel") return "Roost (panel web)";
        if (stem == "roost-desktop") return "Roost (app)";
        if (stem == "nginx-error") return "nginx: errores";
        if (stem == "nginx-access") return "nginx: accesos";
        if (stem == "nginx-stdout") return "nginx: salida";
        if (stem == "error") return "nginx: arranque";
        if (stem == "apache-error") return "Apache: errores";
        if (stem == "apache-access") return "Apache: accesos";
        if (stem == "apache-stdout") return "Apache: salida";
        if (stem == "uninstall") return "Desinstalación";
        if (Regex.Match(stem, @"^php-(\d+\.\d+)-error$") is { Success: true } e) return $"PHP {e.Groups[1].Value}: errores";
        if (Regex.Match(stem, @"^php-(\d+\.\d+)-(\d+)$") is { Success: true } p) return $"PHP {p.Groups[1].Value}: {(OperatingSystem.IsWindows() ? "php-cgi" : "php-fpm")} {p.Groups[2].Value}";
        if (Regex.Match(stem, @"^mariadb-(.+)$") is { Success: true } m) return $"MariaDB {m.Groups[1].Value}";
        if (Regex.Match(stem, @"^mysql-(.+)$") is { Success: true } y) return $"MySQL {y.Groups[1].Value}";
        if (Regex.Match(stem, @"^tunel-(.+)$") is { Success: true } t) return $"Túnel de {t.Groups[1].Value}";
        if (Regex.Match(stem, @"^pasos-(.+)$") is { Success: true } s) return $"Pasos de {s.Groups[1].Value}";
        if (Regex.Match(stem, @"^git-(.+)$") is { Success: true } g) return $"git de {g.Groups[1].Value}";
        return stem;
    }

    private static int Order(string name) => name switch
    {
        "roostd.log" => 0,
        "panel.log" => 0,
        "nginx-error.log" or "apache-error.log" => 1,
        "nginx-access.log" or "apache-access.log" => 2,
        _ when Regex.IsMatch(name, @"^php-\d+\.\d+-error\.log$") => 3,
        _ when name.StartsWith("mariadb-", StringComparison.Ordinal) || name.StartsWith("mysql-", StringComparison.Ordinal) => 4,
        _ when name.StartsWith("php-", StringComparison.Ordinal) => 7,
        _ => 6,
    };
}
