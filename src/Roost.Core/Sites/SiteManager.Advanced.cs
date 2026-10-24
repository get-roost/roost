using Roost.Core.Git;
using Roost.Core.Install;
using Roost.Core.Stack;

namespace Roost.Core.Sites;

/// <summary>El resultado de <see cref="SiteManager.Rename"/>.</summary>
/// <param name="Moved">Lo que se ha llevado al nombre nuevo (historial y registros de git, de los pasos y del sitio).</param>
/// <param name="ConfsKept">Los vhost de antes, apartados como <c>.conf.removed</c> (por si tenían cambios a mano).</param>
/// <param name="AliasesMoved">Los subdominios del nombre de antes que pasan al nuevo (<c>api.tienda.test</c> → <c>api.shop.test</c>).</param>
public sealed record SiteRenamed(Site Old, Site Site, IReadOnlyList<string> Moved, IReadOnlyList<string> ConfsKept, IReadOnlyList<(string From, string To)> AliasesMoved);

/// <summary>El resultado de <see cref="SiteManager.Move"/>: el sitio y si se ha movido la carpeta o solo se apunta a otra.</summary>
public sealed record SiteMoved(Site Old, Site Site, bool FilesMoved);

/// <summary>Una cosa que el <c>.roost.toml</c> de un sitio ha cambiado (<see cref="SiteManager.SyncProjectFiles"/>).</summary>
public sealed record ProjectChange(string Site, string What);

/// <summary>Lo que ha hecho <see cref="SiteManager.SyncProjectFiles"/>: los cambios y lo que no se ha podido aplicar.</summary>
public sealed record ProjectSync(IReadOnlyList<ProjectChange> Changes, IReadOnlyList<string> Warnings)
{
    public bool Changed => Changes.Count > 0;

    /// <summary>Si ha cambiado algún nombre (un alias): con el hosts, hay que sincronizarlo.</summary>
    public bool NamesChanged { get; init; }
}

/// <summary>Las claves del <c>.roost.toml</c> que acaba de cambiar una operación (<see cref="SiteManager.KeepProjectFile"/>).</summary>
[Flags]
public enum ProjectKeys
{
    None = 0,
    Php = 1,
    Root = 2,
    Db = 4,
    Aliases = 8,
}

/// <summary>Una subcarpeta de una carpeta aparcada que no es sitio, y por qué.</summary>
public sealed record ParkSkip(string Folder, string Reason);

/// <summary>Lo que ha hecho <see cref="SiteManager.SyncParked"/>: los sitios nuevos, los quitados y las carpetas saltadas.</summary>
public sealed record ParkSync(IReadOnlyList<Site> Added, IReadOnlyList<Site> Removed, IReadOnlyList<ParkSkip> Skipped)
{
    public bool Changed => Added.Count > 0 || Removed.Count > 0;

    public static ParkSync None { get; } = new([], [], []);
}

/// <summary>
/// Sitios avanzados (oleada 6): renombrar y mover un sitio (C2), sus alias (C7), el
/// <c>.roost.toml</c> del proyecto (C11) y las carpetas aparcadas (C3). Nada de esto borra la carpeta
/// de un proyecto ni una base de datos.
/// </summary>
public sealed partial class SiteManager
{
    // C2: renombrar y mover

    /// <summary>
    /// Renombra un sitio: el registro, su certificado (se emite el del nombre nuevo y se borra el de
    /// antes), sus subdominios con nombre, su historial y su registro de git, el de los pasos y los
    /// registros del vhost. Los <c>.conf</c> de antes (nginx y Apache) se apartan como
    /// <c>.conf.removed</c>, como al quitar un sitio: los nuevos los escribe el siguiente <c>up</c>. La
    /// base de datos <b>no</b> cambia: queda fijada la de antes (<see cref="Site.Database"/>), y
    /// copiarla a otro nombre es aparte (<see cref="SiteDatabaseCopy"/>). Quien llama sincroniza el hosts
    /// y aplica el stack.
    /// </summary>
    public SiteRenamed Rename(string name, string newName)
    {
        name = NormalizeName(name);
        newName = NormalizeName(newName);
        var all = Registry.Load();
        var site = all.FirstOrDefault(s => s.Name == name)
            ?? throw new RoostException($"No hay ningun sitio '{name}'. Mira los registrados con: roost site list");
        if (newName == name) throw new RoostException($"{site.Host} ya se llama asi.");
        if (!Site.IsValidName(newName))
            throw new RoostException($"'{newName}' no vale como nombre de sitio: usa minusculas, numeros y guiones (por ejemplo tienda o mi-blog).");
        if (all.FirstOrDefault(s => s.Name == newName) is { } other)
            throw new RoostException($"Ya hay un sitio {other.Host} (en {other.Folder}). Elige otro nombre o quita ese antes.");
        if (SiteAliases.WhyNotName(newName, all, except: name) is { } taken)
            throw new RoostException(taken);
        if (site.Aliases?.Contains($"{newName}.test", StringComparer.Ordinal) == true)
            throw new RoostException($"{newName}.test es un alias de {site.Host}: quitalo antes (roost site alias {name} remove {newName}.test) y vuelve a renombrar.");

        // Los subdominios del nombre de antes pasan al nuevo; los demás alias se quedan como están.
        var newHost = $"{newName}.test";
        var movedAliases = new List<(string, string)>();
        var aliases = site.Aliases?.Select(alias =>
        {
            if (!SiteAliases.IsSubdomainOf(alias, site.Host)) return alias;
            var under = alias[..^site.Host.Length] + newHost;
            movedAliases.Add((alias, under));
            return under;
        }).ToList();
        var renamed = site with { Name = newName, Database = DatabaseOf(site), Aliases = aliases };
        var others = all.Where(s => s.Name != name).ToList();
        foreach (var (_, under) in movedAliases)
        {
            if (SiteAliases.WhyNot(renamed with { Aliases = null }, under, others) is { } why)
                throw new RoostException($"No se puede renombrar: {why}");
        }

        // Primero el registro (si falla, no se ha tocado nada más); luego lo que va con el nombre.
        Registry.Remove(name);
        Registry.Upsert(renamed);

        var kept = new List<string>();
        foreach (var dir in new[] { paths.NginxSites, paths.ApacheSites })
        {
            var conf = Path.Combine(dir, name + ".conf");
            if (!File.Exists(conf)) continue;
            File.Move(conf, conf + ".removed", overwrite: true);
            kept.Add(conf + ".removed");
        }

        var ca = new LocalCa(paths);
        ca.RemoveSiteCert(name);
        ca.EnsureSiteCert(renamed);

        var moved = new List<string>();
        var history = new GitHistory(paths);
        moved.AddRange(MoveIfFree(Path.GetDirectoryName(history.FilePath(name))!, Path.GetDirectoryName(history.FilePath(newName))!, directory: true));
        var gitLog = new GitLog(paths);
        moved.AddRange(MoveIfFree(gitLog.FilePath(name), gitLog.FilePath(newName)));
        moved.AddRange(MoveIfFree(Path.Combine(paths.Logs, $"pasos-{name}.log"), Path.Combine(paths.Logs, $"pasos-{newName}.log")));
        moved.AddRange(SiteLogs.Rename(paths, name, newName));

        KeepProjectFile(renamed, ProjectKeys.Db | ProjectKeys.Aliases);
        return new SiteRenamed(site, renamed, moved, kept, movedAliases);
    }

    /// <summary>
    /// El sitio pasa a servirse desde otra carpeta, validada como en <see cref="Add(string, string?, string?, string?)"/>
    /// (en relativo, cuelga de la www). Con <paramref name="moveFiles"/>, Roost mueve la carpeta del
    /// proyecto allí (tiene que no existir, o estar vacía, y estar en la misma unidad); si no, solo
    /// apunta a una que ya existe. La raíz web se conserva si existe en la carpeta nueva; si no, se
    /// vuelve a mirar (<c>public/</c>). El repositorio de git solo sigue si se mueve la carpeta (es
    /// el mismo checkout). Quien llama aplica el stack.
    /// </summary>
    public SiteMoved Move(string name, string folder, bool moveFiles)
    {
        name = NormalizeName(name);
        var site = Registry.Find(name)
            ?? throw new RoostException($"No hay ningun sitio '{name}'. Mira los registrados con: roost site list");
        var target = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder.Trim(), paths.Www));
        var source = Path.TrimEndingDirectorySeparator(Path.GetFullPath(site.Folder));
        if (SamePath(source, target))
            throw new RoostException($"{site.Host} ya se sirve desde {source}.");

        if (moveFiles)
        {
            if (!Directory.Exists(source))
                throw new RoostException($"La carpeta de {site.Host} ({source}) ya no existe: no hay nada que mover. Usa solo apuntar (sin --move-files) a donde este ahora.");
            if (IsInside(target, source))
                throw new RoostException($"{target} esta dentro de la carpeta del proyecto: no se puede mover una carpeta dentro de si misma.");
            if (Directory.Exists(target) && Directory.EnumerateFileSystemEntries(target).Any())
                throw new RoostException($"{target} ya existe y no esta vacia: Roost no mezcla carpetas. Elige otra, o usa solo apuntar (sin --move-files) si el proyecto ya esta ahi.");
            if (!string.Equals(Path.GetPathRoot(source), Path.GetPathRoot(target), StringComparison.OrdinalIgnoreCase))
                throw new RoostException($"{target} esta en otra unidad: Roost solo mueve carpetas dentro de la misma. Muevela tu (copiar y comprobar) y luego usa solo apuntar (sin --move-files).");
            var parent = Path.GetDirectoryName(target)!;
            Directory.CreateDirectory(parent);
            try
            {
                if (Directory.Exists(target)) Directory.Delete(target);   // estaba vacía
                Directory.Move(source, target);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new RoostException(
                    $"No se ha podido mover {source} a {target}: {ex.Message} Algo tiene abierta la carpeta (el editor, una terminal, npm run dev...): "
                    + "cierralo y vuelve a probar, o muevela tu y usa solo apuntar (sin --move-files). No se ha tocado nada.");
            }
        }
        else if (!Directory.Exists(target))
        {
            throw new RoostException($"No existe {target}. Para llevar alli el proyecto, usa --move-files (o Mover la carpeta en la app); para un proyecto nuevo, roost site add.");
        }

        // La raíz web: la de antes si también está en la carpeta nueva; si no, la de Add (public/ si existe).
        string? root;
        if (site.Root is { } oldRoot && Directory.Exists(Path.Combine(target, oldRoot)))
            root = NormalizeRoot(target, oldRoot);
        else
            root = Directory.Exists(Path.Combine(target, "public")) ? "public" : null;

        var moved = site with
        {
            Folder = target,
            Root = root,
            Git = moveFiles ? site.Git : null,
            // Fuera de su carpeta aparcada, ya no es de ella (y no se quita sola si desaparece).
            ParkedIn = site.ParkedIn is { } parked && IsInside(target, parked) && SamePath(Path.GetDirectoryName(target)!, parked) ? parked : null,
        };
        Registry.Upsert(moved);
        return new SiteMoved(site, moved, moveFiles);
    }

    private static bool IsInside(string path, string folder)
    {
        var relative = Path.GetRelativePath(Path.GetFullPath(folder), Path.GetFullPath(path));
        return relative != "." && !relative.StartsWith("..", StringComparison.Ordinal) && !Path.IsPathRooted(relative);
    }

    /// <summary>Mueve un fichero o carpeta si existe y el destino no; lo que no se pueda (en uso), se queda. Devuelve el destino si lo ha movido.</summary>
    private static IEnumerable<string> MoveIfFree(string from, string to, bool directory = false)
    {
        try
        {
            if (directory)
            {
                if (!Directory.Exists(from) || Directory.Exists(to)) return [];
                Directory.CreateDirectory(Path.GetDirectoryName(to)!);
                Directory.Move(from, to);
            }
            else
            {
                if (!File.Exists(from) || File.Exists(to)) return [];
                File.Move(from, to);
            }
            return [to];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    // C7: alias y subdominios

    /// <summary>
    /// Añade un alias (<c>tienda-alias.test</c>) o un subdominio con nombre (<c>api.tienda.test</c>) al
    /// sitio, validado contra los demás (<see cref="SiteAliases.WhyNot"/>). Quien llama sincroniza el
    /// hosts y aplica el stack (el certificado se reemite en el siguiente <c>up</c>).
    /// </summary>
    public Site AddAlias(string name, string alias)
    {
        name = NormalizeName(name);
        var all = Registry.Load();
        var site = all.FirstOrDefault(s => s.Name == name)
            ?? throw new RoostException($"No hay ningun sitio '{name}'. Mira los registrados con: roost site list");
        var normalized = SiteAliases.Normalize(alias);
        if (SiteAliases.WhyNot(site, normalized, all) is { } why) throw new RoostException(why);
        var changed = site with { Aliases = [.. site.Aliases ?? [], normalized] };
        Registry.Upsert(changed);
        KeepProjectFile(changed, ProjectKeys.Aliases);
        return changed;
    }

    /// <summary>Quita un alias del sitio. Lanza si no lo tiene.</summary>
    public Site RemoveAlias(string name, string alias)
    {
        name = NormalizeName(name);
        var site = Registry.Find(name)
            ?? throw new RoostException($"No hay ningun sitio '{name}'. Mira los registrados con: roost site list");
        var normalized = SiteAliases.Normalize(alias);
        if (site.Aliases?.Contains(normalized, StringComparer.Ordinal) != true)
            throw new RoostException($"{site.Host} no tiene el alias {normalized}.");
        var rest = site.Aliases.Where(a => a != normalized).ToList();
        var changed = site with { Aliases = rest.Count == 0 ? null : rest };
        Registry.Upsert(changed);
        KeepProjectFile(changed, ProjectKeys.Aliases);
        return changed;
    }

    /// <summary>
    /// Deja los alias del sitio exactamente en <paramref name="aliases"/> (normalizados y validados; lanza
    /// con el primero que no vale y no cambia nada). Lo usa el diálogo «Alias…» de la app.
    /// </summary>
    public Site SetAliases(string name, IEnumerable<string> aliases)
    {
        name = NormalizeName(name);
        var all = Registry.Load();
        var site = all.FirstOrDefault(s => s.Name == name)
            ?? throw new RoostException($"No hay ningun sitio '{name}'. Mira los registrados con: roost site list");
        var valid = ValidAliases(site with { Aliases = null }, aliases, all, site.Host);
        var changed = site with { Aliases = valid.Count == 0 ? null : valid };
        Registry.Upsert(changed);
        KeepProjectFile(changed, ProjectKeys.Aliases);
        return changed;
    }

    /// <summary>
    /// Los alias de <paramref name="wanted"/> normalizados y validados para <paramref name="site"/> (lanza con
    /// el primero que no vale, diciendo de dónde viene). Sin repetidos.
    /// </summary>
    private static List<string> ValidAliases(Site site, IEnumerable<string> wanted, IReadOnlyList<Site> all, string source)
    {
        var accepted = new List<string>();
        foreach (var alias in wanted.Select(SiteAliases.Normalize).Distinct(StringComparer.Ordinal))
        {
            if (SiteAliases.WhyNot(site with { Aliases = accepted }, alias, all) is { } why)
                throw new RoostException($"{source}: {why}");
            accepted.Add(alias);
        }
        return accepted;
    }

    // C11: el .roost.toml del proyecto

    /// <summary>
    /// Si la carpeta del sitio tiene <c>.roost.toml</c>, le pasa lo que se acaba de cambiar
    /// (<paramref name="keys"/>) y que el fichero ya dice: así el siguiente <c>up</c> no lo deshace. Lo
    /// demás no se toca (el fichero puede decir algo que aún no se ha aplicado), y una clave que no
    /// tiene no se añade: no dice nada, y nada se deshace. Sin fichero, no crea ninguno (eso es
    /// <see cref="SaveProjectFile"/>). Nunca lanza: es un extra.
    /// </summary>
    public void KeepProjectFile(Site site, ProjectKeys keys)
    {
        try
        {
            if (keys == ProjectKeys.None || !Directory.Exists(site.Folder) || ProjectFile.Read(site.Folder) is not { } current) return;
            var wanted = current with
            {
                Php = current.Php is not null && keys.HasFlag(ProjectKeys.Php) ? site.Php ?? "global" : current.Php,
                Root = current.Root is not null && keys.HasFlag(ProjectKeys.Root) ? site.Root ?? "." : current.Root,
                Db = current.Db is not null && keys.HasFlag(ProjectKeys.Db) ? site.Database : current.Db,
                Aliases = current.Aliases is not null && keys.HasFlag(ProjectKeys.Aliases) ? site.Aliases ?? [] : current.Aliases,
            };
            if (wanted.Php == current.Php && wanted.Root == current.Root && wanted.Db == current.Db && SameList(wanted.Aliases, current.Aliases)) return;
            ProjectFile.Write(site.Folder, wanted);
        }
        catch (Exception ex) when (ex is RoostException or IOException or UnauthorizedAccessException)
        {
            // un .roost.toml roto o de solo lectura no impide el cambio: lo dirá el siguiente up
        }
    }

    /// <summary>Escribe (o crea) el <c>.roost.toml</c> del sitio con lo que tiene en el registro. Devuelve su ruta.</summary>
    public string SaveProjectFile(string name)
    {
        name = NormalizeName(name);
        var site = Registry.Find(name)
            ?? throw new RoostException($"No hay ningun sitio '{name}'. Mira los registrados con: roost site list");
        if (!Directory.Exists(site.Folder))
            throw new RoostException($"La carpeta de {site.Host} ({site.Folder}) ya no existe.");
        ProjectFile.Write(site.Folder, ProjectFile.For(site, ProjectFile.Read(site.Folder)));
        return ProjectFile.PathIn(site.Folder);
    }

    private static bool SameList(IReadOnlyList<string>? a, IReadOnlyList<string>? b) =>
        (a ?? []).SequenceEqual(b ?? [], StringComparer.Ordinal);

    /// <summary>
    /// Pone al día los sitios cuya carpeta tiene <c>.roost.toml</c>: lo que diga (php, root, db,
    /// aliases) pasa al registro. Una clave que no está no cambia nada. Lo que no se puede aplicar (un
    /// PHP que no está instalado, una raíz que no existe, un alias de otro sitio) se queda como estaba y
    /// sale en <see cref="ProjectSync.Warnings"/>. Lo llaman <c>roost up</c> (también el daemon) y la
    /// app al abrirse.
    /// </summary>
    public ProjectSync SyncProjectFiles()
    {
        var changes = new List<ProjectChange>();
        var warnings = new List<string>();
        var namesChanged = false;
        IReadOnlyList<Site> all = Registry.Load();
        IReadOnlyList<InstalledPhp>? installed = null;

        foreach (var site in all.ToList())
        {
            ProjectSettings? project;
            try { project = Directory.Exists(site.Folder) ? ProjectFile.Read(site.Folder) : null; }
            catch (RoostException ex) { warnings.Add(ex.Message); continue; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
            if (project is null) continue;

            var file = ProjectFile.PathIn(site.Folder);
            var next = site;
            var changed = false;

            if (project.Php is { } php)
            {
                var spec = php.Equals("global", StringComparison.OrdinalIgnoreCase) ? null : php;
                if (spec != site.Php)
                {
                    installed ??= new PhpInstaller(paths).ListInstalled();
                    if (spec is not null && PhpInstaller.Match(installed, spec).Count == 0)
                    {
                        warnings.Add($"{file} pide PHP {spec}, pero no esta instalado: {site.Host} sigue con {site.Php ?? "el global"} (roost php install {spec}).");
                    }
                    else
                    {
                        next = next with { Php = spec };
                        changes.Add(new ProjectChange(site.Name, $"PHP {spec ?? "global"}"));
                        changed = true;
                    }
                }
            }

            if (project.Root is { } root)
            {
                try
                {
                    var normalized = NormalizeRoot(site.Folder, root);
                    if (normalized != site.Root)
                    {
                        next = next with { Root = normalized };
                        changes.Add(new ProjectChange(site.Name, $"raiz web {normalized ?? "."}"));
                        changed = true;
                    }
                }
                catch (RoostException ex) { warnings.Add($"{file}: {ex.Message}"); }
            }

            if (project.Db is { } db && db != site.Database)
            {
                if (!DbAdmin.IsValidName(db) || DbAdmin.SystemSchemas.Contains(db))
                {
                    warnings.Add($"{file}: '{db}' no vale como base de datos de un sitio (solo letras, numeros y _, y ninguna del sistema).");
                }
                else
                {
                    next = next with { Database = db };
                    changes.Add(new ProjectChange(site.Name, $"base de datos {db}"));
                    changed = true;
                }
            }

            if (project.Aliases is { } aliases)
            {
                try
                {
                    var valid = ValidAliases(next with { Aliases = null }, aliases, all, file);
                    if (!SameList(valid, site.Aliases))
                    {
                        next = next with { Aliases = valid.Count == 0 ? null : valid };
                        changes.Add(new ProjectChange(site.Name, valid.Count == 0 ? "sin alias" : "alias " + string.Join(", ", valid)));
                        changed = true;
                        namesChanged = true;
                    }
                }
                catch (RoostException ex) { warnings.Add(ex.Message); }
            }

            if (!changed) continue;
            Registry.Upsert(next);
            all = Registry.Load();
        }

        return new ProjectSync(changes, warnings) { NamesChanged = namesChanged };
    }

    // C3: carpetas aparcadas

    /// <summary>Las carpetas aparcadas, en <c>state/parked.json</c>.</summary>
    public ParkedFolders Parked => new(paths);

    /// <summary>
    /// Aparca una carpeta: cada subcarpeta suya pasa a ser un sitio <c>&lt;subcarpeta&gt;.test</c> (con
    /// lo que diga su <c>.roost.toml</c>, si tiene), ahora y cada vez que se sincronice
    /// (<see cref="SyncParked"/>). Quien llama sincroniza el hosts y aplica el stack.
    /// </summary>
    public ParkSync Park(string folder)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder.Trim(), paths.Www));
        if (!Directory.Exists(full)) throw new RoostException($"No existe {full}.");
        var parked = Parked;
        var current = parked.Load();
        if (current.FirstOrDefault(p => IsInside(full, p)) is { } outer)
            throw new RoostException($"{full} esta dentro de {outer}, que ya esta aparcada: sus subcarpetas ya son sitios.");
        if (current.FirstOrDefault(p => IsInside(p, full)) is { } inner)
            throw new RoostException($"Dentro de {full} esta {inner}, que ya esta aparcada: dejala de aparcar antes (roost unpark).");
        parked.Add(full);
        return SyncParked();
    }

    /// <summary>
    /// Deja de aparcar una carpeta y quita sus sitios (los que salieron de ella, no los dados de alta a
    /// mano), como <see cref="Remove"/>: las carpetas no se tocan. Devuelve los quitados.
    /// </summary>
    public IReadOnlyList<Site> Unpark(string folder)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder.Trim(), paths.Www));
        var parked = Parked;
        var match = parked.Load().FirstOrDefault(p => SamePath(p, full))
            ?? throw new RoostException($"{full} no esta aparcada. Mira las aparcadas con: roost parked");
        parked.Remove(match);
        var removed = new List<Site>();
        foreach (var site in Registry.Load().Where(s => s.ParkedIn is { } p && SamePath(p, match)))
        {
            if (Remove(site.Name) is { } gone) removed.Add(gone);
        }
        return removed;
    }

    /// <summary>
    /// Deja los sitios de las carpetas aparcadas como están sus subcarpetas: una nueva da un sitio, y
    /// uno cuya carpeta ha desaparecido se quita (como <see cref="Remove"/>). Se saltan, y se dice, los
    /// nombres que no valen como <c>.test</c> y los que ya son de otro sitio; las carpetas con punto
    /// inicial (de trabajo, como <c>.git</c>) se saltan sin decir nada. Si una carpeta aparcada no está
    /// (un disco que no está puesto), sus sitios se quedan. Es idempotente.
    /// </summary>
    public ParkSync SyncParked()
    {
        var parked = Parked.Load();
        var sites = Registry.Load();
        if (parked.Count == 0 && sites.All(s => s.ParkedIn is null)) return ParkSync.None;

        var added = new List<Site>();
        var removed = new List<Site>();
        var skipped = new List<ParkSkip>();

        // Primero lo que ya no está: así un nombre que queda libre se puede volver a usar.
        foreach (var site in sites.Where(s => s.ParkedIn is not null))
        {
            var stillParked = parked.FirstOrDefault(p => SamePath(p, site.ParkedIn!));
            if (stillParked is null || (Directory.Exists(stillParked) && !Directory.Exists(site.Folder)))
            {
                if (Remove(site.Name) is { } gone) removed.Add(gone);
            }
        }

        foreach (var folder in parked)
        {
            if (!Directory.Exists(folder))
            {
                skipped.Add(new ParkSkip(folder, "la carpeta aparcada no esta ahora (un disco que no esta puesto?): sus sitios se quedan"));
                continue;
            }

            IEnumerable<string> subfolders;
            try { subfolders = Directory.EnumerateDirectories(folder).Order(StringComparer.OrdinalIgnoreCase).ToList(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                skipped.Add(new ParkSkip(folder, ex.Message));
                continue;
            }

            foreach (var sub in subfolders)
            {
                var leaf = Path.GetFileName(sub);
                if (leaf.StartsWith('.')) continue;   // de trabajo: .git, .tmp-…, .idea

                var current = Registry.Load();
                if (current.Any(s => SamePath(s.Folder, sub))) continue;   // ya es un sitio (quizá con otro nombre)

                ProjectSettings? project = null;
                try { project = ProjectFile.Read(sub); }
                catch (RoostException ex)
                {
                    skipped.Add(new ParkSkip(sub, ex.Message));
                    continue;
                }

                var name = (project?.Name ?? leaf).Trim().ToLowerInvariant();
                if (!Site.IsValidName(name))
                {
                    skipped.Add(new ParkSkip(sub, $"'{project?.Name ?? leaf}' no vale como nombre .test (solo minusculas, numeros y guiones, sin espacios ni acentos): renombra la carpeta o pon name en su {ProjectFile.FileName}"));
                    continue;
                }
                if (current.FirstOrDefault(s => s.Name == name) is { } taken)
                {
                    skipped.Add(new ParkSkip(sub, $"ya hay un sitio {taken.Host} en {taken.Folder}"));
                    continue;
                }

                try
                {
                    added.Add(AddCore(name, sub, root: null, php: null, parkedIn: folder).Site);
                }
                catch (RoostException ex)
                {
                    skipped.Add(new ParkSkip(sub, ex.Message));
                }
            }
        }

        return new ParkSync(added, removed, skipped);
    }
}
