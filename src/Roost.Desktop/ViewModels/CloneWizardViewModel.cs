using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Roost.Core;
using Roost.Core.Audit;
using Roost.Core.Config;
using Roost.Core.Git;
using Roost.Core.Install;
using Roost.Core.Models;
using Roost.Core.Scanning;
using Roost.Core.Sites;
using Roost.Core.Stack;
using Roost.Desktop.Controls;
using Roost.Desktop.Services;

namespace Roost.Desktop.ViewModels;

/// <summary>
/// Lo que hay que deshacer si el clonado no acaba, como en <c>roost site clone</c>: el token recién
/// guardado (o el que había antes, si se sustituyó) y la clave recién importada. La clave que genera
/// Roost se queda: puede que ya esté pegada como deploy key.
/// </summary>
public sealed class GitAccessUndo
{
    private readonly List<Action> _steps = [];

    public bool IsEmpty => _steps.Count == 0;

    public void Add(Action action) => _steps.Add(action);

    /// <summary>Ya es del sitio: no hay nada que deshacer.</summary>
    public void Clear() => _steps.Clear();

    public void Run()
    {
        foreach (var step in Enumerable.Reverse(_steps))
        {
            try { step(); }
            catch (Exception ex) when (ex is RoostException or IOException or UnauthorizedAccessException) { /* lo que no se pueda deshacer se queda */ }
        }
        _steps.Clear();
    }
}

/// <summary>Lo que sale del asistente: lo que se va a clonar, la base de datos y lo que habría que deshacer.</summary>
public sealed record CloneWizardResult(CloneRequest Request, string? Database, bool CreateDatabase, GitAccessUndo Undo, string AccessText);

/// <summary>Lo que dijo «Comprobar».</summary>
public enum CloneCheck
{
    None,
    Public,
    Private,
    Ssh,
    Failed,
}

/// <summary>Las pestañas del paso Acceso.</summary>
public enum GitAccessTab
{
    Token,
    RoostKey,
    OwnKey,
}

/// <summary>Lo que se sabe de la huella del servidor SSH en el paso Acceso.</summary>
public enum HostCheck
{
    None,
    Scanning,
    Published,
    Trusted,
    Unknown,
    Mismatch,
    Changed,
    Failed,
}

/// <summary>Una rama del desplegable del paso Sitio.</summary>
public sealed record BranchOption(string Name, bool IsDefault)
{
    public override string ToString() => IsDefault ? $"{Name} (por defecto)" : Name;
}

/// <summary>
/// «Desde git…» (maquetas A, B y C del diseño de git): tres pasos.
/// <list type="number">
/// <item>Repositorio: la dirección y «Comprobar», que solo pregunta al servidor (<c>ls-remote</c>):
/// el proveedor, si es público o privado y sus ramas.</item>
/// <item>Acceso (solo si es privado o va por SSH): token HTTPS, la clave SSH de Roost para este
/// sitio (generarla, copiarla, el enlace a sus deploy keys) o las del usuario; la huella del
/// servidor (de serie, aceptada, desconocida con «Confiar en este servidor» o cambiada) y «Probar
/// acceso».</item>
/// <item>Sitio: nombre, carpeta, rama, PHP y base de datos, con lo que hará Roost al crearlo.</item>
/// </list>
/// Devuelve un <see cref="CloneWizardResult"/>; el clonado lo hace quien lo abre.
/// </summary>
public sealed partial class CloneWizardViewModel : DialogViewModel
{
    private readonly MainViewModel _main;
    private readonly GitContext _git;
    private readonly Func<Task<GitLocation?>> _ensureGit;
    private readonly Action<string> _openUrl;
    private readonly IReadOnlyList<Site> _sites;
    private readonly IReadOnlyList<InstalledPhp> _installed;
    private readonly InstalledPhp? _global;
    private readonly InstalledDb? _db;
    private readonly CancellationTokenSource _cancel = new();
    private SiteManager? _manager;
    private GitUrl? _url;
    private RemoteProbe? _probe;
    private SiteAccess? _access;
    private GitUrl? _accessUrl;
    private IReadOnlyList<SshHostKey> _scanned = [];
    private bool _tokenUndo;
    private bool _nameTouched;
    private bool _folderTouched;
    private bool _settingName;
    private bool _settingFolder;

    /// <param name="ensureGit">Prepara git (la primera vez, el MinGit verificado con el diálogo Instalando); null si no se pudo.</param>
    /// <param name="openUrl">Abre una página en el navegador (los tests no abren nada).</param>
    public CloneWizardViewModel(MainViewModel main, GitContext git, Func<Task<GitLocation?>> ensureGit, Action<string> openUrl)
    {
        _main = main;
        _git = git;
        _ensureGit = ensureGit;
        _openUrl = openUrl;
        var paths = main.App.Paths;
        try { _sites = new SiteRegistry(paths).Load(); } catch (RoostException) { _sites = []; }
        _installed = new PhpInstaller(paths).ListInstalled();
        var config = git.Config();
        var planner = new StackPlanner(paths, ProcessRunner.Default);
        try { _global = planner.ResolvePhp(config, _installed); } catch (RoostException) { }
        try { _db = planner.ResolveDb(config, new DbInstaller(paths).ListInstalled()); } catch (RoostException) { }
        UsesLocalDns = config.UsesLocalDns;
        CreateDb = _db is not null;
        BuildPhpOptions();
    }

    public override bool Wide => true;

    /// <summary>Mientras pregunta al servidor o crea una clave, Escape no lo cierra.</summary>
    public override bool CanDismiss => !Busy;

    /// <summary>Lo que hay que deshacer si el sitio no llega a crearse (quien lo abre lo llama).</summary>
    public GitAccessUndo Undo { get; } = new();

    // Pasos

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsStep1), nameof(IsStep2), nameof(IsStep3), nameof(HeaderTitle), nameof(HeaderSubtitle), nameof(ShowBack))]
    public partial int Step { get; private set; } = 1;

    public bool IsStep1 => Step == 1;
    public bool IsStep2 => Step == 2;
    public bool IsStep3 => Step == 3;
    public bool ShowBack => Step > 1;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanNext), nameof(CanTest), nameof(CanClone), nameof(Idle))]
    public partial bool Busy { get; private set; }

    public bool Idle => !Busy;

    partial void OnBusyChanged(bool value) => OnPropertyChanged(nameof(CanDismiss));

    public string HeaderTitle => Step == 2 ? $"Acceso a {_url?.Label}" : "Nuevo sitio";

    public string HeaderSubtitle => Step switch
    {
        1 => "Desde un repositorio git. Paso 1 de 3 · Repositorio",
        2 => IsSshUrl ? "Paso 2 de 3 · Por SSH hace falta una clave" : "Paso 2 de 3 · El repositorio es privado",
        _ => $"Paso 3 de 3 · {_url?.Label} · {AccessText}",
    };

    /// <summary>Cómo se entrará, para el subtítulo del paso 3 y el panel.</summary>
    public string AccessText => _url is { IsLocal: true } ? "repositorio local" : _access?.Kind switch
    {
        SiteGit.AccessToken => "acceso con token HTTPS",
        SiteGit.AccessKey => "acceso con clave SSH de Roost",
        SiteGit.AccessOwn => "acceso con tus claves SSH",
        _ => "público",
    };

    private bool IsSshUrl => _url?.Scheme == GitScheme.Ssh;

    // ---- Paso 1: Repositorio ----

    [ObservableProperty] public partial string Url { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCheck), nameof(CheckTone), nameof(CanNext), nameof(CheckFailed))]
    public partial CloneCheck CheckResult { get; private set; }

    [ObservableProperty] public partial string CheckTitle { get; private set; } = "";

    [ObservableProperty] public partial string CheckDetail { get; private set; } = "";

    public bool HasCheck => CheckResult != CloneCheck.None;

    public bool CheckFailed => CheckResult == CloneCheck.Failed;

    public Tone CheckTone => CheckResult switch
    {
        CloneCheck.Public => Tone.Ok,
        CloneCheck.Private or CloneCheck.Ssh => Tone.Warn,
        CloneCheck.Failed => Tone.Bad,
        _ => Tone.Neutral,
    };

    /// <summary>«Siguiente»: solo tras comprobar (y, en el paso 2, tras probar el acceso).</summary>
    public bool CanNext => !Busy && (Step == 1 ? CheckResult is CloneCheck.Public or CloneCheck.Private or CloneCheck.Ssh : AccessOk);

    partial void OnUrlChanged(string value)
    {
        // Otra dirección: lo comprobado ya no vale.
        if (_url is not null && !string.Equals(value.Trim(), _url.Original, StringComparison.Ordinal)) Reset();
    }

    private void Reset()
    {
        _url = null;
        _probe = null;
        _access = null;
        _accessUrl = null;
        CheckResult = CloneCheck.None;
        CheckTitle = CheckDetail = "";
        AccessOk = false;
    }

    [RelayCommand]
    private async Task CheckAsync()
    {
        if (Busy) return;
        if (!GitUrl.TryParse(Url, out var url, out var why))
        {
            Reset();
            SetCheck(CloneCheck.Failed, GitTexts.InvalidUrl(why), "");
            return;
        }

        Busy = true;
        CheckResult = CloneCheck.None;
        CheckTitle = "Preguntando al servidor…";
        try
        {
            if (await ManagerAsync() is not { } manager)
            {
                SetCheck(CloneCheck.Failed, "Sin git no se puede comprobar el repositorio.", "Prepáralo con Comprobar otra vez o indica tu git en Ajustes › Git.");
                return;
            }
            RemoteProbe probe;
            try { probe = await manager.ProbeAsync(url, null, _cancel.Token); }
            catch (GitException ex) { probe = new RemoteProbe(false, true, null, [], ex.Failure); }   // SSH sin clave: no se lanza nada

            _url = url;
            _probe = probe;
            _access = null;
            _accessUrl = null;
            AccessOk = false;
            var who = $"{(url.IsLocal ? "Repositorio local" : url.ProviderName)} · {url.Label}";
            if (probe.Reachable)
            {
                var detail = $"Rama por defecto: {probe.DefaultBranch ?? "ninguna"} · {GitTexts.Plural(probe.Branches.Count, "rama", "ramas")}";
                SetCheck(CloneCheck.Public, url.IsLocal ? who : $"{who} · público", url.IsLocal ? $"{detail} · {url.Path}" : detail);
            }
            else if (url.Scheme == GitScheme.Ssh && probe.Failure?.Kind is GitFailureKind.KeyRequired or GitFailureKind.AuthRequired or GitFailureKind.KeyRejected or GitFailureKind.HostKeyUnverified)
            {
                SetCheck(CloneCheck.Ssh, $"{who} · por SSH", "Por SSH siempre hace falta una clave. En el paso siguiente eliges cuál.");
            }
            else if (probe.NeedsAuth)
            {
                SetCheck(CloneCheck.Private, $"{who} · privado (o no existe)",
                    $"{GitTexts.Who(url)} no distingue entre «no existe» y «no tienes acceso». En el paso siguiente eliges cómo entrar.");
            }
            else
            {
                SetCheck(CloneCheck.Failed, GitTexts.Explain(probe.Failure ?? GitFailure.Classify(1, "", url), url), probe.Failure?.Detail is { Length: > 0 } d ? "git: " + d : "");
            }
        }
        catch (RoostException ex)
        {
            SetCheck(CloneCheck.Failed, ex.Message, "");
        }
        finally
        {
            Busy = false;
        }
    }

    private void SetCheck(CloneCheck result, string title, string detail)
    {
        CheckTitle = title;
        CheckDetail = detail;
        CheckResult = result;
    }

    /// <summary>El SiteManager con git; la primera vez prepara git (puede abrir el diálogo Instalando).</summary>
    private async Task<SiteManager?> ManagerAsync()
    {
        if (_manager is not null) return _manager;
        var git = await _ensureGit();
        return git is null ? null : _manager = _git.Manager(git);
    }

    [RelayCommand]
    private void Next()
    {
        if (!CanNext) return;
        if (Step == 1)
        {
            if (CheckResult == CloneCheck.Public)
            {
                _access = null;
                _accessUrl = _url;
                EnterSite();
            }
            else
            {
                EnterAccess();
            }
        }
        else if (Step == 2)
        {
            EnterSite();
        }
    }

    [RelayCommand]
    private void Back()
    {
        if (Busy) return;
        if (Step == 3 && CheckResult != CloneCheck.Public) Step = 2;
        else Step = 1;
    }

    // ---- Paso 2: Acceso ----

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsTokenTab), nameof(IsKeyTab), nameof(IsOwnTab), nameof(ShowHost))]
    public partial GitAccessTab Tab { get; private set; }

    public bool IsTokenTab { get => Tab == GitAccessTab.Token; set { if (value) PickTab(GitAccessTab.Token); } }
    public bool IsKeyTab { get => Tab == GitAccessTab.RoostKey; set { if (value) PickTab(GitAccessTab.RoostKey); } }
    public bool IsOwnTab { get => Tab == GitAccessTab.OwnKey; set { if (value) PickTab(GitAccessTab.OwnKey); } }

    /// <summary>La dirección https del repositorio (la del token), o null si no se sabe.</summary>
    public GitUrl? TokenUrl => _url?.ToHttps();

    /// <summary>La dirección SSH (la de las claves), o null si no se sabe.</summary>
    public GitUrl? SshUrl => _url?.ToSsh();

    public bool CanToken => TokenUrl is not null;
    public bool CanSsh => SshUrl is not null;
    public string TokenTabTip => CanToken ? "Un token de acceso personal, por HTTPS" : "Un token solo vale por HTTPS: pon la dirección https:// del repositorio.";
    public string SshTabTip => CanSsh ? "Por SSH" : "Una clave SSH solo vale por SSH: pon la dirección SSH del repositorio (git@servidor:grupo/repo.git).";

    private GitUrl? AccessUrlForTab => Tab == GitAccessTab.Token ? TokenUrl : SshUrl;

    private void EnterAccess()
    {
        Step = 2;
        OnPropertyChanged(nameof(TokenUrl));
        OnPropertyChanged(nameof(SshUrl));
        OnPropertyChanged(nameof(CanToken));
        OnPropertyChanged(nameof(CanSsh));
        OnPropertyChanged(nameof(TokenTabTip));
        OnPropertyChanged(nameof(SshTabTip));
        KeyName = PickKeyName(_url!.SuggestedSiteName);
        LoadKey();
        LoadToken();
        LoadOwn();
        var tab = IsSshUrl || !CanToken ? GitAccessTab.RoostKey : GitAccessTab.Token;
        if (tab == GitAccessTab.RoostKey && !CanSsh) tab = GitAccessTab.Token;
        PickTab(tab, force: true);
        OnPropertyChanged(nameof(DeployKeySteps));
        OnPropertyChanged(nameof(HasDeployKeysUrl));
    }

    private void PickTab(GitAccessTab tab, bool force = false)
    {
        if (!force && tab == Tab) return;
        if (tab == GitAccessTab.Token && !CanToken) return;
        if (tab != GitAccessTab.Token && !CanSsh) return;
        Tab = tab;
        AccessOk = false;
        AccessState = AccessCheck.Untested;
        AccessMessage = "sin probar";
        if (tab == GitAccessTab.Token) HostState = HostCheck.None;
        else _ = CheckHostAsync();
        NotifyTabTexts();
    }

    private void NotifyTabTexts()
    {
        OnPropertyChanged(nameof(TokenUserHint));
        OnPropertyChanged(nameof(NewTokenText));
        OnPropertyChanged(nameof(TokenPermissionText));
        OnPropertyChanged(nameof(TokenStorageText));
        OnPropertyChanged(nameof(PasswordNote));
        OnPropertyChanged(nameof(HasPasswordNote));
        OnPropertyChanged(nameof(HasNewTokenUrl));
    }

    /// <summary>El nombre de la clave de Roost: el del sitio sugerido, o uno que no use ya otro sitio.</summary>
    private string PickKeyName(string suggested)
    {
        for (var i = 1; i < 20; i++)
        {
            var name = i == 1 ? suggested : $"{suggested}-{i}";
            if (!Site.IsValidName(name)) continue;
            if (_sites.Any(s => s.Name == name || s.Git?.KeyName == name)) continue;
            return name;
        }
        return suggested;
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanNext), nameof(CanTest), nameof(ShowTest), nameof(ShowNext2))]
    public partial bool AccessOk { get; private set; }

    public enum AccessCheck
    {
        Untested,
        Testing,
        Ok,
        Failed,
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AccessTone))]
    public partial AccessCheck AccessState { get; private set; }

    [ObservableProperty] public partial string AccessMessage { get; private set; } = "sin probar";

    public Tone AccessTone => AccessState switch
    {
        AccessCheck.Ok => Tone.Ok,
        AccessCheck.Failed => Tone.Bad,
        AccessCheck.Testing => Tone.Accent,
        _ => Tone.Neutral,
    };

    public bool CanTest => !Busy && Step == 2;
    public bool ShowTest => !AccessOk;
    public bool ShowNext2 => AccessOk;

    // Pestaña «Token HTTPS»

    [ObservableProperty] public partial string TokenUser { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TokenWarning), nameof(HasTokenWarning))]
    public partial string TokenText { get; set; } = "";

    [ObservableProperty] public partial bool ShowToken { get; set; }

    [RelayCommand]
    private void ToggleShowToken() => ShowToken = !ShowToken;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasExistingToken))]
    public partial string? ExistingToken { get; private set; }

    public bool HasExistingToken => ExistingToken is not null;

    partial void OnTokenTextChanged(string value)
    {
        if (Tab == GitAccessTab.Token && AccessOk) { AccessOk = false; AccessState = AccessCheck.Untested; AccessMessage = "sin probar"; }
    }

    private bool KnownProvider => TokenUrl?.Provider is GitProvider.GitHub or GitProvider.GitLab or GitProvider.Bitbucket;

    public string TokenUserHint => KnownProvider ? $"En {TokenUrl!.ProviderName} el usuario da igual: vale el token." : "Si tu servidor pide un usuario con el token, ponlo; si no, déjalo vacío.";

    public string NewTokenText => $"Crear un token en {TokenUrl?.ProviderName ?? "el servidor"}";

    public bool HasNewTokenUrl => TokenUrl?.NewTokenUrl is not null;

    public string TokenPermissionText => $"Permiso que necesita: {TokenUrl?.TokenPermission ?? "leer el repositorio"}.";

    public string TokenStorageText => $"Se guarda en el Administrador de credenciales de Windows, cifrado para tu usuario, y solo vale para {TokenUrl?.Label ?? "este repositorio"}. Nunca va en la dirección del repositorio ni en los registros.";

    public string? PasswordNote => KnownProvider ? $"Tu contraseña de {TokenUrl!.ProviderName} no sirve: {TokenUrl.ProviderName} solo acepta tokens." : null;

    public bool HasPasswordNote => PasswordNote is not null;

    /// <summary>Si lo pegado no tiene pinta de token del proveedor (suele ser la contraseña de la cuenta).</summary>
    public string? TokenWarning => TokenUrl?.Host is { } host && TokenText.Trim().Length > 0 && GitCredentials.LooksLikePassword(host, TokenText.Trim()) is not null
        ? $"Eso no parece un token de {host}. Si es la contraseña de la cuenta, no servirá: {host} solo acepta tokens."
        : null;

    public bool HasTokenWarning => TokenWarning is not null;

    private void LoadToken()
    {
        ExistingToken = null;
        if (TokenUrl is not { } url || CredentialLocation.For(url) is not { } location) return;
        TokenUser = "";
        if (_git.Access.Credentials.Find(location) is { } saved)
            ExistingToken = $"Hay un token guardado para {_git.Access.Credentials.Describe(saved.Target)}{(saved.UserName is { Length: > 0 } u ? $" (usuario {u})" : "")}: si no pegas otro, se usa ese.";
    }

    [RelayCommand]
    private void CreateToken()
    {
        if (TokenUrl?.NewTokenUrl is { } page) _openUrl(page);
    }

    // Pestaña «Clave SSH de Roost»

    [ObservableProperty] public partial string KeyName { get; private set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPublicKey), nameof(CanGenerate))]
    public partial string? PublicKey { get; private set; }

    [ObservableProperty] public partial string KeyNote { get; private set; } = "";

    public bool HasPublicKey => PublicKey is not null;

    public bool CanGenerate => !HasPublicKey;

    public string KeyIntro => "Roost crea una clave solo para este sitio (ed25519, solo lectura en el repositorio). Su parte privada queda en este equipo, solo legible por tu usuario.";

    public IReadOnlyList<string> DeployKeySteps => GitTexts.DeployKeySteps(SshUrl);

    public bool HasDeployKeysUrl => SshUrl?.DeployKeysUrl is not null;

    private void LoadKey()
    {
        var key = _git.Access.Keys.Find(KeyName);
        PublicKey = key?.PublicKey;
        KeyNote = key is null ? "" : $"Es la clave que ya tenía {KeyName} ({key.Algorithm} {key.Fingerprint}).";
    }

    [RelayCommand]
    private async Task GenerateKeyAsync()
    {
        if (Busy || HasPublicKey) return;
        Busy = true;
        try
        {
            if (_git.Ssh is null) throw new RoostException(MissingSsh);
            var key = await _git.Access.Keys.GenerateAsync(KeyName, _cancel.Token);
            PublicKey = key.PublicKey;
            KeyNote = $"Creada para {KeyName}: {key.Algorithm} {key.Fingerprint}, sin contraseña. Si no llegas a clonar, se queda para la próxima vez.";
            AccessState = AccessCheck.Untested;
            AccessMessage = "sin probar";
        }
        catch (RoostException ex)
        {
            KeyNote = ex.Message;
        }
        finally
        {
            Busy = false;
        }
    }

    /// <summary>«o usa un fichero de clave tuyo…»: se copia a Roost (la tuya no se toca); si no se llega a clonar, la copia se borra.</summary>
    [RelayCommand]
    private async Task ImportKeyAsync()
    {
        if (Busy || _main.StorageProvider is not { } storage) return;
        if (_git.Access.Keys.Exists(KeyName))
        {
            KeyNote = $"{KeyName} ya tiene una clave de Roost: úsala, o quítala en Ajustes › Git para usar la tuya.";
            return;
        }
        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions { Title = "Tu clave privada de OpenSSH (sin contraseña)", AllowMultiple = false });
        if (files.Count == 0 || files[0].TryGetLocalPath() is not { } file) return;
        Busy = true;
        try
        {
            if (_git.Ssh is null) throw new RoostException(MissingSsh);
            var key = await _git.Access.Keys.ImportAsync(KeyName, file, _cancel.Token);
            Undo.Add(() => _git.Access.Keys.Remove(KeyName));
            PublicKey = key.PublicKey;
            KeyNote = $"Copiada de {file} ({key.Algorithm} {key.Fingerprint}), solo para tu usuario. La tuya no se toca; si no llegas a clonar, la copia se borra.";
        }
        catch (RoostException ex)
        {
            KeyNote = ex.Message;
        }
        finally
        {
            Busy = false;
        }
    }

    [RelayCommand]
    private Task CopyKeyAsync() => PublicKey is { } key ? _main.CopyAsync(key, "Clave pública copiada") : Task.CompletedTask;

    [RelayCommand]
    private void OpenDeployKeys()
    {
        if (SshUrl?.DeployKeysUrl is { } page) _openUrl(page);
    }

    // Pestaña «Mi clave SSH»

    [ObservableProperty] public partial string OwnText { get; private set; } = "";

    [ObservableProperty] public partial string OwnFound { get; private set; } = "";

    [ObservableProperty] public partial Tone OwnTone { get; private set; }

    public string OwnHint => "Si la clave tiene contraseña, arranca el servicio y haz ssh-add; si no, Roost no podrá usarla sin preguntar.";

    public const string OwnHelpUrl = "https://learn.microsoft.com/es-es/windows-server/administration/openssh/openssh_keymanagement";

    private void LoadOwn()
    {
        var own = _git.OwnSsh();
        var names = own.Keys.Count == 0 ? "id_ed25519, id_rsa…" : string.Join(", ", own.Keys.Take(3)) + (own.Keys.Count > 3 ? "…" : "");
        OwnText = $"Usa las claves de {own.Folder} ({names}) y el ssh-agent de Windows, como en tu terminal. Roost no guarda nada.";
        OwnFound = own.Keys.Count == 0
            ? $"No hay ninguna clave id_* en {own.Folder} · servicio ssh-agent: {own.AgentState}"
            : $"Encontrada {own.Keys[0]} · servicio ssh-agent: {own.AgentState}";
        OwnTone = own.Keys.Count == 0 && own.AgentState != "en marcha" ? Tone.Warn : Tone.Ok;
    }

    [RelayCommand]
    private void OwnHelp() => _openUrl(OwnHelpUrl);

    // La huella del servidor

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HostTone), nameof(CanTrustHost), nameof(ShowHost))]
    public partial HostCheck HostState { get; private set; }

    [ObservableProperty] public partial string HostTitle { get; private set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasHostFingerprint))]
    public partial string HostFingerprint { get; private set; } = "";

    public bool HasHostFingerprint => HostFingerprint.Length > 0;

    [ObservableProperty] public partial string HostText { get; private set; } = "";

    public bool ShowHost => Tab != GitAccessTab.Token && HostState != HostCheck.None;

    public bool CanTrustHost => HostState == HostCheck.Unknown && _scanned.Count > 0;

    public Tone HostTone => HostState switch
    {
        HostCheck.Published or HostCheck.Trusted => Tone.Ok,
        HostCheck.Unknown => Tone.Warn,
        HostCheck.Mismatch or HostCheck.Changed or HostCheck.Failed => Tone.Bad,
        _ => Tone.Accent,
    };

    private const string MissingSsh = "Falta el cliente OpenSSH de Windows, que es una característica opcional: Configuración › Sistema › Características opcionales › Agregar una característica › Cliente OpenSSH. Sin él, entra con un token HTTPS.";

    private async Task CheckHostAsync()
    {
        if (SshUrl?.Host is not { } host)
        {
            HostState = HostCheck.None;
            return;
        }
        var port = SshUrl.Port;
        var hosts = _git.Access.KnownHosts;
        var status = hosts.Status(host, port);
        HostTitle = $"Huella de {status.Token}";
        switch (status.Trust)
        {
            case HostTrust.Published:
                var published = status.Published!.Keys[0];
                if (status.Stored.Count == 0) HostTitle = $"Primera conexión con {status.Token}";
                HostFingerprint = $"{published.Algorithm} {published.Fingerprint}";
                HostText = $"Coincide con la que publica {GitUrl.NameOf(status.Published.Provider)}. Roost la recordará.";
                HostState = HostCheck.Published;
                return;
            case HostTrust.Trusted:
                var stored = status.Stored[0];
                HostFingerprint = $"{stored.Key.Algorithm} {stored.Key.Fingerprint}";
                HostText = KnownHosts.AcceptedOn(stored) is { } on ? $"La aceptaste el {on}." : "La aceptaste antes.";
                HostState = HostCheck.Trusted;
                return;
            case HostTrust.Mismatch:
                HostTitle = $"La huella guardada de {status.Token} no es la que publica {GitUrl.NameOf(status.Published!.Provider)}";
                HostFingerprint = "";
                HostText = $"Lo que Roost tiene guardado para {status.Token} no coincide con la huella que publica {GitUrl.NameOf(status.Published.Provider)}. No se conecta. Si no lo cambiaste tú a propósito, pulsa Olvidar en Ajustes › Git y vuelve la publicada.";
                HostState = HostCheck.Mismatch;
                return;
        }

        if (_git.Ssh is null)
        {
            HostTitle = "Falta el OpenSSH de Windows";
            HostFingerprint = "";
            HostText = MissingSsh;
            HostState = HostCheck.Failed;
            return;
        }
        HostTitle = $"Primera conexión con {status.Token}";
        HostFingerprint = "";
        HostText = $"Preguntando a {status.Token} por su clave (ssh-keyscan)…";
        HostState = HostCheck.Scanning;
        try
        {
            _scanned = await hosts.ScanAsync(host, port, _cancel.Token);
            var key = _scanned[0];
            HostFingerprint = $"Huella {key.Algorithm} {key.Fingerprint}";
            HostText = $"Roost no puede comprobar esta huella. Compárala con la que te dé quien administra {host} y, si coincide, pulsa Confiar en este servidor.";
            HostState = HostCheck.Unknown;
        }
        catch (RoostException)
        {
            _scanned = [];
            HostText = $"{status.Token} no ha dado ninguna clave SSH. Comprueba el nombre, el puerto y tu conexión.";
            HostState = HostCheck.Failed;
        }
        catch (OperationCanceledException) { }
    }

    [RelayCommand]
    private void TrustHost()
    {
        if (!CanTrustHost || SshUrl?.Host is not { } host) return;
        _git.Access.KnownHosts.Trust(host, SshUrl.Port, [_scanned[0]]);
        var token = KnownHosts.HostToken(host, SshUrl.Port);
        HostTitle = $"Huella de {token}";
        HostFingerprint = $"{_scanned[0].Algorithm} {_scanned[0].Fingerprint}";
        HostText = "Aceptada ahora: Roost la recordará y avisará si cambia.";
        HostState = HostCheck.Trusted;
    }

    // Probar acceso

    [RelayCommand]
    private async Task TestAccessAsync()
    {
        if (Busy || AccessUrlForTab is not { } url || await ManagerAsync() is not { } manager) return;
        SiteAccess access;
        switch (Tab)
        {
            case GitAccessTab.Token:
                var location = CredentialLocation.For(url)!;
                var typed = TokenText.Trim();
                if (typed.Length > 0)
                {
                    try { SaveToken(location, typed); }
                    catch (RoostException ex) { FailAccess(ex.Message); return; }
                }
                else if (_git.Access.Credentials.Find(location) is null)
                {
                    FailAccess("Pega el token antes de probar.");
                    return;
                }
                access = SiteAccess.Token;
                break;
            case GitAccessTab.RoostKey:
                if (!_git.Access.Keys.Exists(KeyName))
                {
                    FailAccess("Genera la clave (o usa un fichero tuyo) y ponla como deploy key antes de probar.");
                    return;
                }
                if (HostState is not (HostCheck.Published or HostCheck.Trusted))
                {
                    FailAccess(HostState == HostCheck.Unknown
                        ? "Antes, compara la huella del servidor y pulsa Confiar en este servidor."
                        : "Roost no puede conectar con este servidor: mira lo que dice la huella.");
                    return;
                }
                access = SiteAccess.Key(KeyName);
                break;
            default:
                if (HostState is HostCheck.Mismatch or HostCheck.Changed)
                {
                    FailAccess("Roost no conecta con un servidor cuya huella no cuadra: mira lo que dice la huella.");
                    return;
                }
                access = SiteAccess.Own;
                break;
        }

        Busy = true;
        AccessState = AccessCheck.Testing;
        AccessMessage = "probando…";
        try
        {
            RemoteProbe probe;
            try { probe = await manager.ProbeAsync(url, access, _cancel.Token); }
            catch (GitException ex) { probe = new RemoteProbe(false, false, null, [], ex.Failure); }
            if (probe.Reachable)
            {
                _probe = probe;
                _access = access;
                _accessUrl = url;
                AccessState = AccessCheck.Ok;
                AccessMessage = $"correcto · {GitTexts.BranchesSummary(probe.DefaultBranch, probe.Branches)}";
                AccessOk = true;
                return;
            }
            var failure = probe.Failure ?? GitFailure.Classify(1, "", url);
            if (failure.Kind == GitFailureKind.HostKeyChanged)
            {
                HostState = HostCheck.Changed;
                HostTitle = $"La huella de {KnownHosts.HostToken(url.Host!, url.Port)} ha cambiado";
                HostFingerprint = "";
                HostText = GitTexts.Explain(failure, url);
            }
            FailAccess(GitTexts.Explain(failure, url, keyName: Tab == GitAccessTab.RoostKey ? KeyName : null, ownKey: Tab == GitAccessTab.OwnKey));
        }
        catch (RoostException ex)
        {
            FailAccess(ex.Message);
        }
        finally
        {
            Busy = false;
        }
    }

    private void FailAccess(string message)
    {
        AccessOk = false;
        AccessState = AccessCheck.Failed;
        AccessMessage = message;
    }

    /// <summary>Guarda el token pegado (el de antes, si había, se recuerda en memoria para volver a ponerlo si no se llega a clonar).</summary>
    private void SaveToken(CredentialLocation location, string token)
    {
        var credentials = _git.Access.Credentials;
        var target = credentials.TargetOf(location);
        var previous = credentials.Store.Read(target);
        credentials.Save(location, token, TokenUser.Trim().Length == 0 ? null : TokenUser.Trim());
        if (!_tokenUndo)
        {
            _tokenUndo = true;
            Undo.Add(() =>
            {
                if (previous is null) credentials.Remove(target);
                else credentials.Store.Write(previous);
            });
        }
        TokenText = "";
        LoadToken();
    }

    // ---- Paso 3: Sitio ----

    [ObservableProperty] public partial string Name { get; set; } = "";

    [ObservableProperty] public partial string Folder { get; set; } = "";

    [ObservableProperty] public partial string NameHint { get; private set; } = "";

    [ObservableProperty] public partial bool NameOk { get; private set; }

    [ObservableProperty] public partial string FolderHint { get; private set; } = "";

    [ObservableProperty] public partial bool FolderOk { get; private set; }

    [ObservableProperty] public partial IReadOnlyList<BranchOption> Branches { get; private set; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanClone), nameof(StepClone))]
    public partial BranchOption? Branch { get; set; }

    [ObservableProperty] public partial IReadOnlyList<PhpOption> PhpOptions { get; private set; } = [];

    [ObservableProperty] public partial PhpOption? Php { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanClone), nameof(DbText))]
    public partial bool CreateDb { get; set; }

    [ObservableProperty] public partial string DbName { get; private set; } = "";

    public bool HasDb => _db is not null;

    public string DbText => _db is null ? "No hay base de datos global: instálala en Bases de datos (o créala luego desde el panel del sitio)."
        : $"Crear {DbName} en {_db.Engine.DisplayName()} {_db.Branch}";

    public bool UsesLocalDns { get; }

    public bool UsesHosts => !UsesLocalDns;

    public string Host => $"{(Site.IsValidName(Name.Trim()) ? Name.Trim() : "<nombre>")}.test";

    public string StepClone => $"Clona {Branch?.Name ?? "la rama por defecto"} de {_url?.Label} en {Folder.Trim()}";

    public string StepVhost => $"Escribe el vhost y emite el certificado de {Host}";

    public string StepHosts => UsesLocalDns ? $"{Host} ya resuelve: lo responde el DNS local de Roost, sin permisos" : $"Añade {Host} al hosts: Windows pedirá permiso";

    public string StepDb => $"Crea la base de datos {DbName} (utf8mb4)";

    public string StepDetect => "Mira qué proyecto es y te propone los pasos (composer, .env…)";

    public bool CanClone => !Busy && Step == 3 && NameOk && FolderOk && Branch is not null && (!CreateDb || DbValid);

    private bool DbValid => DbAdmin.IsValidName(DbName) && !DbAdmin.SystemSchemas.Contains(DbName);

    partial void OnNameChanged(string value)
    {
        if (!_settingName) _nameTouched = value.Trim().Length > 0;
        UpdateSite();
    }

    partial void OnFolderChanged(string value)
    {
        if (!_settingFolder) _folderTouched = true;
        UpdateSite();
    }

    private void EnterSite()
    {
        Step = 3;
        if (!_nameTouched)
        {
            var name = _access?.Kind == SiteGit.AccessKey && _access.KeyName is { } key ? key : PickSiteName(_url!.SuggestedSiteName);
            _settingName = true;
            Name = name;
            _settingName = false;
        }
        var probe = _probe!;
        var options = probe.Branches.Select(b => new BranchOption(b, b == probe.DefaultBranch))
            .OrderByDescending(b => b.IsDefault).ToList();
        var previous = Branch?.Name;
        Branches = options;
        Branch = options.FirstOrDefault(o => o.Name == previous) ?? options.FirstOrDefault();
        UpdateSite();
        OnPropertyChanged(nameof(HeaderSubtitle));
        OnPropertyChanged(nameof(AccessText));
    }

    private string PickSiteName(string suggested)
    {
        for (var i = 1; i < 20; i++)
        {
            var name = i == 1 ? suggested : $"{suggested}-{i}";
            if (Site.IsValidName(name) && _sites.All(s => s.Name != name)) return name;
        }
        return suggested;
    }

    private void UpdateSite()
    {
        var name = Name.Trim().ToLowerInvariant();
        var valid = Site.IsValidName(name);
        var taken = valid && _sites.Any(s => s.Name == name);
        NameOk = valid && !taken;
        NameHint = name.Length == 0 ? "Minúsculas, números y guiones: tienda, mi-blog…"
            : !valid ? "Solo minúsculas, números y guiones (sin empezar ni acabar en guion)."
            : taken ? $"Ya hay un sitio {name}.test: elige otro nombre."
            : $"Se servirá en https://{name}.test";

        if (!_folderTouched)
        {
            var folder = Path.Combine(_main.App.Paths.Www, name);
            if (Folder != folder)
            {
                _settingFolder = true;
                Folder = folder;
                _settingFolder = false;
            }
        }

        string? full = null;
        try { full = Folder.Trim().Length == 0 ? null : Path.TrimEndingDirectorySeparator(Path.GetFullPath(Folder.Trim(), _main.App.Paths.Www)); }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException) { }
        if (full is null)
        {
            FolderOk = false;
            FolderHint = "Pon la carpeta del proyecto.";
        }
        else if (File.Exists(full))
        {
            FolderOk = false;
            FolderHint = $"{full} es un fichero, no una carpeta.";
        }
        else if (Directory.Exists(full) && Directory.EnumerateFileSystemEntries(full).Any())
        {
            FolderOk = false;
            FolderHint = GitTexts.FolderBusy(full);
        }
        else
        {
            FolderOk = true;
            FolderHint = Directory.Exists(full) ? "Existe y está vacía: se clona dentro." : "No existe: se crea al clonar.";
        }

        DbName = name.Length == 0 ? "" : DbAdmin.NameFor(name);
        OnPropertyChanged(nameof(DbText));
        OnPropertyChanged(nameof(Host));
        OnPropertyChanged(nameof(StepClone));
        OnPropertyChanged(nameof(StepVhost));
        OnPropertyChanged(nameof(StepHosts));
        OnPropertyChanged(nameof(StepDb));
        OnPropertyChanged(nameof(CanClone));
    }

    private void BuildPhpOptions()
    {
        var options = new List<PhpOption> { new($"Global (ahora {_global?.Version?.ToString() ?? "ninguno"})", null) };
        foreach (var php in _installed.Where(p => !p.ThreadSafe))
        {
            var eol = php.Version is { } v && _main.Audit.Evaluate(ComponentKind.Php, v).Verdict == AuditVerdict.Eol;
            options.Add(new PhpOption($"{php.Version}{(eol ? " (EOL)" : "")}", HomeViewModel.Branch(php.Name)));
        }
        PhpOptions = options;
        Php = options[0];
    }

    [RelayCommand]
    private async Task BrowseAsync()
    {
        if (await _main.PickFolderAsync("Carpeta del sitio (vacía, o que no exista)") is { } folder) Folder = folder;
    }

    [RelayCommand]
    private void Clone()
    {
        if (!CanClone || _url is null || _probe is null) return;
        var url = _accessUrl ?? _url;
        var request = new CloneRequest(url, Name.Trim().ToLowerInvariant(), Branch!.Name, Folder.Trim(), Php?.Spec, _probe, _access);
        var database = HasDb && CreateDb ? DbName : null;
        Close(new CloneWizardResult(request, database, database is not null, Undo, AccessText));
    }

    [RelayCommand]
    private void Cancel()
    {
        if (Busy) _cancel.Cancel();
        Close();
    }
}
