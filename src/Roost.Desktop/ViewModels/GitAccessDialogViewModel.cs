using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Roost.Core;
using Roost.Core.Git;
using Roost.Core.Sites;
using Roost.Desktop.Controls;
using Roost.Desktop.Services;

namespace Roost.Desktop.ViewModels;

/// <summary>
/// «Acceso de tienda.test» (maqueta I): cómo entra el sitio en su repositorio y qué se puede hacer.
/// Con la clave de Roost, la pública (Copiar, sus deploy keys), quitarla o crear otra; con token,
/// cuál es (nunca el secreto), sustituirlo o quitarlo; sin credenciales por HTTPS, guardar uno. La
/// huella del servidor, el último acceso de esta sesión y «Probar acceso» (ls-remote).
/// </summary>
public sealed partial class GitAccessDialogViewModel : DialogViewModel
{
    private readonly MainViewModel _main;
    private readonly GitContext _git;
    private readonly Site _site;
    private readonly SiteGitState _state;
    private readonly Action<string> _openUrl;
    private readonly Func<Task<GitLocation?>> _ensureGit;
    private SiteGit _info;

    internal GitAccessDialogViewModel(MainViewModel main, GitContext git, SiteGit info, Site site, SiteGitState state, Action<string> openUrl, Func<Task<GitLocation?>> ensureGit)
    {
        _main = main;
        _git = git;
        _info = info;
        _site = site;
        _state = state;
        _openUrl = openUrl;
        _ensureGit = ensureGit;
        Url = GitUrl.TryParse(info.Remote, out var url, out _) ? url : null;
        Title = $"Acceso de {site.Host}";
        Load();
    }

    public string Title { get; }

    public GitUrl? Url { get; }

    /// <summary>Si se cambió algo del sitio (el tipo de acceso): quien lo abre repinta.</summary>
    public bool Changed { get; private set; }

    [ObservableProperty] public partial string Subtitle { get; private set; } = "";

    [ObservableProperty] public partial bool IsKey { get; private set; }

    [ObservableProperty] public partial bool IsHttps { get; private set; }

    [ObservableProperty] public partial bool IsOwn { get; private set; }

    [ObservableProperty] public partial bool IsPublicSsh { get; private set; }

    // La clave de Roost

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPublicKey), nameof(KeyMissing))]
    public partial string? PublicKey { get; private set; }

    [ObservableProperty] public partial string KeyText { get; private set; } = "";

    public bool HasPublicKey => PublicKey is not null;

    public bool KeyMissing => IsKey && PublicKey is null;

    public bool HasDeployKeysUrl => Url?.DeployKeysUrl is not null;

    public string DeployKeysText => $"Deploy keys de {Url?.Label ?? "el repositorio"}";

    // El token

    [ObservableProperty] public partial string TokenText { get; private set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TokenLabel))]
    public partial bool HasToken { get; private set; }

    public string TokenLabel => HasToken ? "Sustituir token…" : "Guardar un token…";

    // Servidor y último acceso

    [ObservableProperty] public partial string ServerText { get; private set; } = "";

    [ObservableProperty] public partial Tone ServerTone { get; private set; }

    [ObservableProperty] public partial string LastText { get; private set; } = "";

    [ObservableProperty] public partial Tone LastTone { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Idle))]
    public partial bool Busy { get; private set; }

    public bool Idle => !Busy;

    public override bool CanDismiss => !Busy;

    private void Load()
    {
        var access = _info.Access;
        IsKey = access == SiteGit.AccessKey;
        IsOwn = access == SiteGit.AccessOwn;
        IsHttps = Url?.Scheme == GitScheme.Https;
        IsPublicSsh = Url?.Scheme == GitScheme.Ssh && access == SiteGit.AccessNone;
        Subtitle = $"{GitLog.Redact(_info.Remote)} · {GitTexts.AccessLabel(_info)}";

        var keyName = _info.KeyName ?? _site.Name;
        var key = IsKey ? _git.Access.Keys.Find(keyName) : null;
        PublicKey = key?.PublicKey;
        KeyText = !IsKey ? "" : key is null
            ? $"No está la clave de {keyName} ({_git.Access.Keys.PrivateFile(keyName)}): sin ella, el sitio no puede actualizarse. Crea otra y ponla como deploy key de {Url?.Label}."
            : $"{key.Algorithm} {key.Fingerprint} · sin contraseña, solo legible por tu usuario.";

        if (IsHttps && Url is not null && CredentialLocation.For(Url) is { } location)
        {
            var found = _git.Access.Credentials.Find(location);
            HasToken = found is not null;
            TokenText = found is null
                ? $"No hay ningún token guardado para {location.Key[(location.Protocol.Length + 3)..]}: sin él, git entra como público."
                : $"Token de {GitTexts.Where(_git.Access.Credentials.Describe(found.Target))}{(found.UserName is { Length: > 0 } u ? $" (usuario {u})" : "")} · guardado en el Administrador de credenciales.";
        }

        if (Url is { Scheme: GitScheme.Ssh, Host: { } host })
        {
            var status = _git.Access.KnownHosts.Status(host, Url.Port);
            (ServerText, ServerTone) = status.Trust switch
            {
                HostTrust.Published => ($"{status.Token} · huella conocida (publicada por {GitUrl.NameOf(status.Published!.Provider)})", Tone.Ok),
                HostTrust.Trusted => ($"{status.Token} · huella aceptada{(KnownHosts.AcceptedOn(status.Stored[0]) is { } on ? $" el {on}" : "")} ({status.Stored[0].Key.Fingerprint})", Tone.Ok),
                HostTrust.Mismatch => ($"{status.Token} · lo guardado no es la huella que publica {GitUrl.NameOf(status.Published!.Provider)}: no se conecta (Ajustes › Git › Olvidar)", Tone.Bad),
                _ => ($"{status.Token} · huella desconocida: Roost aún no la ha aceptado", Tone.Warn),
            };
        }
        else
        {
            ServerText = Url is { IsLocal: true } ? "carpeta de este equipo" : $"{Url?.Host ?? "?"} · HTTPS, con el almacén de certificados de Windows";
            ServerTone = Tone.Ok;
        }
        ShowLast();
    }

    private void ShowLast()
    {
        if (_state.When is not { } when)
        {
            LastText = "sin comprobar en esta sesión";
            LastTone = Tone.Neutral;
            return;
        }
        var look = SitesViewModel.Look(_info, _state, _site.Host);
        if (look.NoAccess || _state.Failure is not null || _state.Error is not null)
        {
            LastText = $"{GitTexts.When(when)} · {look.Note ?? look.Chip}";
            LastTone = Tone.Bad;
        }
        else
        {
            LastText = $"{GitTexts.When(when)} · correcto";
            LastTone = Tone.Ok;
        }
    }

    [RelayCommand]
    private async Task TestAsync()
    {
        if (Busy) return;
        if (await _ensureGit() is not { } git) return;
        Busy = true;
        LastText = "probando…";
        LastTone = Tone.Accent;
        try
        {
            var check = await Task.Run(() => _git.Manager(git).CheckUpdatesAsync(_site.Name, CancellationToken.None));
            _state.Check = check;
            _state.Failure = check.Failure;
            _state.Error = null;
            if (check.Status is { } status) _state.Status = status;
            if (check.Branches.Count > 0) _state.Branches = check.Branches;
        }
        catch (Exception ex) when (ex is RoostException or IOException or UnauthorizedAccessException)
        {
            _state.Error = ex.Message;
        }
        finally
        {
            _state.When = DateTimeOffset.Now;
            Busy = false;
        }
        ShowLast();
    }

    [RelayCommand]
    private Task CopyKeyAsync() => PublicKey is { } key ? _main.CopyAsync(key, "Clave pública copiada") : Task.CompletedTask;

    [RelayCommand]
    private void OpenDeployKeys()
    {
        if (Url?.DeployKeysUrl is { } page) _openUrl(page);
    }

    [RelayCommand]
    private async Task GenerateKeyAsync()
    {
        if (Busy || !IsKey || HasPublicKey) return;
        Busy = true;
        try
        {
            var key = await _git.Access.Keys.GenerateAsync(_info.KeyName ?? _site.Name, CancellationToken.None);
            PublicKey = key.PublicKey;
            KeyText = $"Creada: {key.Algorithm} {key.Fingerprint}. Ponla como deploy key de {Url?.Label} (solo lectura) y pulsa Probar acceso.";
            _state.Error = null;
        }
        catch (RoostException ex)
        {
            KeyText = ex.Message;
        }
        finally
        {
            Busy = false;
        }
    }

    [RelayCommand]
    private async Task RemoveKeyAsync()
    {
        if (Busy || !HasPublicKey) return;
        var name = _info.KeyName ?? _site.Name;
        if (!await _main.ConfirmAsync($"Quitar la clave de {_site.Host}",
                $"Borra la clave privada de {name} de este equipo: {_site.Host} no podrá actualizarse hasta que tenga otra. Quita también la pública en {Url?.ProviderName ?? "el servidor"}{(Url?.DeployKeysUrl is { } page ? $" ({page})" : "")}.",
                "Quitar la clave", danger: true))
            return;
        _git.Access.Keys.Remove(name);
        Load();
        _main.Flash($"Quitada la clave SSH de {_site.Host}");
    }

    [RelayCommand]
    private async Task ReplaceTokenAsync()
    {
        if (Busy || Url is null || CredentialLocation.For(Url) is not { } location) return;
        var where = location.Key[(location.Protocol.Length + 3)..];
        var existing = _git.Access.Credentials.Find(location);
        var dialog = new GitTokenDialogViewModel(HasToken ? $"Sustituir el token de {_site.Host}" : $"Guardar un token para {_site.Host}",
            where, Url.Host ?? "", existing?.UserName, Url.NewTokenUrl, Url.TokenPermission, _openUrl);
        if (await _main.ShowDialogAsync(dialog) is not GitTokenEntry entry) return;
        try
        {
            var target = _git.Access.Credentials.Save(location, entry.Token, entry.User);
            if (_info.Access != SiteGit.AccessToken || _info.CredentialTarget != target)
                SetAccess(_info with { Access = SiteGit.AccessToken, CredentialTarget = target });
            _state.Failure = null;
            _state.Error = null;
            Load();
            _main.Flash($"Token de {where} guardado");
        }
        catch (RoostException ex)
        {
            await _main.InfoAsync("No se guardó el token", ex.Message, Tone.Bad);
        }
    }

    [RelayCommand]
    private async Task RemoveTokenAsync()
    {
        if (Busy || !HasToken || Url is null || CredentialLocation.For(Url) is not { } location) return;
        var found = _git.Access.Credentials.Find(location);
        if (found is null) return;
        var where = GitTexts.Where(_git.Access.Credentials.Describe(found.Target));
        if (!await _main.ConfirmAsync($"Quitar el token de {_site.Host}",
                $"Borra el token de {where} del Administrador de credenciales. Sin él, {_site.Host} entra como público (si el repositorio es privado, no podrá actualizarse). Si ya no lo usas, revócalo también en {Url.ProviderName}.",
                "Quitar token", danger: true))
            return;
        _git.Access.Credentials.Remove(found.Target);
        if (_info.Access == SiteGit.AccessToken) SetAccess(_info with { Access = SiteGit.AccessNone, CredentialTarget = null });
        Load();
        _main.Flash($"Quitado el token de {where}");
    }

    private void SetAccess(SiteGit info)
    {
        var registry = new SiteRegistry(_main.App.Paths);
        if (registry.Find(_site.Name) is not { } current) return;
        registry.Upsert(current with { Git = info });
        _info = info;
        _state.Git = info;
        Changed = true;
    }

    [RelayCommand]
    private void Finish() => Close();
}
