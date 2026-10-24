using System.Text.RegularExpressions;
using Roost.Core.Models;
using Roost.Core.Platform;

namespace Roost.Core.Install;

/// <summary>
/// Un PHP instalado por Roost, en su carpeta bajo <see cref="RoostPaths.PhpBin"/>. El ejecutable
/// está en la raíz de la carpeta en Windows (el zip oficial) y en <c>bin/</c> en Linux (el tar.gz de
/// roost-builds); los demás binarios (php-cgi, php-fpm) van a su lado.
/// </summary>
public sealed record InstalledPhp(string Name, string Directory, string Executable)
{
    /// <summary>Versión sacada del nombre de la carpeta (<c>8.3.36-nts-x64</c>).</summary>
    public ComponentVersion? Version => ComponentVersion.TryParse(Name, out var v) ? v : null;

    /// <summary>true si es un build thread-safe (<c>…-ts-…</c>).</summary>
    public bool ThreadSafe => Name.Contains("-ts-", StringComparison.OrdinalIgnoreCase);

    /// <summary>La carpeta del ejecutable: la de la instalación en Windows, <c>bin/</c> en los builds de Linux.</summary>
    public string BinDirectory => Path.GetDirectoryName(Executable) ?? Directory;

    /// <summary>El php-cgi que sirve las peticiones web en Windows (que no tiene php-fpm).</summary>
    public string CgiExecutable => Path.Combine(BinDirectory, OsNames.Exe("php-cgi"));

    /// <summary>El php-fpm que sirve las peticiones web en Linux (<c>bin/php-fpm</c> en los builds de roost-builds). En Windows no existe.</summary>
    public string FpmExecutable => Path.Combine(BinDirectory, "php-fpm");

    /// <summary>true si trae php-fpm: los PHP de roost-builds. Uno copiado a mano sin él sirve con php-cgi, como en Windows.</summary>
    public bool HasFpm => !OperatingSystem.IsWindows() && File.Exists(FpmExecutable);

    /// <summary>Lo que dice su <c>BUILD.json</c> (los de roost-builds: extensiones integradas, Xdebug…); null en los de Windows.</summary>
    public PhpBuildInfo? ReadBuildInfo() => PhpBuildInfo.Read(Directory);
}

/// <summary>
/// Instala y quita PHP en la raíz de Roost con las reglas de ROOST.md §5.4 (ver
/// <see cref="InstallFiles"/>): cada versión en su carpeta, nunca se pisa una instalación y
/// si algo falla a medias no queda ni una instalación rota ni temporales.
/// </summary>
public sealed partial class PhpInstaller
{
    /// <summary>El nombre del ejecutable en este sistema: <c>php.exe</c> o <c>php</c>.</summary>
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

    /// <summary>
    /// El ejecutable de una carpeta de instalación: <c>php.exe</c> en la raíz (los zips de Windows);
    /// en Linux, <c>bin/php</c> (el tar.gz de roost-builds) o, si no lo hay, <c>php</c> en la raíz (una
    /// instalación copiada a mano). Que exista lo comprueba quien lo llama.
    /// </summary>
    public static string ExecutableIn(string directory)
    {
        if (!OperatingSystem.IsWindows())
        {
            var nested = Path.Combine(directory, "bin", "php");
            if (File.Exists(nested)) return nested;
        }
        return Path.Combine(directory, ExecutableName);
    }

    /// <summary>PHP instalados por Roost, de más nuevo a más viejo.</summary>
    public IReadOnlyList<InstalledPhp> ListInstalled()
    {
        if (!Directory.Exists(_paths.PhpBin)) return [];

        return Directory.EnumerateDirectories(_paths.PhpBin)
            .Select(d => new InstalledPhp(Path.GetFileName(d), d, ExecutableIn(d)))
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
        var archive = await InstallFiles.DownloadVerifiedAsync(
            http, [build.Url], build.FileName, build.Sha256, _paths.Downloads, downloaded, cancellation);

        // Los zips de PHP para Windows traen php.exe en la raíz, sin carpeta envolvente; los tar.gz de
        // roost-builds, bin/php, bin/php-fpm, ext/xdebug.so, BUILD.json y las licencias, también sin ella.
        InstallFiles.ExtractAtomic(archive, target, build.ExecutableRelativePath, stripSingleRoot: false, build.FileName);
        var executable = Path.Combine(target, build.ExecutableRelativePath);
        if (build.Os == PhpOs.Linux)
        {
            // El tar conserva los permisos; por si lo empaquetó algo que no los pone, los binarios quedan ejecutables.
            foreach (var name in new[] { "php", "php-fpm", "php-cgi" })
            {
                var file = Path.Combine(target, "bin", name);
                if (File.Exists(file)) InstallFiles.MarkExecutable(file);
            }
        }
        return new InstalledPhp(build.InstallName, target, executable);
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
