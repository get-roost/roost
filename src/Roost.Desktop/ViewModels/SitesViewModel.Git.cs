using System.Globalization;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Roost.Core;
using Roost.Core.Config;
using Roost.Core.Git;
using Roost.Core.Install;
using Roost.Core.Scanning;
using Roost.Core.Sites;
using Roost.Core.Stack;
using Roost.Desktop.Controls;
using Roost.Desktop.Services;

namespace Roost.Desktop.ViewModels;

/// <summary>Git-app: la columna «Git» de la lista de sitios.</summary>
public sealed partial class SiteRow
{
    /// <summary>«al día», «3 ↓», «2 ✎», «nuevo ↓», «sin acceso», la rama sin comprobar o «—» sin git.</summary>
    [ObservableProperty] public partial string GitChip { get; set; } = "—";

    [ObservableProperty] public partial Tone GitTone { get; set; } = Tone.Neutral;

    [ObservableProperty] public partial string? GitTip { get; set; }
}

/// <summary>Lo que se sabe en esta sesión del repositorio de un sitio (nada de esto va a disco).</summary>
internal sealed class SiteGitState
{
    /// <summary>El estado local de la última lectura (cambios, por delante o por detrás de lo último que se trajo).</summary>
    public RepoStatus? Status { get; set; }

    public GitCommit? Head { get; set; }

    /// <summary>Lo que Roost recuerda del sitio tras la última lectura (rama y último commit al día).</summary>
    public SiteGit? Git { get; set; }

    /// <summary><see cref="Status"/> viene de después de un fetch o una actualización: «por detrás» es de ahora.</summary>
    public bool Fetched { get; set; }

    /// <summary>La última comprobación de novedades (ls-remote).</summary>
    public SiteUpdateCheck? Check { get; set; }

    /// <summary>El último fallo de git al hablar con el remoto.</summary>
    public GitFailure? Failure { get; set; }

    /// <summary>Un fallo que no es de git (falta la clave o el OpenSSH).</summary>
    public string? Error { get; set; }

    /// <summary>Cuándo se habló con el remoto por última vez (bien o mal).</summary>
    public DateTimeOffset? When { get; set; }

    public IReadOnlyList<string> Branches { get; set; } = [];

    /// <summary>De un <c>.git</c> que Roost no gestiona: <c>acme/tienda</c>, para «repositorio detectado».</summary>
    public string? RemoteLabel { get; set; }
}

/// <summary>Cómo se pinta el estado de git de un sitio: el chip del panel, el de la lista y la nota.</summary>
internal sealed record GitLook(string Chip, Tone Tone, string Row, string? Note, Tone NoteTone)
{
    public bool Behind { get; init; }
    public bool NoAccess { get; init; }
    public bool Changes { get; init; }
    public bool Unchecked { get; init; }
}

/// <summary>
/// Git-app (oleada 4): el bloque Git del panel del sitio (maqueta F del diseño de git) y lo que
/// sale de él: la rama con su desplegable (solo con el árbol limpio), el chip del estado, el último
/// commit, «Actualizar» (solo fast-forward), «Historial», «Acceso…», «Comprobar ahora» y, para un
/// <c>.git</c> que Roost no gestiona, «Adoptar». Además «Desde git…» (el asistente y el diálogo
/// Clonando), la columna Git de la lista, la casilla de la clave y el token al quitar un sitio y la
/// comprobación de novedades cada N minutos (I8: <c>ls-remote</c>, una a la vez, sin avisos).
/// </summary>
public sealed partial class SitesViewModel
{
    private GitContext? _git;
    private readonly Dictionary<string, SiteGitState> _gitStates = new(StringComparer.Ordinal);
    private string? _gitFor;
    private bool _settingGitBranch;
    private DispatcherTimer? _gitTimer;
    private bool _gitFirstCheck;
    private bool _gitChecking;

    /// <summary>El git de la app (runner, acceso); los tests ponen uno guionizado, con tokens en memoria y un OpenSSH de pega.</summary>
    internal GitContext Git
    {
        get => _git ??= new GitContext(Main.App.Paths, CommandRunner.Default);
        set => _git = value;
    }

    /// <summary>Aplica el sitio recién clonado al stack (o lo arranca); los tests lo cambian para no hablar con ningún daemon.</summary>
    internal Func<Task<bool>>? ApplyForClone { get; set; }

    /// <summary>Abre una página en el navegador; los tests lo cambian para no abrir nada.</summary>
    internal Action<string> OpenBrowser { get; set; } = Shell.OpenUrl;

    private SiteGitState StateOf(string site)
    {
        if (!_gitStates.TryGetValue(site, out var state)) _gitStates[site] = state = new SiteGitState();
        return state;
    }

    // El panel

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(GitCanUpdate), nameof(GitCanBranch))]
    public partial bool HasGit { get; private set; }

    [ObservableProperty] public partial bool GitDetected { get; private set; }

    [ObservableProperty] public partial string GitDetectedText { get; private set; } = "";

    [ObservableProperty] public partial string GitRepoText { get; private set; } = "";

    [ObservableProperty] public partial IReadOnlyList<string> GitBranches { get; private set; } = [];

    [ObservableProperty] public partial string? GitBranch { get; set; }

    [ObservableProperty] public partial string GitChipText { get; private set; } = "";

    [ObservableProperty] public partial Tone GitChipTone { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasGitCommit))]
    public partial string GitCommitSha { get; private set; } = "";

    [ObservableProperty] public partial string GitCommitSubject { get; private set; } = "";

    [ObservableProperty] public partial string GitCommitMeta { get; private set; } = "";

    public bool HasGitCommit => GitCommitSha.Length > 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasGitNote))]
    public partial string? GitNote { get; private set; }

    [ObservableProperty] public partial Tone GitNoteTone { get; private set; } = Tone.Warn;

    public bool HasGitNote => GitNote is not null;

    /// <summary>Git está trabajando en este sitio (actualizar, cambiar de rama, comprobar): los botones esperan.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(GitIdle), nameof(GitCanUpdate), nameof(GitCanBranch))]
    public partial bool GitBusy { get; private set; }

    public bool GitIdle => !GitBusy;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(GitCanUpdate), nameof(GitCanBranch))]
    public partial bool GitDirty { get; private set; }

    [ObservableProperty] public partial bool GitUpdatePrimary { get; private set; }

    [ObservableProperty] public partial bool GitAccessPrimary { get; private set; }

    [ObservableProperty] public partial bool GitShowCheckNow { get; private set; }

    public bool GitCanUpdate => HasGit && !GitBusy && !GitDirty;

    public bool GitCanBranch => HasGit && !GitBusy && !GitDirty;

    public string GitCheckHint => Main.App.Prefs.GitCheckUpdates
        ? $"Mira si hay novedades cada {Main.App.Prefs.GitCheckMinutes} min con la app abierta; nunca descarga nada sin que lo pidas."
        : "No se miran las novedades solas (Ajustes › Git); nunca descarga nada sin que lo pidas.";

    partial void OnSelectedChanged(SiteRow? oldValue, SiteRow? newValue) => PickGit(newValue?.Site);

    partial void OnRowsChanged(IReadOnlyList<SiteRow> value)
    {
        foreach (var row in value) PaintRow(row);
        _ = WarmGitAsync(value.Select(r => r.Site).Where(s => s.Git is not null && !_gitStates.ContainsKey(s.Name)).ToList());
    }

    partial void OnGitBranchChanged(string? value)
    {
        if (_settingGitBranch || value is null || Selected is not { } row || row.Site.Git is null) return;
        var current = StateOf(row.Site.Name).Status?.Branch ?? row.Site.Git.Branch;
        if (value == current) return;
        _ = ChangeBranchAsync(row, value);
    }

    private void PickGit(Site? site)
    {
        _gitFor = site?.Name;
        HasGit = site?.Git is not null;
        GitDetected = site is { Git: null } && Directory.Exists(site.Folder) && GitClient.IsRepository(site.Folder);
        OnPropertyChanged(nameof(GitCheckHint));
        if (site is null) return;
        if (GitDetected)
        {
            var label = StateOf(site.Name).RemoteLabel;
            GitDetectedText = label is null ? "repositorio detectado" : $"repositorio detectado · {label}";
            if (label is null) _ = DetectRemoteAsync(site);
            return;
        }
        if (site.Git is null) return;
        ShowGit(site);
        _ = RefreshGitAsync(site.Name, fetch: false);
    }

    /// <summary>Pinta el bloque con lo que recuerda el registro y lo que se sabe en esta sesión.</summary>
    private void ShowGit(Site site)
    {
        var state = StateOf(site.Name);
        var info = state.Git ?? site.Git;
        if (info is null) return;
        var url = GitUrl.TryParse(info.Remote, out var parsed, out _) ? parsed : null;
        GitRepoText = url is null ? info.Remote : url.IsLocal ? $"{url.Label} · carpeta local · {GitTexts.AccessLabel(info)}" : $"{url.Label} · {url.ProviderName} · {GitTexts.AccessLabel(info)}";

        var branch = state.Status?.Branch ?? info.Branch;
        var branches = state.Branches.Count > 0 ? state.Branches : [];
        if (!branches.Contains(branch)) branches = [branch, .. branches];
        _settingGitBranch = true;
        GitBranches = branches;
        GitBranch = branch;
        _settingGitBranch = false;

        var head = state.Head ?? info.LastCommit;
        GitCommitSha = head?.ShortSha ?? "";
        GitCommitSubject = head is null ? "" : $"«{head.Subject}»";
        GitCommitMeta = head is null ? "" : $"{GitTexts.When(head.When)} · {head.Author}";

        var look = Look(info, state, site.Host);
        GitChipText = look.Chip;
        GitChipTone = look.Tone;
        GitNote = look.Note;
        GitNoteTone = look.NoteTone;
        GitDirty = look.Changes;
        GitUpdatePrimary = look.Behind;
        GitAccessPrimary = look.NoAccess;
        GitShowCheckNow = look.Unchecked;
    }

    private void PaintRow(SiteRow row)
    {
        var site = row.Site;
        if (site.Git is null)
        {
            var detected = StateOf(site.Name).RemoteLabel is not null || (Directory.Exists(site.Folder) && GitClient.IsRepository(site.Folder));
            row.GitChip = detected ? "sin adoptar" : "—";
            row.GitTone = Tone.Neutral;
            row.GitTip = detected ? "Tiene un repositorio git que Roost no gestiona: Adoptar, en su panel." : "No viene de ningún repositorio git.";
            return;
        }
        var state = _gitStates.GetValueOrDefault(site.Name);
        var look = Look(state?.Git ?? site.Git, state, site.Host);
        row.GitChip = look.Row;
        row.GitTone = look.Tone == Tone.Accent ? Tone.Neutral : look.Tone;
        row.GitTip = look.Chip;
    }

    private void Repaint(string site)
    {
        foreach (var row in _all.Where(r => r.Site.Name == site)) PaintRow(row);
        if (_gitFor == site && Selected is { } selected && selected.Site.Name == site) ShowGit(selected.Site);
    }

    /// <summary>
    /// El estado que se enseña, del más urgente al más tranquilo: cambios locales, sin acceso, no se
    /// pudo comprobar, HEAD suelto, divergente, por detrás (contado), novedades sin contar, por
    /// delante, sin rama remota, al día (si se ha comprobado) o sin comprobar.
    /// </summary>
    internal static GitLook Look(SiteGit git, SiteGitState? state, string host)
    {
        var status = state?.Status;
        var check = state?.Check;
        var failure = state?.Failure;
        var url = GitUrl.TryParse(git.Remote, out var parsed, out _) ? parsed : null;
        var key = git.Access == SiteGit.AccessKey ? git.KeyName : null;
        var own = git.Access == SiteGit.AccessOwn;

        if (status is { IsClean: false })
        {
            var n = status.Changed.Count;
            return new GitLook(n == 1 ? "1 cambio local" : $"{n} cambios locales", Tone.Warn, $"{n} ✎",
                "Actualizar y cambiar de rama se desactivan hasta que los guardes o descartes.", Tone.Warn) { Changes = true };
        }
        if (state?.Error is { } error)
            return new GitLook("sin acceso", Tone.Bad, "sin acceso", error, Tone.Bad) { NoAccess = true };
        if (failure is { } f && IsAccessFailure(f.Kind))
            return new GitLook($"sin acceso: {ShortReason(f.Kind)}", Tone.Bad, "sin acceso", GitTexts.Explain(f, url, key, host, branch: git.Branch, ownKey: own), Tone.Bad) { NoAccess = true };
        if (failure is { } other)
            return new GitLook("no se pudo comprobar", Tone.Neutral, git.Branch, GitTexts.Explain(other, url, key, host, branch: git.Branch, ownKey: own), Tone.Warn) { Unchecked = true };
        if (status is { IsDetached: true })
            return new GitLook("HEAD suelto", Tone.Warn, "suelto", "El checkout no está en ninguna rama (HEAD suelto). Vuelve a una rama con tu git para poder actualizar.", Tone.Warn);
        if (status is { Behind: > 0, Ahead: > 0 })
            return new GitLook("divergente", Tone.Bad, $"{status.Behind} ↓ {status.Ahead} ↑",
                GitTexts.Explain(new GitFailure(GitFailureKind.Diverged, "", ""), url, branch: status.Branch ?? git.Branch), Tone.Bad);
        var behind = status is { Behind: > 0 } ? status.Behind : check is { HasNews: true, Behind: > 0 } ? check.Behind!.Value : 0;
        if (behind > 0)
            return new GitLook(behind == 1 ? "1 commit por detrás" : $"{behind} commits por detrás", Tone.Warn, $"{behind} ↓", null, Tone.Warn) { Behind = true };
        if (check is { HasNews: true })
            return new GitLook("hay novedades", Tone.Warn, "nuevo ↓", "El remoto tiene commits nuevos: Actualizar los trae (solo fast-forward).", Tone.Accent) { Behind = true };
        if (status is { Ahead: > 0 })
            return new GitLook(status.Ahead == 1 ? "1 commit por delante" : $"{status.Ahead} commits por delante", Tone.Accent, $"{status.Ahead} ↑",
                "Tienes commits que no están en el remoto: súbelos con tu git (git push).", Tone.Accent);
        if (status is { HasUpstream: false })
            return new GitLook("sin rama remota", Tone.Warn, git.Branch, "La rama no sigue a ninguna de origin: Roost no sabe de dónde actualizarla.", Tone.Warn);
        if (state is { Fetched: true } || check is { Ok: true })
            return new GitLook("al día", Tone.Ok, "al día", null, Tone.Ok);
        return new GitLook("sin comprobar", Tone.Neutral, git.Branch, null, Tone.Neutral) { Unchecked = true };
    }

    private static bool IsAccessFailure(GitFailureKind kind) => kind is GitFailureKind.AuthRequired or GitFailureKind.KeyRejected
        or GitFailureKind.HostKeyUnverified or GitFailureKind.HostKeyChanged or GitFailureKind.KeyRequired or GitFailureKind.KeyUnusable
        or GitFailureKind.TokenRejected or GitFailureKind.TokenForbidden or GitFailureKind.PasswordNotAccepted or GitFailureKind.AccessMismatch;

    private static string ShortReason(GitFailureKind kind) => kind switch
    {
        GitFailureKind.TokenRejected => "el token no vale",
        GitFailureKind.TokenForbidden => "falta autorizar el token",
        GitFailureKind.PasswordNotAccepted => "hace falta un token",
        GitFailureKind.KeyRejected => "la clave no vale",
        GitFailureKind.KeyRequired => "falta la clave",
        GitFailureKind.KeyUnusable => "la clave no se puede usar",
        GitFailureKind.HostKeyChanged => "la huella cambió",
        GitFailureKind.HostKeyUnverified => "huella sin aceptar",
        GitFailureKind.AccessMismatch => "el acceso no casa",
        _ => "pide acceso",
    };

    /// <summary>Lee el estado local (y, con <paramref name="fetch"/>, trae antes lo nuevo: lo pidió el usuario) y repinta.</summary>
    private async Task RefreshGitAsync(string name, bool fetch)
    {
        if (Git.TryManager() is not { } manager) return;   // sin git instalado: lo que recuerda el registro
        var state = StateOf(name);
        try
        {
            var status = await Task.Run(() => manager.GitStatusAsync(name, fetch, CancellationToken.None));
            state.Status = status.Status;
            state.Head = status.Head;
            state.Git = status.Git;
            if (fetch)
            {
                state.Fetched = true;
                state.Check = null;
                state.Failure = null;
                state.Error = null;
                state.When = DateTimeOffset.Now;
            }
            if (Git.Locate() is { } git)
            {
                var branches = await Git.Client(git).RemoteBranchesAsync(status.Site.Folder, CancellationToken.None);
                if (branches.Count > 0) state.Branches = branches;
            }
        }
        catch (GitException ex) when (fetch)
        {
            state.Failure = ex.Failure;
            state.Error = null;
            state.When = DateTimeOffset.Now;
        }
        catch (Exception ex) when (fetch && ex is RoostException or IOException or UnauthorizedAccessException)
        {
            state.Error = ex.Message;
            state.When = DateTimeOffset.Now;
        }
        catch (Exception ex) when (ex is RoostException or IOException or UnauthorizedAccessException)
        {
            // Leer el estado local no salió (la carpeta ya no está, no es un repositorio): se queda lo que recuerda el registro.
        }
        Repaint(name);
    }

    /// <summary>El estado local de los sitios de git que aún no se han mirado, uno detrás de otro (para los chips de la lista; sin red).</summary>
    private async Task WarmGitAsync(IReadOnlyList<Site> sites)
    {
        if (sites.Count == 0 || Git.TryManager() is null) return;
        foreach (var site in sites)
        {
            if (_gitStates.ContainsKey(site.Name)) continue;
            StateOf(site.Name);
            await RefreshGitAsync(site.Name, fetch: false);
        }
    }

    /// <summary>De un .git sin adoptar, a qué remoto apunta (acme/tienda), para «repositorio detectado».</summary>
    private async Task DetectRemoteAsync(Site site)
    {
        GitLocation? git;
        try { git = Git.Locate(); } catch (RoostException) { return; }
        if (git is null) return;
        try
        {
            var remote = await Git.Client(git).RemoteUrlAsync(site.Folder, CancellationToken.None);
            if (remote is null || !GitUrl.TryParse(remote, out var url, out _)) return;
            StateOf(site.Name).RemoteLabel = url.IsLocal ? url.Label : $"{url.Label} · {url.ProviderName}";
            if (_gitFor == site.Name) GitDetectedText = $"repositorio detectado · {StateOf(site.Name).RemoteLabel}";
        }
        catch (RoostException) { }
    }

    /// <summary>
    /// El git que usa Roost. La primera vez instala el MinGit verificado con el diálogo Instalando,
    /// como Composer o nginx. null si no hay git (cancelado, fallido o el de Ajustes › Git no existe).
    /// </summary>
    internal async Task<GitLocation?> EnsureGitAsync()
    {
        try
        {
            if (Git.Locate() is { } found) return found;
        }
        catch (RoostException)
        {
            var exe = Git.Config().GitExe;
            await Main.InfoAsync("No se encuentra tu git", $"El git de Ajustes › Git ({exe}) no existe. Arregla la ruta o vuelve al MinGit de Roost.", Tone.Bad);
            return null;
        }

        var paths = Main.App.Paths;
        var build = GitCatalog.Windows;
        GitLocation? installed = null;
        var steps = new List<InstallStep>
        {
            new("git fijado en Roost", $"· {build.Label}, verificado con su SHA-256"),
            new("Descargando MinGit", $"· de {build.Url.Host}"),
            new("Verificar el SHA-256", "· antes de extraer nada"),
            new($"Instalar en {Path.Combine(paths.GitBin, build.Version)}", null),
        };
        var dialog = new InstallDialogViewModel($"Instalando {build.Label}", $"La primera vez que se usa git · desde {build.Url.Host}", null, steps,
            async (d, cancel) =>
            {
                d.Step(1, "Descargando");
                var bytes = new Progress<long>(d.Downloaded);
                try { installed = await GitTool.EnsureAsync(paths, Git.Config(), Main.App.Http, bytes, cancel); }
                catch (RoostException ex) { throw new RoostException(GitTexts.GitSetupFailed(ex.Message), ex); }
                d.Step(3, "Instalando");
            });
        var run = dialog.RunAsync();
        var shown = await Main.ShowDialogAsync(dialog);
        await run;
        if (!await dialog.Finished)
        {
            if (shown is null && dialog.Failed) await Main.InfoAsync($"No se instaló {build.Label}", dialog.Error!, Tone.Bad);
            return null;
        }
        return installed;
    }

    // Desde git…

    [RelayCommand]
    private Task FromGitAsync() => OpenCloneWizardAsync();

    /// <summary>«Desde git…»: el asistente de tres pasos y, si se acepta, el clonado. También lo puede abrir Nuevo sitio.</summary>
    public async Task OpenCloneWizardAsync()
    {
        var wizard = new CloneWizardViewModel(Main, Git, EnsureGitAsync, OpenBrowser);
        if (await Main.ShowDialogAsync(wizard) is not CloneWizardResult result)
        {
            wizard.Undo.Run();   // un token recién guardado o una clave recién copiada no se quedan sin sitio
            return;
        }
        await CloneAsync(result);
    }

    /// <summary>
    /// «Clonando…» (maqueta D): preparar git, comprobar el servidor, clonar con su porcentaje,
    /// registrar el sitio (hosts, vhost y certificado al aplicar, la base de datos) y detectar el
    /// proyecto. Si falla o se cancela, no queda nada y se deshace el token o la clave recién puestos.
    /// Después, lo de Nuevo sitio: los pasos tras clonar (<see cref="AfterCloneAsync"/>), ofrecer la
    /// CA y abrir el navegador.
    /// </summary>
    private async Task CloneAsync(CloneWizardResult wizard)
    {
        var request = wizard.Request;
        var url = request.Url;
        GitLocation? git = null;
        try { git = Git.Locate(); } catch (RoostException) { }
        if (git is null && (git = await EnsureGitAsync()) is null)
        {
            wizard.Undo.Run();
            return;
        }
        var manager = Git.Manager(git);
        var paths = Main.App.Paths;
        var folder = request.Folder is { } f ? Path.GetFullPath(f, paths.Www) : Path.Combine(paths.Www, request.Name ?? url.SuggestedSiteName);
        var branch = request.Branch ?? request.Probe?.DefaultBranch ?? "la rama por defecto";
        var steps = new List<GitStep>
        {
            new("Preparar git"),
            new("Comprobar el servidor"),
            new("Clonar"),
            new("Registrar el sitio", "vhost, certificado, hosts"),
            new("Detectar el proyecto"),
        };
        CloneResult? cloned = null;
        var hostsOk = true;
        string? dbError = null;
        var keyName = request.Access?.Kind == SiteGit.AccessKey ? request.Access.KeyName : null;
        var own = request.Access?.Kind == SiteGit.AccessOwn;
        var es = CultureInfo.GetCultureInfo("es-ES");

        var dialog = new GitTaskDialogViewModel($"Clonando {url.Label}", $"{branch} → {folder}", steps, async (d, cancel) =>
        {
            d.Step(0, "Preparando git");
            steps[0].Hint = git.Source == GitSource.Roost ? $"{GitCatalog.Windows.Label}, SHA-256 verificado" : $"el tuyo: {git.Executable}";
            d.Step(1, "Comprobando el servidor");
            steps[1].Hint = ServerHint(url, request.Access);
            d.Step(2, "Clonando");
            var progress = new Progress<CloneProgress>(d.Progress);
            var lines = new Progress<string>(line =>
            {
                if (d.Line(line) is { } total) steps[2].Hint = $"{total.ToString("N0", es)} objetos";
            });
            cloned = await manager.CloneAsync(request, progress, lines, cancel);
            // A partir de aquí el sitio existe: ya no se cancela a medias, y el acceso es suyo.
            d.Cancelable = false;
            wizard.Undo.Clear();
            var site = cloned.Change.Site;
            StateOf(site.Name).Fetched = true;

            d.Step(3, "Registrando el sitio");
            Main.NotifyConfigChanged();
            hostsOk = await Main.SyncHostsAsync(showError: false);
            var applied = await (ApplyForClone ?? ApplyStackAsync)();
            if (wizard.Database is { } database)
            {
                try
                {
                    site = manager.SetDatabase(site.Name, database);
                    if (applied && wizard.CreateDatabase) await CreateDatabaseAsync(database);
                }
                catch (Exception ex) when (ex is RoostException or IOException)
                {
                    dbError = ex.Message;
                }
            }

            d.Step(4, "Mirando qué proyecto es");
            var project = ProjectKind.Detect(site.Folder);
            steps[4].Hint = project.Framework is { } framework
                ? $"{framework}{(site.Root is { } root ? $", raíz web {root}/" : "")}"
                : project.HasComposerJson ? "PHP con composer.json" : "PHP a secas: se sirve tal cual";
        }, ex => ex is GitException g ? GitTexts.Explain(g.Failure, url, keyName, folder, ownKey: own) : ex.Message)
        {
            CanBackground = true,
            Note = "Si algo falla, no queda nada a medias: se clona en una carpeta temporal y solo al acabar pasa a ser el sitio.",
            CanceledText = "Cancelado. No ha quedado nada a medias: el clonado iba en una carpeta temporal y ya no está.",
            OpenLog = () => Main.OpenLogAsync($"git-{request.Name ?? url.SuggestedSiteName}.log"),
        };

        var run = dialog.RunAsync();
        var shown = await Main.ShowDialogAsync(dialog);
        await run;
        if (!await dialog.Finished)
        {
            if (cloned is null) wizard.Undo.Run();
            if (shown is null && dialog.Failed) await Main.InfoAsync($"No se ha clonado {url.Label}", dialog.Error!, Tone.Bad);
            Main.NotifyConfigChanged();
            return;
        }

        var created = new SiteRegistry(paths).Find(cloned!.Change.Site.Name) ?? cloned.Change.Site;
        Main.NotifyConfigChanged();
        Pick(_all.FirstOrDefault(r => r.Site.Name == created.Name));
        _ = RefreshGitAsync(created.Name, fetch: false);
        Main.Flash($"{created.Host} clonado: {cloned.Branch} · {cloned.Head?.ShortSha ?? "sin commits"}");

        if (!hostsOk)
            await Main.InfoAsync($"{created.Host} está clonado, pero falta en el hosts",
                $"{Main.HostsError}\n\nEl sitio queda registrado y se sirve, pero {created.Host} no resolverá en el navegador hasta que esté en el hosts. "
                + $"Para añadirlo: Sitios › {created.Host} › «Añadir al hosts» (Windows volverá a pedir permiso), o pasa al DNS local en Ajustes › Nombres .test.",
                Tone.Warn);
        if (dbError is not null)
            await Main.InfoAsync($"No se creó la base de datos de {created.Host}", $"{dbError}\n\nEl sitio está clonado y registrado: créala desde su panel (Base de datos › Crear).", Tone.Warn);

        // Los pasos tras clonar (composer, .env, migraciones…): los engancha ahí el paquete «Pasos y plantillas».
        await AfterCloneAsync(created);
        if (!hostsOk) return;
        await Main.OfferTrustAsync();
        OpenBrowser(StackPlanner.SiteUrl(created, Git.Config().HttpsPort));
    }

    /// <summary>Lo de Nuevo sitio: si está en marcha se aplica; si no, se arranca. false si no quedó en marcha.</summary>
    private async Task<bool> ApplyStackAsync()
    {
        if (Main.Stack.IsRunning) return await Main.ApplyAsync();
        await Main.StartStackAsync();
        return Main.Stack.IsRunning;
    }

    /// <summary>La base de datos del sitio en la global en marcha, como al crearlo con Nuevo sitio.</summary>
    private async Task CreateDatabaseAsync(string database)
    {
        if (Main.Stack.Status?.Db is null) return;
        var paths = Main.App.Paths;
        var planner = new StackPlanner(paths, ProcessRunner.Default);
        var db = planner.ResolveDb(RoostConfig.Load(paths.ConfigFile), new DbInstaller(paths).ListInstalled());
        if (db is null) return;
        var ini = planner.DbIniPath(db);
        await Task.Run(() => DbAdmin.Create(db, ini, database, ProcessRunner.Default));
    }

    /// <summary>La pista del paso «Comprobar el servidor». Con la clave de Roost, el servidor tiene que estar aceptado.</summary>
    private string ServerHint(GitUrl url, SiteAccess? access)
    {
        if (url.IsLocal) return "carpeta local";
        if (url.Scheme == GitScheme.Https) return access?.Kind == SiteGit.AccessToken ? $"{url.Host}, con token" : $"{url.Host}, público";
        var status = Git.Access.KnownHosts.Status(url.Host!, url.Port);
        return status.Trust switch
        {
            HostTrust.Published => $"{status.Token}, huella de serie",
            HostTrust.Trusted => $"{status.Token}, huella aceptada",
            HostTrust.Mismatch => throw new GitException(new GitFailure(GitFailureKind.HostKeyChanged, "", "")),
            _ when access?.Kind == SiteGit.AccessOwn => $"{status.Token}, con tu known_hosts",
            _ => throw new GitException(new GitFailure(GitFailureKind.HostKeyUnverified, "", "")),
        };
    }

    // Actualizar, cambiar de rama, comprobar, adoptar

    private (SiteGit Info, GitUrl? Url) InfoOf(Site site)
    {
        var info = StateOf(site.Name).Git ?? site.Git!;
        return (info, GitUrl.TryParse(info.Remote, out var url, out _) ? url : null);
    }

    private string ExplainOp(Exception ex, Site site, SiteGit info, GitUrl? url) => ex is GitException g
        ? GitTexts.Explain(g.Failure, url, info.Access == SiteGit.AccessKey ? info.KeyName : null, site.Host,
            StateOf(site.Name).Status?.Changed, StateOf(site.Name).Status?.Branch ?? info.Branch, info.Access == SiteGit.AccessOwn)
        : ex.Message;

    /// <summary>«Actualizar»: <c>pull --ff-only</c>, solo con el árbol limpio, en el diálogo de pasos.</summary>
    [RelayCommand]
    private async Task GitUpdateAsync()
    {
        if (Selected is not { } row || row.Site.Git is null || GitBusy) return;
        if (GitDirty)
        {
            await Main.InfoAsync($"{row.Host} tiene cambios sin guardar", GitTexts.LocalChanges(row.Host, StateOf(row.Site.Name).Status?.Changed), Tone.Warn);
            return;
        }
        if (await EnsureGitAsync() is not { } git) return;
        var manager = Git.Manager(git);
        var site = row.Site;
        var (info, url) = InfoOf(site);
        GitBusy = true;
        try
        {
            PullResult? result = null;
            var steps = new List<GitStep>
            {
                new("Comprobar que no hay cambios locales"),
                new("Traer lo nuevo de origin"),
                new($"Avanzar {info.Branch}", "solo fast-forward"),
            };
            var dialog = new GitTaskDialogViewModel($"Actualizando {site.Host}", $"{info.Branch} desde {url?.Label ?? info.Remote}", steps, async (d, cancel) =>
            {
                d.Step(0, "Comprobando");
                var lines = new Progress<string>(line =>
                {
                    if (steps[0].IsActive) d.Step(1, "Trayendo");
                    if (GitOutput.ParseProgress(line) is { } p) d.Progress(p);
                    d.Line(line);
                });
                result = await manager.PullAsync(site.Name, lines, cancel);
                d.Step(2, "Avanzando");
            }, ex => ExplainOp(ex, site, info, url))
            {
                Note = "Solo avanza la rama si no hay nada tuyo por medio (fast-forward): Roost nunca pisa tu trabajo.",
                CanceledText = "Cancelado. Si git iba a medias, mira el estado con tu git (git status) antes de seguir.",
                OpenLog = () => Main.OpenLogAsync($"git-{site.Name}.log"),
            };
            var run = dialog.RunAsync();
            await Main.ShowDialogAsync(dialog);
            await run;

            var state = StateOf(site.Name);
            if (await dialog.Finished && result is { } done)
            {
                state.Check = null;
                state.Failure = null;
                state.Error = null;
                state.Fetched = true;
                state.When = DateTimeOffset.Now;
                Main.Flash(done.Changed
                    ? $"{site.Host}: {Short(done.From)} → {Short(done.To)}, {GitTexts.Plural(done.ChangedFiles, "fichero", "ficheros")}"
                    : $"{site.Host} ya estaba al día");
            }
            await RefreshGitAsync(site.Name, fetch: false);
        }
        finally
        {
            GitBusy = false;
        }
    }

    private static string Short(string sha) => sha.Length > 7 ? sha[..7] : sha;

    /// <summary>El desplegable de la rama (maqueta H): confirmación y, si se acepta, el cambio con su diálogo de pasos.</summary>
    private async Task ChangeBranchAsync(SiteRow row, string branch)
    {
        var site = row.Site;
        if (GitBusy || GitDirty)
        {
            ShowGit(site);
            return;
        }
        if (!await Main.ConfirmAsync($"Cambiar {site.Host} a {branch}",
                "Se descarga la rama y se cambia el checkout. Las migraciones y las dependencias pueden cambiar: Roost te lo dirá al acabar.", "Cambiar de rama"))
        {
            ShowGit(site);
            return;
        }
        if (await EnsureGitAsync() is not { } git)
        {
            ShowGit(site);
            return;
        }
        var manager = Git.Manager(git);
        var (info, url) = InfoOf(site);
        GitBusy = true;
        try
        {
            var steps = new List<GitStep>
            {
                new("Comprobar que no hay cambios locales"),
                new($"Traer {branch} de origin"),
                new($"Cambiar el checkout a {branch}"),
            };
            var dialog = new GitTaskDialogViewModel($"Cambiando {site.Host} a {branch}", $"{info.Branch} → {branch} · {url?.Label ?? info.Remote}", steps, async (d, cancel) =>
            {
                d.Step(0, "Comprobando");
                var lines = new Progress<string>(line =>
                {
                    if (steps[0].IsActive) d.Step(1, "Trayendo");
                    if (GitOutput.ParseProgress(line) is { } p) d.Progress(p);
                    if (line.StartsWith("Switched to", StringComparison.Ordinal) || line.StartsWith("branch '", StringComparison.Ordinal)) d.Step(2, "Cambiando");
                    d.Line(line);
                });
                await manager.CheckoutAsync(site.Name, branch, lines, cancel);
            }, ex => ExplainOp(ex, site, info, url))
            {
                Note = "Solo con el árbol limpio: Roost nunca cambia de rama por encima de tu trabajo.",
                CanceledText = "Cancelado. Si git iba a medias, mira el estado con tu git (git status) antes de seguir.",
                OpenLog = () => Main.OpenLogAsync($"git-{site.Name}.log"),
            };
            var run = dialog.RunAsync();
            await Main.ShowDialogAsync(dialog);
            await run;
            if (await dialog.Finished)
            {
                var state = StateOf(site.Name);
                state.Check = null;
                state.Failure = null;
                Main.Flash($"{site.Host} está ahora en {branch}");
                Main.NotifyConfigChanged();
            }
            await RefreshGitAsync(site.Name, fetch: false);
        }
        finally
        {
            GitBusy = false;
            if (Selected?.Site.Name == site.Name) ShowGit(Selected.Site);
        }
    }

    /// <summary>«Comprobar ahora»: lo pide el usuario, así que trae lo nuevo (fetch, sin tocar la carpeta) y cuenta cuántos commits faltan.</summary>
    [RelayCommand]
    private async Task GitCheckNowAsync()
    {
        if (Selected is not { } row || row.Site.Git is null || GitBusy) return;
        if (await EnsureGitAsync() is null) return;
        GitBusy = true;
        GitChipText = "comprobando…";
        GitChipTone = Tone.Accent;
        try
        {
            await RefreshGitAsync(row.Site.Name, fetch: true);
        }
        finally
        {
            GitBusy = false;
            if (Selected?.Site.Name == row.Site.Name) ShowGit(row.Site);
        }
    }

    /// <summary>«Adoptar»: el .git que ya tiene la carpeta pasa a ser el repositorio del sitio, con el acceso del usuario.</summary>
    [RelayCommand]
    private async Task GitAdoptAsync()
    {
        if (Selected is not { } row || GitBusy) return;
        if (await EnsureGitAsync() is not { } git) return;
        GitBusy = true;
        try
        {
            var adopted = await Git.Manager(git).AdoptGitAsync(row.Site.Name, CancellationToken.None);
            _gitStates.Remove(row.Site.Name);
            Main.NotifyConfigChanged();
            Main.Flash($"{adopted.Host} sigue ahora {adopted.Git!.Branch} de {(GitUrl.TryParse(adopted.Git.Remote, out var u, out _) ? u.Label : adopted.Git.Remote)}");
        }
        catch (RoostException ex)
        {
            await Main.InfoAsync($"No se pudo adoptar el repositorio de {row.Host}", ex is GitException g ? GitTexts.Explain(g.Failure, null, where: row.Host) : ex.Message, Tone.Bad);
        }
        finally
        {
            GitBusy = false;
        }
    }

    [RelayCommand]
    private Task GitHistoryAsync()
    {
        if (Selected is not { } row || row.Site.Git is null) return Task.CompletedTask;
        var manager = Git.AccessManager();
        var (_, url) = InfoOf(row.Site);
        var dialog = new GitHistoryDialogViewModel(row.Host, manager.History.Load(row.Site.Name), () => Main.OpenLogAsync($"git-{row.Site.Name}.log"), url);
        return Main.ShowDialogAsync(dialog);
    }

    [RelayCommand]
    private async Task GitAccessAsync()
    {
        if (Selected is not { } row || row.Site.Git is null) return;
        var state = StateOf(row.Site.Name);
        var dialog = new GitAccessDialogViewModel(Main, Git, state.Git ?? row.Site.Git, row.Site, state, OpenBrowser, EnsureGitAsync);
        await Main.ShowDialogAsync(dialog);
        if (dialog.Changed) Main.NotifyConfigChanged();
        Repaint(row.Site.Name);
    }

    // Quitar el sitio: la casilla de la clave y el token (texto 15 del diseño)

    /// <summary>
    /// La confirmación de «Quitar sitio»: si el sitio tiene clave de Roost o token propios, con la
    /// casilla «Quitar también la clave SSH y el token de este sitio». null si se cancela; si no, si
    /// hay que quitarlos.
    /// </summary>
    private async Task<bool?> ConfirmRemoveAsync(SiteRow row, string text)
    {
        SiteAccessRemoval? plan = null;
        try { plan = Git.AccessManager().RemoveAccess(row.Site, dryRun: true); }
        catch (Exception ex) when (ex is RoostException or IOException or UnauthorizedAccessException) { }
        var removing = new List<string>();
        var kept = new List<string>();
        if (plan?.KeyName is { } key)
        {
            if (plan.KeyShared) kept.Add($"Su clave SSH ({key}) se queda: la usa otro sitio.");
            else removing.Add($"Su clave SSH de Roost ({key}): quita también la pública de las deploy keys{(plan.DeployKeysUrl is { } page ? $" ({page})" : " del repositorio")}.");
        }
        if (plan?.Token is { } token)
        {
            var where = GitTexts.Where(Git.Access.Credentials.Describe(token));
            if (plan.TokenShared) kept.Add($"Su token ({where}) se queda: también vale para otro sitio.");
            else removing.Add($"Su token de {where}, del Administrador de credenciales.");
        }
        var dialog = new RemoveSiteDialogViewModel(row.Host, text, removing, kept);
        return await Main.ShowDialogAsync(dialog) as bool?;
    }

    /// <summary>Quita la clave y el token del sitio ya quitado (salvo lo que compartan otros sitios).</summary>
    private void RemoveSiteAccess(Site site, bool removeAccess)
    {
        if (!removeAccess) return;
        try
        {
            var done = Git.AccessManager().RemoveAccess(site);
            if (done.KeyRemoved || done.TokenRemoved)
                Main.Flash(done.KeyRemoved && done.TokenRemoved ? $"Quitadas la clave SSH y el token de {site.Host}"
                    : done.KeyRemoved ? $"Quitada la clave SSH de {site.Host}" : $"Quitado el token de {site.Host}");
        }
        catch (Exception ex) when (ex is RoostException or IOException or UnauthorizedAccessException) { }
        _gitStates.Remove(site.Name);
    }

    // Novedades (I8): ls-remote de cada sitio cada N minutos con la app abierta

    /// <summary>Cuándo fue la última comprobación de novedades (para Ajustes › Git).</summary>
    [ObservableProperty] public partial string GitChecksText { get; private set; } = "Aún no se ha mirado en esta sesión.";

    public bool GitChecking => _gitChecking;

    /// <summary>
    /// Arranca (o rearranca, al cambiar Ajustes › Git) la comprobación periódica: cada
    /// <see cref="DesktopPrefs.GitCheckMinutes"/> minutos y la primera, al minuto de abrir la app.
    /// Con la casilla quitada, se para.
    /// </summary>
    public void StartGitChecks()
    {
        _gitTimer?.Stop();
        _gitTimer = null;
        var prefs = Main.App.Prefs;
        OnPropertyChanged(nameof(GitCheckHint));
        if (!prefs.GitCheckUpdates) return;
        _gitTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(Math.Clamp(prefs.GitCheckMinutes, 5, 24 * 60)) };
        _gitTimer.Tick += async (_, _) => await CheckGitUpdatesAsync();
        _gitTimer.Start();
        if (_gitFirstCheck) return;
        _gitFirstCheck = true;
        DispatcherTimer.RunOnce(() => _ = CheckGitUpdatesAsync(), TimeSpan.FromMinutes(1));
    }

    /// <summary>
    /// Mira si hay commits nuevos en el remoto de cada sitio de git (los que tienen
    /// <see cref="SiteGit.CheckUpdates"/>), uno detrás de otro y fuera del hilo de la UI. Solo
    /// <c>ls-remote</c>: nunca descarga ni toca las carpetas. Si ya hay una en marcha, no hace nada;
    /// si algo falla, no avisa: se ve en el chip del sitio. Sin git instalado, no instala nada.
    /// Devuelve cuántos sitios tienen novedades.
    /// </summary>
    public async Task<int> CheckGitUpdatesAsync(bool force = false)
    {
        if (_gitChecking) return 0;
        if (!force && !Main.App.Prefs.GitCheckUpdates) return 0;
        if (Git.TryManager() is not { } manager) return 0;
        _gitChecking = true;
        OnPropertyChanged(nameof(GitChecking));
        var news = 0;
        var checkedSites = 0;
        try
        {
            IReadOnlyList<Site> sites;
            try { sites = new SiteRegistry(Main.App.Paths).Load(); }
            catch (RoostException) { return 0; }
            foreach (var site in sites.Where(s => s.Git is { CheckUpdates: true }))
            {
                var state = StateOf(site.Name);
                try
                {
                    var check = await Task.Run(() => manager.CheckUpdatesAsync(site.Name, CancellationToken.None));
                    state.Check = check;
                    state.Failure = check.Failure;
                    state.Error = null;
                    state.When = check.When;
                    if (check.Status is { } status) state.Status = status;
                    if (check.Branches.Count > 0) state.Branches = check.Branches;
                    if (check.HasNews) news++;
                }
                catch (Exception ex) when (ex is RoostException or IOException or UnauthorizedAccessException)
                {
                    state.Error = ex.Message;
                    state.When = DateTimeOffset.Now;
                }
                checkedSites++;
                Repaint(site.Name);
            }
            GitChecksText = checkedSites == 0
                ? $"{GitTexts.When(DateTimeOffset.Now)}: ningún sitio de git que mirar."
                : $"{GitTexts.When(DateTimeOffset.Now)}: {GitTexts.Plural(checkedSites, "sitio", "sitios")}, {(news == 0 ? "ninguno con novedades" : GitTexts.Plural(news, "con novedades", "con novedades"))}.";
            return news;
        }
        finally
        {
            _gitChecking = false;
            OnPropertyChanged(nameof(GitChecking));
        }
    }
}
