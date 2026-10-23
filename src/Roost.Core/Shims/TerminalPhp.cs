using Roost.Core.Config;
using Roost.Core.Install;
using Roost.Core.Platform;
using Roost.Core.Scanning;
using Roost.Core.Sites;
using Roost.Core.Stack;

namespace Roost.Core.Shims;

/// <summary>El PHP elegido para la terminal y por qué (lo cuenta <c>roost php which</c>).</summary>
public sealed record PhpChoice(InstalledPhp Php, string Reason);

/// <summary>
/// Qué PHP usa la terminal en una carpeta (ROOST.md §5.9, caso de uso 2). Lo decide el shim
/// <c>php</c> en cada llamada, en este orden:
/// <list type="number">
/// <item>la variable <see cref="VersionVariable"/> (para un script o una sola orden);</item>
/// <item>un <see cref="VersionFile"/> en la carpeta o en una superior (versionable en git);</item>
/// <item>el PHP propio del sitio cuya carpeta contiene a esta;</item>
/// <item>el PHP global (<c>[php] version</c>, o el más nuevo instalado).</item>
/// </list>
/// Lo que se pide explícitamente y no casa con nada instalado es un error, no un «ya usaré otro».
/// </summary>
public static class TerminalPhp
{
    public const string VersionVariable = "ROOST_PHP_VERSION";
    public const string VersionFile = ".php-version";

    public static PhpChoice Resolve(RoostPaths paths, string directory, string? pinned)
    {
        var installed = new PhpInstaller(paths).ListInstalled();
        if (installed.Count == 0)
            throw new RoostException("No hay ningun PHP instalado por Roost. Instala uno: roost php install 8.4");

        if (!string.IsNullOrWhiteSpace(pinned))
            return Pick(installed, pinned.Trim(), $"{VersionVariable}={pinned.Trim()}");

        directory = Path.GetFullPath(directory);
        if (FindVersionFile(directory) is { } file)
        {
            var spec = ReadVersionFile(file)
                ?? throw new RoostException($"{file} esta vacio: pon una version (8.3, 8.3.36) o borralo.");
            return Pick(installed, spec, $"{file} pide {spec}");
        }

        if (FindSite(new SiteRegistry(paths).Load(), directory) is { } site)
        {
            if (site.Php is { } sitePhp) return Pick(installed, sitePhp, $"el sitio {site.Host} usa PHP {sitePhp}");
            return Global(paths, installed, $"el sitio {site.Host} usa el PHP global");
        }

        return Global(paths, installed, null);
    }

    /// <summary>
    /// Lo que necesita el PHP de la terminal para cargar las mismas extensiones y ajustes que el
    /// servidor: <c>PHPRC</c> (la carpeta de su php.ini generado) y <c>PHP_INI_SCAN_DIR</c> (conf.d).
    /// Van en el entorno y no en <c>-c</c> para que los PHP que lance este (Composer, PHPUnit) los
    /// hereden. Si el php.ini aún no existe (nunca se ha hecho <c>up</c>), lo genera.
    /// </summary>
    public static IReadOnlyDictionary<string, string> Environment(RoostPaths paths, InstalledPhp php)
    {
        var planner = new StackPlanner(paths, ProcessRunner.Default);
        var ini = planner.PhpIniPath(php);
        if (!File.Exists(ini))
        {
            planner.EnsureUserIni();
            planner.WritePhpIni(php);
        }
        return new Dictionary<string, string>
        {
            ["PHPRC"] = Path.GetDirectoryName(ini)!,
            ["PHP_INI_SCAN_DIR"] = planner.PhpConfD,
        };
    }

    /// <summary>
    /// PURO: la línea de órdenes sin el propio ejecutable, sin volver a partirla ni a
    /// entrecomillarla. Lo que llegó al shim es lo que recibe PHP: rutas con espacios, comillas
    /// dentro de <c>-r</c>, etc. (re-entrecomillar argv en Windows no es reversible).
    /// </summary>
    public static string ArgumentsAfterProgram(string commandLine)
    {
        var line = commandLine.TrimStart();
        int end;
        if (line.StartsWith('"'))
        {
            var close = line.IndexOf('"', 1);
            end = close < 0 ? line.Length : close + 1;
        }
        else
        {
            end = line.IndexOfAny([' ', '\t']);
            if (end < 0) end = line.Length;
        }
        return line[end..].TrimStart(' ', '\t');
    }

    /// <summary>El <see cref="VersionFile"/> más cercano subiendo desde <paramref name="directory"/>, o null.</summary>
    public static string? FindVersionFile(string directory)
    {
        for (var dir = new DirectoryInfo(directory); dir is not null; dir = dir.Parent)
        {
            var file = Path.Combine(dir.FullName, VersionFile);
            if (File.Exists(file)) return file;
        }
        return null;
    }

    /// <summary>
    /// La versión de un <c>.php-version</c>: la primera línea con algo que no sea un comentario.
    /// Acepta también el formato de asdf («php 8.3.36»).
    /// </summary>
    public static string? ReadVersionFile(string file)
    {
        foreach (var raw in File.ReadLines(file))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            if (line.StartsWith("php ", StringComparison.OrdinalIgnoreCase)) line = line[4..].Trim();
            return line;
        }
        return null;
    }

    /// <summary>El sitio cuya carpeta contiene a <paramref name="directory"/>; si hay varios anidados, el más profundo.</summary>
    public static Site? FindSite(IEnumerable<Site> sites, string directory)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var here = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        return sites
            .Select(s => (Site: s, Folder: Path.TrimEndingDirectorySeparator(Path.GetFullPath(s.Folder))))
            .Where(x => here.Equals(x.Folder, comparison)
                        || here.StartsWith(x.Folder + Path.DirectorySeparatorChar, comparison))
            .OrderByDescending(x => x.Folder.Length)
            .Select(x => x.Site)
            .FirstOrDefault();
    }

    private static PhpChoice Global(RoostPaths paths, IReadOnlyList<InstalledPhp> installed, string? why)
    {
        var config = RoostConfig.Load(paths.ConfigFile);
        var php = new StackPlanner(paths, ProcessRunner.Default).ResolvePhp(config, installed);
        var reason = config.PhpVersion is { } spec ? $"el PHP global ({spec}, en {paths.ConfigFile})" : "el PHP global (el mas nuevo instalado)";
        return new PhpChoice(php, why is null ? reason : $"{why}: {reason}");
    }

    private static PhpChoice Pick(IReadOnlyList<InstalledPhp> installed, string spec, string reason)
    {
        var candidates = PhpInstaller.Match(installed, spec);
        if (candidates.Count == 0)
            throw new RoostException($"{reason}, pero no hay ningun PHP de Roost que case. Instalalo: roost php install {spec}");
        return new PhpChoice(PhpInstaller.Best(candidates), reason);
    }
}
