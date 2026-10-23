using System.Diagnostics;
using Roost.Core.Git;
using Roost.Core.Install;

namespace Roost.Core.Sites;

/// <summary>Lo que se pide para clonar un sitio. Lo que va a null lo decide Roost (el nombre del repositorio, su rama por defecto, <c>www/&lt;nombre&gt;</c>, el PHP global).</summary>
/// <param name="Probe">Si ya se preguntó al remoto (la app lo hace en el paso 1), se reutiliza en vez de volver a preguntar.</param>
public sealed record CloneRequest(GitUrl Url, string? Name = null, string? Branch = null, string? Folder = null, string? Php = null, RemoteProbe? Probe = null);

/// <summary>Lo que se va a hacer, resuelto y validado antes de tocar nada.</summary>
public sealed record ClonePlan(GitUrl Url, string Name, string Folder, string? Branch);

/// <summary>Lo que dejó un clonado.</summary>
public sealed record CloneResult(SiteChange Change, string Branch, GitCommit? Head, RemoteProbe Probe, TimeSpan Elapsed);

/// <summary>El estado del repositorio de un sitio, para el panel y para <c>roost site git</c>.</summary>
public sealed record SiteGitStatus(Site Site, SiteGit Git, RepoStatus Status, GitCommit? Head, IReadOnlyList<GitHistoryEntry> History)
{
    /// <summary>true si <see cref="Status"/> viene de después de un fetch (y «por detrás» es de verdad).</summary>
    public bool Fetched { get; init; }
}

/// <summary>
/// Sitios desde un repositorio (ROOST.md: git al estilo Plesk). Se clona en una carpeta temporal
/// al lado de la definitiva y se renombra al acabar: si algo falla o se cancela, no queda nada.
/// Actualizar es solo fast-forward y cambiar de rama solo con el árbol limpio: Roost nunca pisa
/// trabajo del usuario. Todo queda apuntado en <see cref="GitHistory"/> y en <see cref="GitLog"/>.
/// </summary>
public sealed partial class SiteManager
{
    private GitClient Client => git ?? throw new InvalidOperationException("Este SiteManager no tiene GitClient: construyelo con el git de GitTool.");

    public GitHistory History { get; } = new(paths);

    public GitLog Log { get; } = new(paths);

    /// <summary>Pregunta al remoto por sus ramas sin descargar nada (lo que hace «Comprobar»).</summary>
    public Task<RemoteProbe> ProbeAsync(GitUrl url, CancellationToken cancellation) => Client.ProbeAsync(url, null, cancellation);

    /// <summary>
    /// Resuelve y valida lo que se va a clonar sin tocar nada: el nombre (válido y libre), la carpeta
    /// (inexistente o vacía) y el PHP (instalado). Lanza <see cref="RoostException"/> si algo no cuadra.
    /// </summary>
    public ClonePlan PlanClone(CloneRequest request)
    {
        var name = (request.Name ?? request.Url.SuggestedSiteName).Trim().ToLowerInvariant();
        if (name.EndsWith(".test", StringComparison.Ordinal)) name = name[..^".test".Length];
        if (!Site.IsValidName(name))
            throw new RoostException($"'{name}' no vale como nombre de sitio: usa minusculas, numeros y guiones (por ejemplo tienda o mi-blog).");
        if (Registry.Find(name) is { } existing)
            throw new RoostException($"Ya hay un sitio '{name}' ({existing.Folder}). Elige otro nombre con --name, o quitalo antes: roost site remove {name}");

        var folder = request.Folder is { } f ? Path.GetFullPath(f, paths.Www) : Path.Combine(paths.Www, name);
        folder = Path.TrimEndingDirectorySeparator(folder);
        if (Directory.Exists(folder) && Directory.EnumerateFileSystemEntries(folder).Any())
            throw new RoostException($"{folder} ya existe y no esta vacia. Elige otra carpeta (--path) o, si ya es ese proyecto, registralo con roost site add: Roost detectara su .git.");
        if (File.Exists(folder))
            throw new RoostException($"{folder} es un fichero, no una carpeta.");

        if (request.Php is { } php && !php.Equals("global", StringComparison.OrdinalIgnoreCase)
            && PhpInstaller.Match(new PhpInstaller(paths).ListInstalled(), php).Count == 0)
            throw new RoostException($"No hay ningun PHP de Roost que case con '{php}'. Instalalo: roost php install {php}");

        return new ClonePlan(request.Url, name, folder, request.Branch);
    }

    /// <summary>
    /// Clona el repositorio como sitio nuevo: pregunta al remoto (rama por defecto y ramas), clona en
    /// <c>.tmp-…</c> junto a la carpeta final, la renombra, registra el sitio con <see cref="Add"/>
    /// (detecta <c>public/</c>) y guarda <see cref="Site.Git"/>. Si falla o se cancela, no queda ni
    /// carpeta ni sitio; sí la entrada del historial y el log.
    /// </summary>
    public async Task<CloneResult> CloneAsync(CloneRequest request, IProgress<CloneProgress>? progress, IProgress<string>? lines, CancellationToken cancellation)
    {
        var plan = PlanClone(request);
        var url = plan.Url;
        var watch = Stopwatch.StartNew();
        History.Clear(plan.Name);
        using var log = Log.Begin(plan.Name, GitHistory.Clone, $"{url.Normalized} -> {plan.Folder}");
        var tee = new Tee(lines, log);
        string? branch = plan.Branch;
        try
        {
            var probe = request.Probe ?? await Client.ProbeAsync(url, tee, cancellation);
            if (!probe.Reachable) throw new GitException(probe.Failure ?? GitFailure.Classify(1, "", url));
            branch ??= probe.DefaultBranch ?? throw new RoostException($"{url.Label} esta vacio: todavia no tiene ninguna rama. Haz el primer commit y vuelve a intentarlo.");
            if (!probe.Branches.Contains(branch))
                throw new GitException(GitFailure.BranchNotFound(url.Label, branch, probe.Branches));

            var parent = Path.GetDirectoryName(plan.Folder) ?? plan.Folder;
            Directory.CreateDirectory(parent);
            // Solo en la www, que es de Roost: fuera no se borran temporales de nadie.
            if (SamePath(parent, paths.Www)) InstallFiles.CleanLeftovers(parent);
            var temp = Path.Combine(parent, InstallFiles.TempPrefix + Guid.NewGuid().ToString("N"));
            try
            {
                await Client.CloneAsync(url, branch, temp, progress, tee, cancellation);
                // Comprobada vacía en PlanClone; si entre medias alguien metió algo, Delete falla y no se pisa.
                if (Directory.Exists(plan.Folder)) Directory.Delete(plan.Folder);
                // Renombrar es atómico en el mismo volumen: o es el sitio entero o no es nada.
                Directory.Move(temp, plan.Folder);
            }
            catch
            {
                InstallFiles.TryDeleteDirectory(temp);
                throw;
            }

            var change = Add(plan.Name, plan.Folder, root: null, php: request.Php);
            var head = await Client.HeadAsync(plan.Folder, cancellation);
            var site = change.Site with
            {
                Git = new SiteGit
                {
                    Remote = url.Normalized,
                    Branch = branch,
                    Provider = url.Provider,
                    Access = SiteGit.AccessNone,
                    LastPullUtc = DateTimeOffset.UtcNow,
                    LastCommit = head,
                },
            };
            Registry.Upsert(site);
            watch.Stop();
            History.Append(plan.Name, new GitHistoryEntry(DateTimeOffset.Now, GitHistory.Clone, branch, null, head?.ShortSha, true, watch.Elapsed.TotalSeconds, null));
            log.End(true, $"{head?.ShortSha ?? "?"} ({branch}) en {watch.Elapsed.TotalSeconds:0.0} s");
            return new CloneResult(change with { Site = site }, branch, head, probe, watch.Elapsed);
        }
        catch (Exception ex) when (ex is RoostException or OperationCanceledException)
        {
            watch.Stop();
            var error = ex is OperationCanceledException ? "Cancelado." : ex.Message;
            History.Append(plan.Name, new GitHistoryEntry(DateTimeOffset.Now, GitHistory.Clone, branch, null, null, false, watch.Elapsed.TotalSeconds, error));
            log.End(false, error);
            throw;
        }
    }

    /// <summary>
    /// Actualiza el sitio con un fast-forward, solo si el árbol está limpio. Si no (cambios locales,
    /// historia divergente), lanza <see cref="GitException"/> sin tocar nada. Devuelve qué cambió.
    /// </summary>
    public async Task<PullResult> PullAsync(string name, IProgress<string>? lines, CancellationToken cancellation)
    {
        var (site, info) = RequireGit(name);
        var watch = Stopwatch.StartNew();
        using var log = Log.Begin(site.Name, GitHistory.Pull, $"{info.Remote} ({info.Branch})");
        try
        {
            var result = await Client.PullFastForwardAsync(site.Folder, new Tee(lines, log), cancellation);
            var head = await Client.HeadAsync(site.Folder, cancellation);
            var branch = await Client.CurrentBranchAsync(site.Folder, cancellation) ?? info.Branch;
            Registry.Upsert(site with { Git = info with { Branch = branch, LastPullUtc = DateTimeOffset.UtcNow, LastCommit = head } });
            watch.Stop();
            History.Append(site.Name, new GitHistoryEntry(DateTimeOffset.Now, GitHistory.Pull, branch, Short(result.From), Short(result.To), true, watch.Elapsed.TotalSeconds, null));
            log.End(true, result.Changed ? $"{Short(result.From)} -> {Short(result.To)}, {result.ChangedFiles} fichero(s)" : "ya estaba al dia");
            return result;
        }
        catch (Exception ex) when (ex is RoostException or OperationCanceledException)
        {
            Fail(site.Name, GitHistory.Pull, info.Branch, watch, log, ex);
            throw;
        }
    }

    /// <summary>Cambia el sitio de rama (local o del remoto), solo con el árbol limpio. Devuelve el sitio ya cambiado.</summary>
    public async Task<Site> CheckoutAsync(string name, string branch, IProgress<string>? lines, CancellationToken cancellation)
    {
        var (site, info) = RequireGit(name);
        var watch = Stopwatch.StartNew();
        using var log = Log.Begin(site.Name, GitHistory.Checkout, $"{info.Branch} -> {branch}");
        try
        {
            var before = (await Client.HeadAsync(site.Folder, cancellation))?.ShortSha;
            await Client.CheckoutAsync(site.Folder, branch, new Tee(lines, log), cancellation);
            var head = await Client.HeadAsync(site.Folder, cancellation);
            var current = await Client.CurrentBranchAsync(site.Folder, cancellation) ?? branch;
            var changed = site with { Git = info with { Branch = current, LastCommit = head } };
            Registry.Upsert(changed);
            watch.Stop();
            History.Append(site.Name, new GitHistoryEntry(DateTimeOffset.Now, GitHistory.Checkout, current, before, head?.ShortSha, true, watch.Elapsed.TotalSeconds, null));
            log.End(true, $"{current} ({head?.ShortSha ?? "?"})");
            return changed;
        }
        catch (Exception ex) when (ex is RoostException or OperationCanceledException)
        {
            Fail(site.Name, GitHistory.Checkout, branch, watch, log, ex);
            throw;
        }
    }

    /// <summary>
    /// El estado del repositorio del sitio: rama, por delante o por detrás, cambios locales y último
    /// commit. Con <paramref name="fetch"/>, antes trae lo nuevo del remoto (red), y «por detrás» es de hoy.
    /// </summary>
    public async Task<SiteGitStatus> GitStatusAsync(string name, bool fetch, CancellationToken cancellation)
    {
        var (site, info) = RequireGit(name);
        if (fetch) await Client.FetchAsync(site.Folder, null, cancellation);
        var status = await Client.StatusAsync(site.Folder, cancellation);
        var head = await Client.HeadAsync(site.Folder, cancellation);
        if (head is not null && (info.LastCommit?.Sha != head.Sha || (status.Branch is not null && status.Branch != info.Branch)))
        {
            // El usuario trabaja con su git: lo que Roost recuerda se pone al día sin que cuente como operación.
            info = info with { LastCommit = head, Branch = status.Branch ?? info.Branch };
            Registry.Upsert(site with { Git = info });
        }
        return new SiteGitStatus(site, info, status, head, History.Load(site.Name)) { Fetched = fetch };
    }

    /// <summary>Las ramas de <c>origin</c> del sitio, tras un fetch (red).</summary>
    public async Task<IReadOnlyList<string>> RemoteBranchesAsync(string name, CancellationToken cancellation)
    {
        var (site, _) = RequireGit(name);
        await Client.FetchAsync(site.Folder, null, cancellation);
        return await Client.RemoteBranchesAsync(site.Folder, cancellation);
    }

    /// <summary>
    /// Adopta el <c>.git</c> que ya tiene la carpeta de un sitio: lee <c>remote.origin.url</c> y la rama
    /// y los guarda como <see cref="SiteGit"/> con acceso <see cref="SiteGit.AccessOwn"/> (lo que tenga
    /// el usuario; Roost no guarda credenciales). Devuelve el sitio ya adoptado.
    /// </summary>
    public async Task<Site> AdoptGitAsync(string name, CancellationToken cancellation)
    {
        var site = Require(name);
        if (!GitClient.IsRepository(site.Folder))
            throw new RoostException($"{site.Folder} no es un repositorio git (no tiene .git). Para clonar uno nuevo: roost site clone <url>");

        var remote = await Client.RemoteUrlAsync(site.Folder, cancellation)
            ?? throw new RoostException($"El repositorio de {site.Host} no tiene remoto origin: no hay de donde actualizar.");
        if (!GitUrl.TryParse(remote, out var url, out var why))
            throw new RoostException($"El remoto origin de {site.Host} ({GitLog.Redact(remote)}) no vale: {why}");
        var branch = await Client.CurrentBranchAsync(site.Folder, cancellation)
            ?? throw new RoostException($"El repositorio de {site.Host} no esta en ninguna rama (HEAD suelto). Cambia a una rama y vuelve a intentarlo.");
        var head = await Client.HeadAsync(site.Folder, cancellation);

        var adopted = site with
        {
            Git = new SiteGit { Remote = url.Normalized, Branch = branch, Provider = url.Provider, Access = SiteGit.AccessOwn, LastCommit = head },
        };
        Registry.Upsert(adopted);
        History.Append(site.Name, new GitHistoryEntry(DateTimeOffset.Now, GitHistory.Adopt, branch, null, head?.ShortSha, true, 0, null));
        return adopted;
    }

    private Site Require(string name)
    {
        name = NormalizeName(name);
        return Registry.Find(name)
            ?? throw new RoostException($"No hay ningun sitio '{name}'. Mira los registrados con: roost site list");
    }

    private (Site Site, SiteGit Git) RequireGit(string name)
    {
        var site = Require(name);
        if (site.Git is { } info) return (site, info);
        if (GitClient.IsRepository(site.Folder))
            throw new RoostException($"{site.Host} tiene un repositorio git que Roost no gestiona todavia. Adoptalo: roost site git {site.Name} --adopt");
        throw new RoostException($"{site.Host} no viene de ningun repositorio git. Para crear un sitio desde uno: roost site clone <url>");
    }

    private void Fail(string site, string kind, string? branch, Stopwatch watch, GitLog.Session log, Exception ex)
    {
        watch.Stop();
        var error = ex is OperationCanceledException ? "Cancelado." : ex.Message;
        History.Append(site, new GitHistoryEntry(DateTimeOffset.Now, kind, branch, null, null, false, watch.Elapsed.TotalSeconds, error));
        log.End(false, error);
    }

    private static string? Short(string sha) => sha.Length == 0 ? null : sha[..Math.Min(7, sha.Length)];

    /// <summary>Cada línea va al log del sitio y, si alguien escucha (la pantalla), también a él.</summary>
    private sealed class Tee(IProgress<string>? listener, GitLog.Session log) : IProgress<string>
    {
        public void Report(string value)
        {
            log.Report(value);
            listener?.Report(value);
        }
    }
}
