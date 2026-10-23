using Roost.Core.Platform;

namespace Roost.Core.Install;

/// <summary>
/// Instala y quita MariaDB y MySQL en <c>bin/&lt;motor&gt;/&lt;versión&gt;</c> con las reglas de
/// <see cref="InstallFiles"/>. Solo toca binarios: los datos (<c>data/</c>) no se borran nunca.
/// </summary>
public sealed class DbInstaller
{
    private readonly RoostPaths _paths;
    private readonly HttpClient? _http;

    /// <param name="http">Solo hace falta para instalar.</param>
    public DbInstaller(RoostPaths paths, HttpClient? http = null)
    {
        _paths = paths;
        _http = http;
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
        var zip = await InstallFiles.DownloadVerifiedAsync(
            http, build.Urls, build.FileName, build.Sha256, _paths.Downloads, downloaded, cancellation);

        // Los zips oficiales de los dos motores lo meten todo en mariadb-…-winx64/ o mysql-…-winx64/.
        InstallFiles.ExtractAtomic(
            zip, target, Path.Combine("bin", build.Engine.ServerExe()), stripSingleRoot: true, build.FileName);
        return new InstalledDb(build.Engine, build.InstallName, target);
    }

    /// <summary>Quita los binarios. Los datos de <c>data/&lt;motor&gt;/&lt;rama&gt;</c> se quedan.</summary>
    public void Remove(InstalledDb db)
    {
        var bin = _paths.DbBin(db.Engine.Key());
        InstallFiles.CleanLeftovers(bin);
        InstallFiles.RemoveAtomic(db.Directory, bin, db.Label);
    }
}
