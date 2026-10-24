using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Roost.Core;
using Roost.Core.Config;
using Roost.Core.Git;
using Roost.Core.Scanning;
using Roost.Core.Sites;
using Roost.Desktop.Controls;
using Roost.Desktop.Services;

namespace Roost.Desktop.ViewModels;

/// <summary>Una clave SSH de Roost en Ajustes › Git: de qué sitio es y su huella (nunca la parte privada).</summary>
public sealed record GitKeyRow(string Name, string Detail, string PublicKey, string Fingerprint);

/// <summary>Un token en Ajustes › Git: para qué vale y con qué usuario (nunca el secreto).</summary>
public sealed record GitTokenRow(string Target, string Where, string Detail);

/// <summary>Un servidor conocido: su huella y de dónde sale (de serie, publicada, aceptada).</summary>
public sealed record GitHostRow(string Host, int? Port, string Token, string Fingerprint, string Source, Tone Tone, bool Stored);

/// <summary>
/// Ajustes › Git (maqueta J del diseño de git), en una pantalla propia a la que lleva la tarjeta Git
/// de Ajustes: el binario (el MinGit de Roost o el de <c>[git] exe</c>), el OpenSSH y el ssh-agent
/// de Windows, las claves SSH de Roost, los tokens, los servidores conocidos y cada cuánto se miran
/// las novedades de los sitios.
/// </summary>
public sealed partial class GitSettingsViewModel : PageViewModel
{
    private bool _loading;

    public GitSettingsViewModel(MainViewModel main) : base(main)
    {
        Subtitle = "Clonar y actualizar sitios desde git: qué git usa Roost, con qué claves y tokens entra y en qué servidores confía.";
    }

    public override string Title => "Git";

    private GitContext Git => Main.Sites.Git;

    // Binario

    [ObservableProperty] public partial string GitText { get; private set; } = "";

    [ObservableProperty] public partial Tone GitTone { get; private set; }

    [ObservableProperty] public partial bool GitInstalled { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UseOtherGit))]
    public partial bool UseRoostGit { get; set; } = true;

    public bool UseOtherGit
    {
        get => !UseRoostGit;
        set => UseRoostGit = !value;
    }

    [ObservableProperty] public partial string OtherGit { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasGitError))]
    public partial string? GitError { get; private set; }

    public bool HasGitError => GitError is not null;

    public string GitBuildText => $"{GitCatalog.Windows.Label} · de github.com/git-for-windows · SHA-256 fijado en Roost";

    // SSH

    [ObservableProperty] public partial string SshText { get; private set; } = "";

    [ObservableProperty] public partial Tone SshTone { get; private set; }

    [ObservableProperty] public partial string AgentText { get; private set; } = "";

    // Claves, tokens, servidores

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasKeys), nameof(NoKeys))]
    public partial IReadOnlyList<GitKeyRow> Keys { get; private set; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTokens), nameof(NoTokens))]
    public partial IReadOnlyList<GitTokenRow> Tokens { get; private set; } = [];

    [ObservableProperty] public partial IReadOnlyList<GitHostRow> Hosts { get; private set; } = [];

    public bool HasKeys => Keys.Count > 0;
    public bool NoKeys => Keys.Count == 0;
    public bool HasTokens => Tokens.Count > 0;
    public bool NoTokens => Tokens.Count == 0;

    public string TokensNote => Git.Access.Credentials.Prefix == GitCredentials.TestPrefix
        ? "Con ROOST_TEST_CREDENTIALS=1: solo los de prueba (Roost-test:git:…)."
        : "En el Administrador de credenciales de Windows (Roost:git:…), cifrados para tu usuario.";

    // Novedades

    [ObservableProperty] public partial bool CheckUpdates { get; set; }

    public IReadOnlyList<int> MinuteChoices { get; } = [15, 30, 60, 120];

    [ObservableProperty] public partial int CheckMinutes { get; set; } = 30;

    public string ChecksText => Main.Sites.GitChecksText;

    public override async Task ShownAsync()
    {
        // Es una página de Ajustes: la barra lateral sigue marcando Ajustes.
        foreach (var item in Main.RoostNav) item.IsActive = item.Page == Main.Settings;
        Load();
        await LoadSshAsync();
    }

    private void Load()
    {
        _loading = true;
        try
        {
            var config = Git.Config();
            UseRoostGit = config.GitExe is null;
            OtherGit = config.GitExe ?? "";
            GitError = null;
            var installed = new GitInstaller(Main.App.Paths).Find(GitCatalog.Windows);
            GitInstalled = installed is not null;
            if (config.GitExe is { } exe)
            {
                (GitText, GitTone) = File.Exists(exe)
                    ? ($"El tuyo: {exe}", Tone.Ok)
                    : ($"{exe} no existe: arregla la ruta o vuelve al MinGit de Roost.", Tone.Bad);
            }
            else
            {
                (GitText, GitTone) = installed is not null
                    ? ($"{GitCatalog.Windows.Label} · {installed.Directory} · SHA-256 verificado", Tone.Ok)
                    : ($"Aún no está: {GitCatalog.Windows.Label} se instala la primera vez que hace falta (verificado con su SHA-256).", Tone.Neutral);
            }

            var prefs = Main.App.Prefs;
            CheckUpdates = prefs.GitCheckUpdates;
            CheckMinutes = MinuteChoices.Contains(prefs.GitCheckMinutes) ? prefs.GitCheckMinutes : 30;

            LoadKeys();
            LoadTokens();
            LoadHosts();
            OnPropertyChanged(nameof(ChecksText));
            OnPropertyChanged(nameof(TokensNote));
        }
        finally
        {
            _loading = false;
        }
    }

    private void LoadKeys()
    {
        IReadOnlyList<Site> sites;
        try { sites = new SiteRegistry(Main.App.Paths).Load(); } catch (RoostException) { sites = []; }
        Keys = Git.Access.Keys.List().Select(k =>
        {
            var site = sites.FirstOrDefault(s => s.Git?.KeyName == k.Name) ?? sites.FirstOrDefault(s => s.Name == k.Name && s.Git is not null);
            var repo = site?.Git is { } g && GitUrl.TryParse(g.Remote, out var url, out _) ? $"{url.ProviderName} {url.Label}" : null;
            var detail = site is null ? "sin sitio (se quedó de un clonado que no acabó)" : repo is null ? site.Host : $"{site.Host} · {repo}";
            return new GitKeyRow(k.Name, detail, k.PublicKey, $"{k.Algorithm} {k.Fingerprint}");
        }).ToList();
    }

    private void LoadTokens()
    {
        try
        {
            Tokens = Git.Access.Credentials.List().Select(t =>
                new GitTokenRow(t.Target, GitTexts.Where(Git.Access.Credentials.Describe(t.Target)), string.IsNullOrEmpty(t.UserName) ? "sin usuario" : $"usuario {t.UserName}")).ToList();
        }
        catch (Exception ex) when (ex is RoostException or IOException or UnauthorizedAccessException)
        {
            Tokens = [];
        }
    }

    private void LoadHosts()
    {
        var hosts = Git.Access.KnownHosts;
        var stored = hosts.Load();
        var rows = new List<GitHostRow>();
        foreach (var group in stored.GroupBy(e => e.Hosts[0], StringComparer.OrdinalIgnoreCase))
        {
            var entry = group.First();
            var (host, port) = ParseToken(group.Key);
            var published = KnownHosts.PublishedFor(host, port);
            var status = hosts.Status(host, port);
            var (source, tone) = status.Trust switch
            {
                HostTrust.Published => ($"publicada por {GitUrl.NameOf(published!.Provider)}", Tone.Ok),
                HostTrust.Mismatch => ($"no es la que publica {GitUrl.NameOf(published!.Provider)}: olvídala", Tone.Bad),
                _ => (KnownHosts.AcceptedOn(entry) is { } on ? $"aceptada el {on}" : "aceptada", Tone.Neutral),
            };
            rows.Add(new GitHostRow(host, port, group.Key, Short(entry.Key.Fingerprint), source, tone, true));
        }
        foreach (var builtIn in KnownHosts.BuiltIn.Where(b => rows.All(r => !string.Equals(r.Token, b.Host, StringComparison.OrdinalIgnoreCase))))
            rows.Add(new GitHostRow(builtIn.Host, null, builtIn.Host, Short(builtIn.Keys[0].Fingerprint), $"de serie ({GitUrl.NameOf(builtIn.Provider)})", Tone.Ok, false));
        Hosts = rows;
    }

    private static string Short(string fingerprint) => fingerprint.Length > 19 ? fingerprint[..19] + "…" : fingerprint;

    private static (string Host, int? Port) ParseToken(string token)
    {
        if (token.StartsWith('[') && token.IndexOf("]:", StringComparison.Ordinal) is var close and > 0
            && int.TryParse(token[(close + 2)..], out var port))
            return (token[1..close], port);
        return (token, null);
    }

    /// <summary>El OpenSSH de Windows (<c>ssh -V</c>, que no conecta con nada) y el estado del ssh-agent, fuera del hilo de la UI.</summary>
    private async Task LoadSshAsync()
    {
        var tools = Git.Ssh;
        if (tools is null)
        {
            SshText = "Falta el cliente OpenSSH de Windows (una característica opcional: Configuración › Sistema › Características opcionales). Sin él, entra con un token HTTPS.";
            SshTone = Tone.Warn;
        }
        else
        {
            SshText = $"OpenSSH de Windows · {tools.Directory}";
            SshTone = Tone.Ok;
            try
            {
                var result = await Git.Runner.RunAsync(new CommandSpec(tools.Ssh, ["-V"], Timeout: TimeSpan.FromSeconds(10)), null, CancellationToken.None);
                var line = (result.Stderr.Length > 0 ? result.Stderr : result.Stdout).Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.StartsWith("OpenSSH", StringComparison.Ordinal));
                if (line is not null) SshText = $"{line.Split(',')[0].Replace("OpenSSH_for_Windows_", "OpenSSH de Windows ").Replace('_', ' ')} · {tools.Directory}";
            }
            catch (Exception ex) when (ex is RoostException or IOException or InvalidOperationException) { }
        }
        var agent = await Task.Run(Git.AgentState);
        AgentText = $"ssh-agent: {agent} · lo usa «Mi clave SSH» (las claves de {Git.UserSshFolder})";
    }

    partial void OnCheckUpdatesChanged(bool value) => SaveChecks();

    partial void OnCheckMinutesChanged(int value) => SaveChecks();

    private void SaveChecks()
    {
        if (_loading) return;
        Main.App.SavePrefs(Main.App.Prefs with { GitCheckUpdates = CheckUpdates, GitCheckMinutes = CheckMinutes });
        Main.Sites.StartGitChecks();
    }

    // Acciones

    [RelayCommand]
    private void Back() => _ = Main.NavigateToAsync(Main.Settings);

    [RelayCommand]
    private async Task InstallGitAsync()
    {
        if (await Main.Sites.EnsureGitAsync() is not null) Main.Flash($"{GitCatalog.Windows.Label} listo");
        Load();
    }

    [RelayCommand]
    private async Task BrowseGitAsync()
    {
        if (Main.StorageProvider is not { } storage) return;
        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Tu git.exe (por ejemplo, C:\\Program Files\\Git\\cmd\\git.exe)",
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("git.exe") { Patterns = ["git.exe"] }],
        });
        if (files.Count > 0 && files[0].TryGetLocalPath() is { } path)
        {
            OtherGit = path;
            UseRoostGit = false;
        }
    }

    [RelayCommand]
    private async Task SaveGitAsync()
    {
        GitError = null;
        string? exe = null;
        if (!UseRoostGit)
        {
            exe = OtherGit.Trim().Trim('"');
            if (exe.Length == 0 || !File.Exists(exe) || !string.Equals(Path.GetFileName(exe), "git.exe", StringComparison.OrdinalIgnoreCase) && OperatingSystem.IsWindows())
            {
                GitError = "Elige un git.exe que exista (el de Git for Windows suele estar en C:\\Program Files\\Git\\cmd\\git.exe).";
                return;
            }
        }
        await Main.RunAsync("No se pudo guardar el git", () =>
        {
            RoostConfig.SetGitExe(Main.App.Paths.ConfigFile, exe);
            return Task.CompletedTask;
        });
        Load();
        Main.Flash(exe is null ? $"Roost usa su {GitCatalog.Windows.Label}" : $"Roost usa {exe}");
    }

    [RelayCommand]
    private void AgentHelp() => Main.Sites.OpenBrowser(CloneWizardViewModel.OwnHelpUrl);

    [RelayCommand]
    private Task CopyKeyAsync(GitKeyRow row) => Main.CopyAsync(row.PublicKey, $"Clave pública de {row.Name} copiada");

    [RelayCommand]
    private async Task RemoveKeyAsync(GitKeyRow row)
    {
        if (!await Main.ConfirmAsync($"Quitar la clave de {row.Name}",
                $"Borra la clave SSH de Roost de {row.Name} de este equipo ({row.Detail}). Si un sitio la usa, no podrá actualizarse hasta que tenga otra. Quita también la pública de las deploy keys del repositorio.",
                "Quitar la clave", danger: true))
            return;
        Git.Access.Keys.Remove(row.Name);
        LoadKeys();
        Main.Flash($"Quitada la clave de {row.Name}");
    }

    [RelayCommand]
    private async Task ReplaceTokenAsync(GitTokenRow row)
    {
        var where = row.Where;
        if (!CredentialLocation.TryParse(where, out var location, out _)) return;
        var host = location.Host.Split(':')[0];
        var provider = GitUrl.TryParse($"https://{where}{(location.Path is null ? "/x" : "")}", out var url, out _) ? url : null;
        var existing = Git.Access.Credentials.Store.Read(row.Target);
        var dialog = new GitTokenDialogViewModel($"Sustituir el token de {where}", where, host, existing?.UserName, provider?.NewTokenUrl, provider?.TokenPermission ?? "leer el repositorio", Main.Sites.OpenBrowser);
        if (await Main.ShowDialogAsync(dialog) is not GitTokenEntry entry) return;
        await Main.RunAsync("No se guardó el token", () =>
        {
            Git.Access.Credentials.Save(location, entry.Token, entry.User);
            return Task.CompletedTask;
        });
        LoadTokens();
        Main.Flash($"Token de {where} sustituido");
    }

    [RelayCommand]
    private async Task RemoveTokenAsync(GitTokenRow row)
    {
        if (!await Main.ConfirmAsync($"Quitar el token de {row.Where}",
                $"Borra el token de {row.Where} del Administrador de credenciales. Los sitios que entraban con él irán como públicos (si el repositorio es privado, no podrán actualizarse). Si ya no lo usas, revócalo también en el proveedor.",
                "Quitar token", danger: true))
            return;
        Git.Access.Credentials.Remove(row.Target);
        LoadTokens();
        Main.Flash($"Quitado el token de {row.Where}");
    }

    [RelayCommand]
    private async Task ForgetHostAsync(GitHostRow row)
    {
        var builtIn = KnownHosts.PublishedFor(row.Host, row.Port) is not null;
        if (!await Main.ConfirmAsync($"Olvidar {row.Token}",
                builtIn
                    ? $"Quita lo guardado para {row.Token}. Es un servidor de serie: la próxima vez vuelve la huella que publica su proveedor."
                    : $"Quita la huella guardada de {row.Token}. La próxima conexión enseñará la que presente y tendrás que volver a comprobarla y aceptarla.",
                "Olvidar"))
            return;
        Git.Access.KnownHosts.Forget(row.Host, row.Port);
        LoadHosts();
        Main.Flash($"{row.Token} olvidado");
    }

    [RelayCommand]
    private async Task CheckNowAsync()
    {
        var news = await Main.Sites.CheckGitUpdatesAsync(force: true);
        OnPropertyChanged(nameof(ChecksText));
        Main.Flash(news == 0 ? "Comprobado: ningún sitio con novedades" : $"{GitTexts.Plural(news, "sitio tiene", "sitios tienen")} novedades");
    }
}
