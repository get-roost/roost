using Roost.Core.Deploy;
using Roost.Core.Install;
using Roost.Core.Platform;
using Roost.Core.Scanning;
using Roost.Core.Stack;

namespace Roost.Core.Sites;

/// <summary>Lo que ha hecho <see cref="SiteDatabaseCopy.CopyAsync"/>.</summary>
/// <param name="EnvUpdated">El <c>.env</c> del proyecto, si tenía <c>DB_DATABASE</c> con el nombre de antes y se ha cambiado.</param>
public sealed record SiteDatabaseCopied(string From, string To, long Bytes, string? EnvUpdated);

/// <summary>
/// Sitios avanzados (C2): al renombrar un sitio, su base de datos se puede llevar a otro nombre.
/// MySQL y MariaDB no saben renombrar una base de datos (y <c>RENAME TABLE</c> entre bases no lleva
/// ni vistas ni triggers ni rutinas), así que se copia: se vuelca la de antes (con rutinas,
/// triggers y eventos, <see cref="DbDump"/>), se importa en una nueva y el sitio pasa a usar la
/// nueva. <b>La de antes se queda</b>, intacta: se borra a mano cuando se haya comprobado todo. Si
/// algo falla, se borra la nueva a medias (la ha creado Roost) y se dice.
/// </summary>
public static class SiteDatabaseCopy
{
    public static async Task<SiteDatabaseCopied> CopyAsync(RoostPaths paths, InstalledDb db, string iniPath, string from, string to,
        ICommandRunner runner, IProcessRunner admin, IProgress<string>? lines, CancellationToken cancellation)
    {
        foreach (var name in new[] { from, to })
        {
            if (!DbAdmin.IsValidName(name) || DbAdmin.SystemSchemas.Contains(name))
                throw new RoostException($"'{name}' no vale como base de datos de un sitio: usa solo letras, numeros y _ (hasta 64), y ninguna del sistema.");
        }
        if (from == to) throw new RoostException($"La base de datos ya se llama {to}.");

        var existing = DbAdmin.List(db, iniPath, admin);
        if (!existing.Contains(from, StringComparer.OrdinalIgnoreCase))
            throw new RoostException($"No existe la base de datos {from} en {db.Label}: no hay nada que copiar.");
        if (existing.Contains(to, StringComparer.OrdinalIgnoreCase))
            throw new RoostException($"Ya existe {to} en {db.Label}: Roost no copia encima de otra base de datos. Elige otro nombre o dejala como estaba.");

        Directory.CreateDirectory(paths.Run);
        var dump = Path.Combine(paths.Run, $".copia-{from}-{Guid.NewGuid().ToString("N")[..8]}.sql.gz");
        var tool = DbTool.For(db, iniPath);
        var created = false;
        try
        {
            var exported = await DbDump.ExportAsync(tool, from, dump, runner, null, lines, cancellation);
            DbAdmin.Create(db, iniPath, to, admin);
            created = true;
            await DbDump.ImportAsync(tool, to, dump, null, runner, null, lines, cancellation);
            created = false;   // ya es buena: no se borra
            return new SiteDatabaseCopied(from, to, exported.Bytes, null);
        }
        finally
        {
            if (created)
            {
                try { DbAdmin.Drop(db, iniPath, to, admin); } catch (RoostException) { /* se dice en el error de arriba */ }
            }
            InstallFiles.TryDeleteFile(dump);
        }
    }

    /// <summary>
    /// Si el <c>.env</c> de la carpeta tiene <c>DB_DATABASE=&lt;from&gt;</c>, lo pasa a <paramref name="to"/>
    /// (respetando el resto del fichero). Devuelve su ruta si lo ha cambiado; null si no había nada que cambiar.
    /// </summary>
    public static string? UpdateEnv(string folder, string from, string to)
    {
        var path = Path.Combine(folder, ".env");
        if (!File.Exists(path)) return null;
        var (env, bom) = EnvFile.Load(path);
        if (env.Get("DB_DATABASE") is not { } current || !current.Equals(from, StringComparison.Ordinal)) return null;
        env.Set("DB_DATABASE", to);
        env.Save(path, bom);
        return path;
    }
}
