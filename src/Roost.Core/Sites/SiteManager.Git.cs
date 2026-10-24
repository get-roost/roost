using System.Diagnostics;
using Roost.Core.Git;
using Roost.Core.Install;

namespace Roost.Core.Sites;

/// <summary>Cómo entrar en el repositorio de un sitio: el tipo (<see cref="SiteGit.Access"/>) y, con la clave de Roost, su nombre (el del sitio).</summary>
public sealed record SiteAccess(string Kind, string? KeyName = null)
{
    public static SiteAccess None { get; } = new(SiteGit.AccessNone);

    public static SiteAccess Token { get; } = new(SiteGit.AccessToken);

    public static SiteAccess Own { get; } = new(SiteGit.AccessOwn);

    public static SiteAccess Key(string name) => new(SiteGit.AccessKey, name);
}

/// <summary>Lo que se pide para clonar un sitio. Lo que va a null lo decide Roost (el nombre del repositorio, su rama por defecto, <c>www/&lt;nombre&gt;</c>, el PHP global).</summary>
/// <param name="Probe">Si ya se preguntó al remoto (la app lo hace en el paso 1), se reutiliza en vez de volver a preguntar.</param>
/// <param name="Access">Cómo entrar (null: sin credenciales; solo vale para repositorios públicos por https y carpetas locales).</param>
public sealed record CloneRequest(GitUrl Url, string? Name = null, string? Branch = null, string? Folder = null, string? Php = null, RemoteProbe? Probe = null, SiteAccess? Access = null);

/// <summary>Lo que se va a hacer, resuelto y validado antes de tocar nada.</summary>
public sealed record ClonePlan(GitUrl Url, string Name, string Folder, string? Branch);

/// <summary>Lo que dejó un clonado.</summary>
public sealed record CloneResult(SiteChange Change, string Branch, GitCommit? Head, RemoteProbe Probe, TimeSpan Elapsed);

/// <summary>
/// Lo que se quitó (o se quitaría) del acceso de un sitio al quitarlo: su clave de Roost y su token,
/// salvo que los use otro sitio. El historial y el registro de git se quedan.
/// </summary>
/// <param name="Token">El target del token del sitio, si tiene.</param>
/// <param name="DeployKeysUrl">Dónde quitar también la clave pública en el proveedor.</param>
public sealed record SiteAccessRemoval(
    string Site, string? KeyName, bool KeyRemoved, bool KeyShared,
    string? Token, bool TokenRemoved, bool TokenShared,
    string? DeployKeysUrl, string HistoryFile, string LogFile)
{
    /// <summary>true si el sitio tenía algo propio que quitar (aunque lo compartiera).</summary>
    public bool HasAccess => KeyName is not null || Token is not null;
}

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

    /// <summary>
    /// Las claves, los servidores conocidos y los tokens (el acceso de cada operación). Sin él, git va
    /// sin nada de Roost: solo repositorios públicos por https y carpetas locales.
    /// </summary>
    public GitAccessFactory? Access { get; init; }

    /// <summary>El acceso con el que se entra en el repositorio de un sitio.</summary>
    public GitAccess AccessFor(SiteGit info) => Access?.For(info) ?? GitAccess.None;

    /// <summary>El acceso para una dirección, con el tipo pedido (por defecto, ninguno).</summary>
    public GitAccess AccessFor(GitUrl url, SiteAccess? access) =>
        Access?.For(url, (access ?? SiteAccess.None).Kind, access?.KeyName) ?? GitAccess.None;

    private GitClient ClientFor(SiteGit info) => Client.With(AccessFor(info));

    public GitHistory History { get; } = new(paths);

    public GitLog Log { get; } = new(paths);

    /// <summary>Pregunta al remoto por sus ramas sin descargar nada (lo que hace «Comprobar»), sin acceso elegido.</summary>
    public Task<RemoteProbe> ProbeAsync(GitUrl url, CancellationToken cancellation) => ProbeAsync(url, null, cancellation);

    /// <summary>
    /// Como <see cref="ProbeAsync(GitUrl, CancellationToken)"/>, con un acceso (lo que hace «Probar
    /// acceso»). Lanza <see cref="GitException"/> si el acceso no casa con la dirección (SSH sin
    /// clave) y <see cref="RoostException"/> si falta la clave o el OpenSSH de Windows.
    /// </summary>
    public Task<RemoteProbe> ProbeAsync(GitUrl url, SiteAccess? access, CancellationToken cancellation) =>
        Client.With(AccessFor(url, access)).ProbeAsync(url, null, cancellation);

    /// <summary>
    /// Pregunta al remoto de un sitio con su acceso (<c>ls-remote</c>, sin descargar ni tocar la
    /// carpeta): lo que hacen «Probar acceso» y la comprobación de novedades. No lanza por lo que
    /// conteste el servidor (va en <see cref="RemoteProbe.Failure"/>); sí si el sitio no tiene git
    /// o le falta la clave o el OpenSSH.
    /// </summary>
    public Task<RemoteProbe> ProbeSiteAsync(string name, CancellationToken cancellation)
    {
        var (_, info) = RequireGit(name);
        var url = GitUrl.TryParse(info.Remote, out var parsed, out var why) ? parsed : throw new RoostException($"El remoto del sitio ({GitLog.Redact(info.Remote)}) no vale: {why}");
        return Client.With(AccessFor(info)).ProbeAsync(url, null, cancellation);
    }

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
        // El acceso se resuelve antes de tocar nada: si no casa (SSH sin clave), no queda ni historial.
        var access = AccessFor(url, request.Access);
        var client = Client.With(access);
        var watch = Stopwatch.StartNew();
        History.Clear(plan.Name);
        using var log = Log.Begin(plan.Name, GitHistory.Clone, $"{url.Normalized} -> {plan.Folder}");
        var tee = new Tee(lines, log);
        string? branch = plan.Branch;
        try
        {
            var probe = request.Probe ?? await client.ProbeAsync(url, tee, cancellation);
            if (!probe.Reachable) throw new GitException(probe.Failure ?? GitFailure.Classify(1, "", url));
            branch ??= probe.DefaultBranch ?? throw new RoostException($"{url.Label} esta vacio: todavia no tiene ninguna rama. Haz el primer commit y vuelve a intentarlo.");
            if (!probe.Branches.Contains(branch))
                throw new GitException(GitFailure.BranchNotFound(url.Label, branch, probe.Branches));

            var parent = Path.GetDirectoryName(plan.Folder) ?? plan.Folder;
            Directory.CreateDirectory(parent);
            // Solo en la www, y solo los .tmp-<guid> que deja un clonado cortado: las carpetas del
            // usuario no se tocan aunque se llamen parecido.
            if (SamePath(parent, paths.Www)) CleanCloneLeftovers(parent);
            var temp = Path.Combine(parent, InstallFiles.TempPrefix + Guid.NewGuid().ToString("N"));
            try
            {
                await client.CloneAsync(url, branch, temp, progress, tee, cancellation);
                // Comprobada vacía en PlanClone; si entre medias alguien metió algo, Delete falla y no se pisa.
                if (Directory.Exists(plan.Folder)) Directory.Delete(plan.Folder);
                // Renombrar es atómico en el mismo volumen: o es el sitio entero o no es nada. Reintenta
                // si el antivirus está leyendo lo que acaba de escribir git.
                InstallFiles.MoveDirectory(temp, plan.Folder);
            }
            catch
            {
                InstallFiles.TryDeleteDirectory(temp);
                throw;
            }

            var change = Add(plan.Name, plan.Folder, root: null, php: request.Php);
            var head = await client.HeadAsync(plan.Folder, cancellation);
            var site = change.Site with
            {
                Git = new SiteGit
                {
                    Remote = url.Normalized,
                    Branch = branch,
                    Provider = url.Provider,
                    Access = access.Kind,
                    KeyName = access.Kind == SiteGit.AccessKey ? access.KeyName : null,
                    CredentialTarget = access.Kind == SiteGit.AccessToken ? access.CredentialTarget : null,
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
            History.Append(plan.Name, new GitHistoryEntry(DateTimeOffset.Now, GitHistory.Clone, branch, null, null, false, watch.Elapsed.TotalSeconds, error)
            {
                ErrorKind = (ex as GitException)?.Failure.Kind.ToString(),
            });
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
            var client = ClientFor(info);
            var result = await client.PullFastForwardAsync(site.Folder, new Tee(lines, log), cancellation);
            var head = await client.HeadAsync(site.Folder, cancellation);
            var branch = await client.CurrentBranchAsync(site.Folder, cancellation) ?? info.Branch;
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
            var client = ClientFor(info);
            var before = (await client.HeadAsync(site.Folder, cancellation))?.ShortSha;
            await client.CheckoutAsync(site.Folder, branch, new Tee(lines, log), cancellation);
            var head = await client.HeadAsync(site.Folder, cancellation);
            var current = await client.CurrentBranchAsync(site.Folder, cancellation) ?? branch;
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
        // Sin fetch todo es local: no hace falta el acceso (ni el OpenSSH, ni que la clave siga ahí).
        var client = fetch ? ClientFor(info) : Client;
        if (fetch) await client.FetchAsync(site.Folder, null, cancellation);
        var status = await client.StatusAsync(site.Folder, cancellation);
        var head = await client.HeadAsync(site.Folder, cancellation);
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
        var (site, info) = RequireGit(name);
        var client = ClientFor(info);
        await client.FetchAsync(site.Folder, null, cancellation);
        return await client.RemoteBranchesAsync(site.Folder, cancellation);
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

    /// <summary>
    /// Quita el acceso de un sitio (al quitarlo): su clave de Roost y su token, salvo que los use otro
    /// de los sitios registrados (una clave con el mismo nombre en otro sitio; un token que sea el de
    /// otro, o que valga para el remoto de otro). El token del sitio es el que guardó al clonar o, si
    /// no, el de su repositorio exacto: nunca uno de grupo o de todo el servidor que no sea suyo. El
    /// historial y el registro de git se quedan. Con <paramref name="dryRun"/> solo dice lo que haría.
    /// </summary>
    public SiteAccessRemoval RemoveAccess(Site site, bool dryRun = false)
    {
        var history = History.FilePath(site.Name);
        var logFile = Log.FilePath(site.Name);
        if (Access is not { } access) return new SiteAccessRemoval(site.Name, null, false, false, null, false, false, null, history, logFile);

        var others = Registry.Load().Where(s => s.Name != site.Name).ToList();
        var url = site.Git is { } g && GitUrl.TryParse(g.Remote, out var parsed, out _) ? parsed : null;

        var keyName = site.Git is { Access: SiteGit.AccessKey, KeyName: { } k } ? k : site.Name;
        if (!access.Keys.Exists(keyName)) keyName = null;
        var keyShared = keyName is not null && others.Any(o => o.Git?.KeyName == keyName);
        var keyRemoved = keyName is not null && !keyShared && !dryRun && access.Keys.Remove(keyName);

        var token = site.Git?.CredentialTarget;
        if (token is null && url is not null && CredentialLocation.For(url) is { } location)
            token = access.Credentials.TargetOf(location);
        // Solo si sigue guardado (git lo borra si el servidor lo rechaza; el usuario, con roost git token remove).
        if (token is not null && access.Credentials.Store.Read(token) is null) token = null;
        var tokenShared = token is not null && others.Any(o => UsesToken(access.Credentials, o, token));
        var tokenRemoved = token is not null && !tokenShared && !dryRun && access.Credentials.Remove(token);

        return new SiteAccessRemoval(site.Name, keyName, keyRemoved, keyShared, token, tokenRemoved, tokenShared, url?.DeployKeysUrl, history, logFile);
    }

    /// <summary>true si el sitio usa ese token: es el suyo, o git lo daría para su remoto (uno de grupo o de servidor).</summary>
    private static bool UsesToken(GitCredentials credentials, Site site, string target)
    {
        if (site.Git is not { } info) return false;
        if (info.CredentialTarget == target) return true;
        return GitUrl.TryParse(info.Remote, out var url, out _)
            && CredentialLocation.For(url) is { } location
            && credentials.Candidates(location).Contains(target, StringComparer.OrdinalIgnoreCase);
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
        History.Append(site, new GitHistoryEntry(DateTimeOffset.Now, kind, branch, null, null, false, watch.Elapsed.TotalSeconds, error)
        {
            ErrorKind = (ex as GitException)?.Failure.Kind.ToString(),
        });
        log.End(false, error);
    }

    private static string? Short(string sha) => sha.Length == 0 ? null : sha[..Math.Min(7, sha.Length)];

    /// <summary>Borra los <c>.tmp-&lt;guid&gt;</c> (32 hexadecimales) que dejó un clonado cortado, y nada más.</summary>
    internal static void CleanCloneLeftovers(string www)
    {
        foreach (var child in Directory.EnumerateDirectories(www, InstallFiles.TempPrefix + "*"))
        {
            var id = Path.GetFileName(child)[InstallFiles.TempPrefix.Length..];
            if (id.Length == 32 && id.All(char.IsAsciiHexDigitLower))
                InstallFiles.TryDeleteDirectory(child);
        }
    }

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
