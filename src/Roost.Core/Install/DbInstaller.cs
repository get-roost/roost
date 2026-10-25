using Roost.Core.Platform;
using Roost.Core.Scanning;

namespace Roost.Core.Install;

/// <summary>
/// Instala y quita MariaDB y MySQL en <c>bin/&lt;motor&gt;/&lt;versión&gt;</c> con las reglas de
/// <see cref="InstallFiles"/>. Solo toca binarios: los datos (<c>data/</c>) no se borran nunca. En
/// Linux, el bintar de MariaDB (tar.gz) se extrae como un zip; el tar.xz <c>-minimal</c> de MySQL, con el
/// <c>tar</c> del sistema (<c>System.Formats.Tar</c> no sabe de xz), y después se le deja el enlace a
/// <c>libaio</c> que pide (<see cref="EnsureLibAio"/>).
/// </summary>
public sealed class DbInstaller
{
    private readonly RoostPaths _paths;
    private readonly HttpClient? _http;
    private readonly ICommandRunner _commands;

    /// <param name="http">Solo hace falta para instalar.</param>
    /// <param name="commands">Linux: con el que se lanza el <c>tar</c> del sistema para un tar.xz.</param>
    public DbInstaller(RoostPaths paths, HttpClient? http = null, ICommandRunner? commands = null)
    {
        _paths = paths;
        _http = http;
        _commands = commands ?? CommandRunner.Default;
    }

    /// <summary>Bases de datos instaladas por Roost: primero MariaDB, y dentro de cada motor, de más nueva a más vieja.</summary>
    public IReadOnlyList<InstalledDb> ListInstalled()
    {
        var all = new List<InstalledDb>();
        foreach (var engine in DbEngines.All)
        {
            var bin = _paths.DbBin(engine.Key());
            if (!Directory.Exists(bin)) continue;
            all.AddRange(Directory.EnumerateDirectories(bin)
                .Select(d => new InstalledDb(engine, Path.GetFileName(d), d))
                .Where(db => !db.Name.StartsWith('.') && db.Version is not null && File.Exists(db.Server))
                .OrderByDescending(db => db.Version));
        }
        return all;
    }

    /// <summary>Las instalaciones que casan con <paramref name="spec"/>, en el orden recibido.</summary>
    public static IReadOnlyList<InstalledDb> Match(IEnumerable<InstalledDb> installed, DbSpec spec) =>
        installed.Where(db => db.Version is { } v && spec.Matches(db.Engine, v)).ToList();

    public string InstallDirectory(DbBuild build) => Path.Combine(_paths.DbBin(build.Engine.Key()), build.InstallName);

    public bool IsInstalled(DbBuild build) => Directory.Exists(InstallDirectory(build));

    /// <summary>¿Es un tar.xz (el <c>-minimal</c> de MySQL en Linux)? Lo demás son zips o tar.gz.</summary>
    public static bool IsTarXz(string fileName) =>
        fileName.EndsWith(".tar.xz", StringComparison.OrdinalIgnoreCase) || fileName.EndsWith(".txz", StringComparison.OrdinalIgnoreCase);

    public async Task<InstalledDb> InstallAsync(
        DbBuild build,
        IProgress<long>? downloaded = null,
        CancellationToken cancellation = default)
    {
        var http = _http ?? throw new InvalidOperationException("DbInstaller sin HttpClient no puede instalar.");
        var target = InstallDirectory(build);
        if (Directory.Exists(target))
            throw new RoostException($"{build.Label} ya esta instalado en {target}.");

        var bin = _paths.DbBin(build.Engine.Key());
        InstallFiles.CleanLeftovers(bin);
        var archive = await InstallFiles.DownloadVerifiedAsync(
            http, build.Urls, build.FileName, build.Sha256, _paths.Downloads, downloaded, cancellation);

        // Los paquetes oficiales de los dos motores lo meten todo en mariadb-…/ o mysql-…/: se instala su contenido.
        var required = Path.Combine("bin", build.Engine.ServerExe());
        if (IsTarXz(build.FileName))
            await ExtractTarXzAtomicAsync(archive, target, required, build.FileName, cancellation);
        else
            InstallFiles.ExtractAtomic(archive, target, required, stripSingleRoot: true, build.FileName);

        var installed = new InstalledDb(build.Engine, build.InstallName, target);
        EnsureLibAio(installed);
        return installed;
    }

    /// <summary>
    /// Extrae un tar.xz con el <c>tar</c> del sistema a una carpeta <c>.tmp-…</c> que se renombra al final
    /// (como <see cref="InstallFiles.ExtractAtomic"/>): o queda instalado entero o no queda nada. Quita la
    /// carpeta envolvente y exige <paramref name="requiredFile"/>.
    /// </summary>
    private async Task ExtractTarXzAtomicAsync(string archive, string target, string requiredFile, string label, CancellationToken cancellation)
    {
        var parent = Path.GetDirectoryName(target)!;
        Directory.CreateDirectory(parent);
        var temp = Path.Combine(parent, InstallFiles.TempPrefix + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(temp);
            var tar = LinuxDeps.FindTool(LinuxDeps.Tar) ?? LinuxDeps.Tar;
            var result = await _commands.RunAsync(new CommandSpec(tar, ["-xJf", archive, "-C", temp], Timeout: TimeSpan.FromMinutes(10)), null, cancellation);
            if (!result.Succeeded)
            {
                var detail = result.Stderr.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.Length > 0) ?? $"codigo {result.ExitCode}";
                throw new RoostException($"No se pudo extraer {label} con tar ({detail}). Hacen falta tar y xz (sudo apt install tar xz-utils).");
            }

            var source = temp;
            if (Directory.GetFiles(temp).Length == 0 && Directory.GetDirectories(temp) is [var only])
                source = only;
            if (!File.Exists(Path.Combine(source, requiredFile)))
                throw new RoostException($"El tar.xz {label} no trae {requiredFile}; no se instala.");

            InstallFiles.MoveDirectory(source, target);
        }
        catch (Exception ex) when (ex is not RoostException and not OperationCanceledException)
        {
            throw new RoostException($"No se pudo extraer {label} en {parent}: {ex.Message}", ex);
        }
        finally
        {
            InstallFiles.TryDeleteDirectory(temp);
        }
    }

    /// <summary>
    /// PURO: Linux: a qué librería del sistema hay que enlazar <c>lib/private/libaio.so.1</c> para que arranque
    /// MySQL: null si la distro ya tiene <c>libaio.so.1</c> (o no tiene ninguna: entonces no hay a qué enlazar y
    /// el SmokeTest dirá el paquete); la ruta de <c>libaio.so.1t64</c> si solo está esa (Ubuntu 24.04 y Debian
    /// 13 la renombraron por la transición a time_t de 64 bits, y mysqld busca el nombre viejo).
    /// </summary>
    public static string? LibAioLinkTarget(Func<string, string?> findLibrary) =>
        findLibrary("libaio.so.1") is not null ? null : findLibrary("libaio.so.1t64");

    /// <summary>
    /// Linux: deja el enlace <c>lib/private/libaio.so.1</c> de una instalación de MySQL a la <c>libaio.so.1t64</c>
    /// de la distro si hace falta (<see cref="LibAioLinkTarget"/>): <c>mysqld</c> lleva <c>RUNPATH $ORIGIN/../lib/private</c>
    /// y la carga de ahí. Se hace al instalar y antes de cada arranque (una instalación de antes, otra
    /// distro). Nunca lanza: si no se puede, el SmokeTest y el arranque dirán qué falta. true si lo ha enlazado.
    /// </summary>
    public static bool EnsureLibAio(InstalledDb db)
    {
        if (OperatingSystem.IsWindows() || db.Engine != DbEngine.MySql) return false;
        var target = LibAioLinkTarget(LinuxDeps.FindLibrary);
        if (target is null) return false;
        try
        {
            var dir = Path.Combine(db.Directory, "lib", "private");
            var link = Path.Combine(dir, "libaio.so.1");
            var info = new FileInfo(link);
            if (info.Exists && (info.LinkTarget is null || info.ResolveLinkTarget(true)?.Exists == true)) return false;   // ya está (fichero o enlace que vale)
            if (info.Exists || info.LinkTarget is not null) info.Delete();   // un enlace roto
            Directory.CreateDirectory(dir);
            File.CreateSymbolicLink(link, target);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Quita los binarios. Los datos de <c>data/&lt;motor&gt;/&lt;rama&gt;</c> se quedan.</summary>
    public void Remove(InstalledDb db)
    {
        var bin = _paths.DbBin(db.Engine.Key());
        InstallFiles.CleanLeftovers(bin);
        InstallFiles.RemoveAtomic(db.Directory, bin, db.Label);
    }
}
