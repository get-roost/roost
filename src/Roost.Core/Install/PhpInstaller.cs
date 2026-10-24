using System.Text.RegularExpressions;
using Roost.Core.Models;
using Roost.Core.Platform;

namespace Roost.Core.Install;

/// <summary>Un PHP instalado por Roost, en su carpeta bajo <see cref="RoostPaths.PhpBin"/>.</summary>
public sealed record InstalledPhp(string Name, string Directory, string Executable)
{
    /// <summary>Versión sacada del nombre de la carpeta (<c>8.3.36-nts-x64</c>).</summary>
    public ComponentVersion? Version => ComponentVersion.TryParse(Name, out var v) ? v : null;

    /// <summary>true si es un build thread-safe (<c>…-ts-…</c>).</summary>
    public bool ThreadSafe => Name.Contains("-ts-", StringComparison.OrdinalIgnoreCase);

    /// <summary>El binario FastCGI que sirve las peticiones web (Windows no tiene php-fpm).</summary>
    public string CgiExecutable => Path.Combine(Directory, OsNames.Exe("php-cgi"));
}

/// <summary>
/// Instala y quita PHP en la raíz de Roost con las reglas de ROOST.md §5.4 (ver
/// <see cref="InstallFiles"/>): cada versión en su carpeta, nunca se pisa una instalación y
/// si algo falla a medias no queda ni una instalación rota ni temporales.
/// </summary>
public sealed partial class PhpInstaller
{
    /// <summary>Lo que se instala son los zips de Windows: el ejecutable es siempre php.exe.</summary>
    public static string ExecutableName => OsNames.Exe("php");

    private readonly RoostPaths _paths;
    private readonly HttpClient? _http;

    /// <param name="http">Solo hace falta para instalar; listar y quitar no van a la red.</param>
    public PhpInstaller(RoostPaths paths, HttpClient? http = null)
    {
        _paths = paths;
        _http = http;
    }

    [GeneratedRegex(@"^\d+\.\d+$")]
    private static partial Regex BranchPattern();

    /// <summary>PHP instalados por Roost, de más nuevo a más viejo.</summary>
    public IReadOnlyList<InstalledPhp> ListInstalled()
    {
        if (!Directory.Exists(_paths.PhpBin)) return [];

        return Directory.EnumerateDirectories(_paths.PhpBin)
            .Select(d => new InstalledPhp(Path.GetFileName(d), d, Path.Combine(d, ExecutableName)))
            .Where(p => !p.Name.StartsWith('.') && File.Exists(p.Executable))
            .OrderByDescending(p => p.Version)
            .ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Instalaciones que casan con <paramref name="spec"/> (ver <see cref="Match"/>).</summary>
    public IReadOnlyList<InstalledPhp> Find(string spec) => Match(ListInstalled(), spec);

    /// <summary>
    /// Filtra por nombre exacto (<c>8.3.36-nts-x64</c>), versión (<c>8.3.36</c>) o rama
    /// (<c>8.3</c>). Puede devolver varias (NTS y TS de la misma versión), en el orden recibido.
    /// </summary>
    public static IReadOnlyList<InstalledPhp> Match(IEnumerable<InstalledPhp> installed, string spec)
    {
        spec = spec.Trim();
        var all = installed.ToList();

        var byName = all.Where(p => p.Name.Equals(spec, StringComparison.OrdinalIgnoreCase)).ToList();
        if (byName.Count > 0) return byName;

        if (BranchPattern().IsMatch(spec))
            return all.Where(p => p.Version is { } v && $"{v.Major}.{v.Minor}" == spec).ToList();

        if (ComponentVersion.TryParse(spec, out var version))
            return all.Where(p => p.Version is { } v && v.CompareTo(version) == 0).ToList();

        return [];
    }

    /// <summary>De varias que casan, la que se usa: NTS antes que TS (es el que usa nginx) y, dentro de eso, la más nueva.</summary>
    public static InstalledPhp Best(IEnumerable<InstalledPhp> candidates) =>
        candidates.OrderBy(p => p.ThreadSafe).ThenByDescending(p => p.Version).First();

    public string InstallDirectory(PhpBuild build) => Path.Combine(_paths.PhpBin, build.InstallName);

    public bool IsInstalled(PhpBuild build) => Directory.Exists(InstallDirectory(build));

    /// <summary>
    /// Descarga (o reutiliza de la caché), verifica y extrae <paramref name="build"/>.
    /// <paramref name="downloaded"/> recibe los bytes descargados hasta el momento. Lanza
    /// <see cref="RoostException"/> si algo falla, sin dejar nada a medias.
    /// </summary>
    public async Task<InstalledPhp> InstallAsync(
        PhpBuild build,
        IProgress<long>? downloaded = null,
        CancellationToken cancellation = default)
    {
        var http = _http ?? throw new InvalidOperationException("PhpInstaller sin HttpClient no puede instalar.");
        var target = InstallDirectory(build);
        if (Directory.Exists(target))
            throw new RoostException($"PHP {build.InstallName} ya esta instalado en {target}.");

        InstallFiles.CleanLeftovers(_paths.PhpBin);
        var zip = await InstallFiles.DownloadVerifiedAsync(
            http, [build.Url], build.FileName, build.Sha256, _paths.Downloads, downloaded, cancellation);

        // Los zips de PHP para Windows traen php.exe en la raíz, sin carpeta envolvente.
        InstallFiles.ExtractAtomic(zip, target, ExecutableName, stripSingleRoot: false, build.FileName);
        return new InstalledPhp(build.InstallName, target, Path.Combine(target, ExecutableName));
    }

    /// <summary>
    /// Quita un PHP instalado por Roost. Nunca toca nada fuera de <see cref="RoostPaths.PhpBin"/>:
    /// lo de Laragon, XAMPP… no es suyo.
    /// </summary>
    public void Remove(InstalledPhp php)
    {
        InstallFiles.CleanLeftovers(_paths.PhpBin);
        InstallFiles.RemoveAtomic(php.Directory, _paths.PhpBin, php.Name);
    }
}
