using System.Security.Cryptography.X509Certificates;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Roost.Core;
using Roost.Core.Audit;
using Roost.Core.Config;
using Roost.Core.Install;
using Roost.Core.Models;
using Roost.Core.Scanning;
using Roost.Core.Sites;
using Roost.Core.Stack;
using Roost.Desktop.Controls;
using Roost.Desktop.Services;

namespace Roost.Desktop.ViewModels;

/// <summary>Un sitio de la lista.</summary>
public sealed partial class SiteRow(Site site) : ObservableObject
{
    public Site Site { get; } = site;
    public string Host => Site.Host;
    public string DocumentRoot => Site.DocumentRoot;
    public required string Php { get; init; }
    public required Tone PhpTone { get; init; }
    public required string? PhpName { get; init; }
    public required string Cert { get; init; }
    public required Tone CertTone { get; init; }
    public required bool InHosts { get; init; }

    /// <summary>Con el DNS local, el nombre resuelve sin estar en el hosts.</summary>
    public required bool ByDns { get; init; }
    public string Hosts => ByDns ? "DNS" : InHosts ? "sí" : "no";
    public Tone HostsTone => ByDns || InHosts ? Tone.Ok : Tone.Warn;

    [ObservableProperty] public partial bool IsSelected { get; set; }
}

/// <summary>Una opción del desplegable «PHP del sitio».</summary>
public sealed record PhpOption(string Label, string? Spec)
{
    public override string ToString() => Label;
}

/// <summary>Un acceso rápido a una subcarpeta típica (public, web, dist…) que existe en el proyecto.</summary>
public sealed record RootChoice(string Root)
{
    public string Label => Root + "/";
}

/// <summary>
/// Sitios: cada proyecto en https://&lt;nombre&gt;.test con su certificado, su PHP (el global o
/// uno propio), su raíz web y su línea en el hosts. Alta con el diálogo Nuevo sitio; la carpeta
/// del proyecto nunca se toca al quitarlo.
/// </summary>
public sealed partial class SitesViewModel : PageViewModel
{
    private IReadOnlyList<SiteRow> _all = [];
    private bool _settingPhp;

    public SitesViewModel(MainViewModel main) : base(main)
    {
        Subtitle = "Cada sitio se sirve en https://<nombre>.test con su certificado. Puede usar el PHP global o uno propio.";
    }

    public override string Title => "Sitios";

    [ObservableProperty] public partial IReadOnlyList<SiteRow> Rows { get; private set; } = [];
    [ObservableProperty] public partial SiteRow? Selected { get; private set; }
    [ObservableProperty] public partial string Search { get; set; } = "";
    [ObservableProperty] public partial string Count { get; private set; } = "";
    [ObservableProperty] public partial IReadOnlyList<PhpOption> PhpOptions { get; private set; } = [];
    [ObservableProperty] public partial PhpOption? SelectedPhp { get; set; }
    [ObservableProperty] public partial string Urls { get; private set; } = "";
    [ObservableProperty] public partial string RootText { get; private set; } = "";
    [ObservableProperty] public partial string CertDetail { get; private set; } = "";
    [ObservableProperty] public partial string ConfPath { get; private set; } = "";

    /// <summary>Las subcarpetas típicas que existen en el proyecto y no son ya la raíz web.</summary>
    [ObservableProperty] public partial IReadOnlyList<RootChoice> RootChoices { get; private set; } = [];

    /// <summary>Si la carpeta del proyecto o la raíz web ya no existen; vacío si todo está.</summary>
    [ObservableProperty] public partial string PathWarning { get; private set; } = "";

    public bool HasSelection => Selected is not null;
    public bool HasSites => _all.Count > 0;
    public bool MissingFromHosts => Selected is { InHosts: false, ByDns: false };
    public bool HasRootChoices => RootChoices.Count > 0;
    public bool HasSubRoot => Selected?.Site.Root is not null;
    public bool HasPathWarning => PathWarning.Length > 0;
    public bool FolderExists => Selected is { } row && Directory.Exists(row.Site.Folder);

    partial void OnSearchChanged(string value) => ApplySearch();
    partial void OnSelectedChanged(SiteRow? value)
    {
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(MissingFromHosts));
        OnPropertyChanged(nameof(HasSubRoot));
        OnPropertyChanged(nameof(FolderExists));
    }

    partial void OnRootChoicesChanged(IReadOnlyList<RootChoice> value) => OnPropertyChanged(nameof(HasRootChoices));
    partial void OnPathWarningChanged(string value) => OnPropertyChanged(nameof(HasPathWarning));

    partial void OnSelectedPhpChanged(PhpOption? value)
    {
        if (_settingPhp || value is null || Selected is not { } row || value.Spec == row.Site.Php) return;
        _ = SetPhpAsync(row, value);
    }

    public override Task ShownAsync()
    {
        // Al volver a la pantalla se vuelven a mirar las bases de datos (una creada desde la CLI aparece).
        ForgetDatabases();
        Build();
        return Task.CompletedTask;
    }

    public override void StackChanged()
    {
        ForgetDatabases();
        Build();
    }

    private void Build()
    {
        var paths = Main.App.Paths;
        RoostConfig config;
        try { config = RoostConfig.Load(paths.ConfigFile); } catch (RoostException) { config = new RoostConfig(); }
        IReadOnlyList<Site> sites;
        try { sites = new SiteRegistry(paths).Load(); }
        catch (RoostException ex)
        {
            sites = [];
            _ = Main.InfoAsync("No se pueden leer los sitios", ex.Message, Tone.Bad);
        }

        var installed = new PhpInstaller(paths).ListInstalled();
        var planner = new StackPlanner(paths, ProcessRunner.Default);
        InstalledPhp? global = null;
        try { global = planner.ResolvePhp(config, installed); } catch (RoostException) { }
        IReadOnlySet<string> inHosts;
        try { inHosts = new HostsUpdater().Current().ToHashSet(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or RoostException) { inHosts = new HashSet<string>(); }

        var ca = new LocalCa(paths);
        _all = sites.Select(site =>
        {
            string? phpName = null;
            try { phpName = global is null ? null : planner.ResolveSitePhp(site, global, installed).Name; } catch (RoostException) { }
            var (php, phpTone) = PhpChip(site, phpName);
            var (cert, certTone) = CertChip(ca, site);
            return new SiteRow(site)
            {
                Php = php,
                PhpTone = phpTone,
                PhpName = phpName,
                Cert = cert,
                CertTone = certTone,
                InHosts = inHosts.Contains(site.Host),
                ByDns = config.UsesLocalDns,
            };
        }).ToList();

        // Las opciones de PHP: el global y cada instalación (por su rama si es la mejor de ella).
        var options = new List<PhpOption> { new($"Global (ahora {global?.Version?.ToString() ?? "ninguno"})", null) };
        foreach (var php in installed.Where(p => !p.ThreadSafe))
        {
            var branch = HomeViewModel.Branch(php.Name);
            var best = planner.ResolvePhp(new RoostConfig { PhpVersion = branch }, installed);
            var eol = php.Version is { } v && Main.Audit.Evaluate(ComponentKind.Php, v).Verdict == AuditVerdict.Eol;
            options.Add(new PhpOption($"{php.Version}{(eol ? " (EOL)" : "")}", best.Name == php.Name ? branch : php.Name));
        }
        PhpOptions = options;

        Count = _all.Count == 1 ? "1 sitio" : $"{_all.Count} sitios";
        OnPropertyChanged(nameof(HasSites));
        ApplySearch(config);
    }

    private void ApplySearch(RoostConfig? config = null)
    {
        var q = Search.Trim();
        Rows = q.Length == 0 ? _all
            : _all.Where(r => r.Host.Contains(q, StringComparison.OrdinalIgnoreCase) || r.DocumentRoot.Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();
        var keep = Rows.FirstOrDefault(r => r.Site.Name == Selected?.Site.Name) ?? Rows.FirstOrDefault();
        Pick(keep, config);
    }

    private void Pick(SiteRow? row, RoostConfig? config = null)
    {
        foreach (var r in _all) r.IsSelected = r == row;
        Selected = row;
        if (row is null)
        {
            Project = null;
            return;
        }

        var paths = Main.App.Paths;
        if (config is null)
        {
            try { config = RoostConfig.Load(paths.ConfigFile); } catch (RoostException) { config = new RoostConfig(); }
        }
        var site = row.Site;
        var http = config.HttpPort == 80 ? $"http://{site.Host}" : $"http://{site.Host}:{config.HttpPort}";
        Urls = StackPlanner.SiteUrl(site, config.HttpsPort).TrimEnd('/') + Environment.NewLine + http;
        RootText = RootLabel(site);
        CertDetail = $"Roost Local CA · {site.Host}, *.{site.Host}";
        ConfPath = new SiteManager(paths).ConfFile(site, config);
        PickDatabase(site, config);
        PickComposer(site);

        // La carpeta o la raíz web pueden haber desaparecido desde que se registró el sitio.
        var folderExists = Directory.Exists(site.Folder);
        PathWarning = !folderExists
            ? $"La carpeta del proyecto ya no existe ({site.Folder}). El sitio dará error hasta que vuelva; si ya no lo quieres, quítalo."
            : site.Root is not null && !Directory.Exists(site.DocumentRoot)
                ? $"La raíz web {RootText} ya no existe dentro de la carpeta. Elige otra con Cambiar… o vuelve a servir la carpeta del proyecto."
                : "";
        var currentRoot = site.Root?.Replace('\\', '/').TrimEnd('/');
        RootChoices = folderExists
            ? SiteManager.TypicalRoots
                .Where(r => !string.Equals(r, currentRoot, StringComparison.OrdinalIgnoreCase) && Directory.Exists(Path.Combine(site.Folder, r)))
                .Select(r => new RootChoice(r)).ToList()
            : [];
        OnPropertyChanged(nameof(HasSubRoot));
        OnPropertyChanged(nameof(FolderExists));

        SelectPhpOf(site);
    }

    private static string RootLabel(Site site) => site.Root is null ? "(la carpeta)" : site.Root.Replace('\\', '/').TrimEnd('/') + "/";

    /// <summary>Deja el desplegable en el PHP del sitio sin disparar un cambio.</summary>
    private void SelectPhpOf(Site site)
    {
        _settingPhp = true;
        SelectedPhp = PhpOptions.FirstOrDefault(o => o.Spec == site.Php)
                      ?? (site.Php is null ? PhpOptions.FirstOrDefault() : new PhpOption(site.Php, site.Php));
        _settingPhp = false;
    }

    private (string, Tone) PhpChip(Site site, string? phpName)
    {
        if (phpName is null) return (site.Php is null ? "sin PHP" : $"PHP {site.Php} (no instalado)", Tone.Warn);
        var branch = HomeViewModel.Branch(phpName);
        var eol = ComponentVersion.TryParse(phpName, out var v) && Main.Audit.Evaluate(ComponentKind.Php, v).Verdict == AuditVerdict.Eol;
        var label = site.Php is null ? $"{branch} (global)" : branch;
        return eol ? ($"{label} · EOL", Tone.Bad) : (label, Tone.Neutral);
    }

    private static (string, Tone) CertChip(LocalCa ca, Site site)
    {
        var file = ca.SiteCertFile(site.Name);
        if (!File.Exists(file)) return ("se emite al iniciar", Tone.Neutral);
        try
        {
            using var cert = X509Certificate2.CreateFromPem(File.ReadAllText(file));
            var left = cert.NotAfter - DateTime.Now;
            return left < TimeSpan.Zero ? ("caducado", Tone.Bad)
                : left < LocalCa.RenewBefore ? ($"hasta {cert.NotAfter:d-MMM-yyyy} · se renueva", Tone.Warn)
                : ($"hasta {cert.NotAfter:d-MMM-yyyy}", Tone.Ok);
        }
        catch (Exception ex) when (ex is IOException or System.Security.Cryptography.CryptographicException or ArgumentException)
        {
            return ("ilegible", Tone.Bad);
        }
    }

    /// <summary>El selector de carpetas del sistema, abierto en <paramref name="startIn"/> si existe; null si se cancela.</summary>
    internal static async Task<string?> PickFolderAsync(MainViewModel main, string title, string? startIn)
    {
        if (main.StorageProvider is not { } storage) return null;
        var start = startIn is not null && Directory.Exists(startIn) ? await storage.TryGetFolderFromPathAsync(startIn) : null;
        var folders = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = title, AllowMultiple = false, SuggestedStartLocation = start });
        return folders.Count == 0 ? null : folders[0].TryGetLocalPath();
    }

    // Acciones

    [RelayCommand]
    private void Choose(SiteRow row) => Pick(row);

    /// <summary>
    /// Cambiar el PHP del sitio rearranca todo el stack: se confirma antes (si está en marcha) y,
    /// si el rearranque no va, el sitio vuelve a su PHP de antes en el registro.
    /// </summary>
    private async Task SetPhpAsync(SiteRow row, PhpOption option)
    {
        var label = option.Spec ?? "el global";
        if (Main.Stack.IsRunning && !await Main.ConfirmAsync($"Cambiar el PHP de {row.Host}",
                $"Cambiar el PHP de {row.Host} a {label} rearranca Roost (unos segundos).", "Cambiar"))
        {
            SelectPhpOf(row.Site);
            return;
        }

        var applied = false;
        await Main.RunAsync($"No se pudo cambiar el PHP de {row.Host}", async () =>
        {
            var registry = new SiteRegistry(Main.App.Paths);
            registry.Upsert(row.Site with { Php = option.Spec });
            applied = await Main.ApplyAsync();
            // El rearranque no fue (el diálogo ya lo ha contado): el registro vuelve a como estaba.
            if (!applied) registry.Upsert(row.Site);
        });
        Main.NotifyConfigChanged();
        if (applied) Main.Flash(Main.Stack.IsRunning ? $"{row.Host} usa ahora PHP {label}" : $"{row.Host} usará PHP {label} al iniciar");
    }

    [RelayCommand]
    private Task OpenAsync() => Main.RunAsync("No se pudo abrir el navegador", () =>
    {
        if (Selected is { } row) Shell.OpenUrl(Urls.Split(Environment.NewLine)[0] + "/");
        return Task.CompletedTask;
    });

    [RelayCommand]
    private Task CopyUrlAsync() => Selected is null ? Task.CompletedTask : Main.CopyAsync(Urls.Split(Environment.NewLine)[0] + "/", "URL copiada");

    [RelayCommand]
    private Task CopyPathAsync() => Selected is { } row ? Main.CopyAsync(row.Site.Folder, "Ruta copiada") : Task.CompletedTask;

    [RelayCommand]
    private Task OpenFolderAsync() => Main.RunAsync("No se pudo abrir la carpeta", () =>
    {
        if (Selected is { } row) Shell.OpenFolder(row.Site.Folder);
        return Task.CompletedTask;
    });

    [RelayCommand]
    private Task OpenTerminalAsync() => Main.RunAsync("No se pudo abrir la terminal", () =>
    {
        if (Selected is not { } row) return Task.CompletedTask;
        var php = row.PhpName is null ? null : new PhpInstaller(Main.App.Paths).ListInstalled().FirstOrDefault(p => p.Name == row.PhpName);
        Shell.OpenTerminal(Main.App.Paths, php?.Directory, row.Site.Folder);
        return Task.CompletedTask;
    });

    [RelayCommand]
    private Task EditConfAsync() => Main.RunAsync("No se pudo abrir el vhost", () =>
    {
        if (!File.Exists(ConfPath)) throw new RoostException("El vhost se escribe al pulsar Iniciar todo; luego podrás editarlo.");
        Shell.OpenFile(ConfPath);
        return Task.CompletedTask;
    });

    [RelayCommand]
    private Task RenewCertAsync() => Main.RunAsync("No se pudo renovar el certificado", async () =>
    {
        if (Selected is not { } row) return;
        new LocalCa(Main.App.Paths).EnsureSiteCert(row.Site, force: true);
        Main.NotifyConfigChanged();
        if (!await Main.ApplyAsync()) return;
        Main.Flash($"Certificado de {row.Host} renovado");
    });

    [RelayCommand]
    private async Task SyncHostsAsync() => await Main.SyncHostsAsync();

    // La raíz web

    /// <summary>«Cambiar…»: el selector de carpetas, abierto en la del proyecto; lo elegido tiene que estar dentro.</summary>
    [RelayCommand]
    private async Task ChangeRootAsync()
    {
        if (Selected is not { } row) return;
        if (await PickFolderAsync(Main, $"Raíz web de {row.Host} (una subcarpeta del proyecto)", row.Site.Folder) is not { } picked) return;
        await ApplyRootAsync(row, picked);
    }

    [RelayCommand]
    private Task UseProjectRootAsync() => Selected is { } row ? ApplyRootAsync(row, null) : Task.CompletedTask;

    [RelayCommand]
    private Task SetRootAsync(string root) => Selected is { } row ? ApplyRootAsync(row, root) : Task.CompletedTask;

    /// <summary>Guarda la raíz web (validada en Core) y la aplica. De momento rearranca el stack; más adelante recargará solo el servidor web.</summary>
    private Task ApplyRootAsync(SiteRow row, string? root) => Main.RunAsync($"No se pudo cambiar la raíz web de {row.Host}", async () =>
    {
        var site = new SiteManager(Main.App.Paths).SetRoot(row.Site.Name, root);
        Main.NotifyConfigChanged();
        if (!await Main.ApplyAsync()) return;
        Main.Flash($"{row.Host} sirve ahora {(site.Root is null ? "la carpeta del proyecto" : site.Root + "/")}");
    });

    [RelayCommand]
    private async Task RemoveAsync()
    {
        if (Selected is not { } row) return;
        if (!await Main.ConfirmAsync($"Quitar {row.Host}",
                (row.ByDns
                    ? "Deja de servirse (el DNS local deja de resolverlo al momento). "
                    : "Deja de servirse y se quita del hosts (Windows pedirá permiso de administrador). ")
                + $"La carpeta del proyecto ({row.Site.Folder}) no se toca.",
                "Quitar sitio", danger: true))
            return;

        var removed = false;
        var hostsOk = true;
        var applied = false;
        await Main.RunAsync($"No se pudo quitar {row.Host}", async () =>
        {
            new SiteManager(Main.App.Paths).Remove(row.Site.Name);
            removed = true;
            Main.NotifyConfigChanged();
            // Si se cancela el permiso del hosts, el sitio ya está quitado: el cambio se aplica igual
            // (deja de servirse) y se cuenta la verdad al final.
            hostsOk = await Main.SyncHostsAsync(showError: false);
            applied = await Main.ApplyAsync();
        });
        Main.NotifyConfigChanged();
        if (!removed) return;

        if (!hostsOk)
            await Main.InfoAsync($"{row.Host} está quitado, pero sigue en el hosts",
                $"{Main.HostsError}\n\nEl sitio ya no está registrado{(applied ? " ni se sirve" : "")}; solo queda su línea en el hosts, que no molesta. "
                + "Se quitará la próxima vez que Roost sincronice el hosts: al crear o quitar otro sitio, o con «Añadir al hosts» en el panel de un sitio al que le falte. "
                + "Con el DNS local (Ajustes › Nombres .test) el hosts deja de hacer falta.",
                Tone.Warn);
        else if (applied)
            Main.Flash($"{row.Host} quitado");
    }

    [RelayCommand]
    private async Task NewSiteAsync()
    {
        var dialog = new NewSiteDialogViewModel(Main);
        if (await Main.ShowDialogAsync(dialog) is not NewSiteRequest request) return;
        // Pasos y plantillas: con una plantilla, el proyecto se crea en el diálogo de los pasos (SitesViewModel.Deploy.cs).
        await (request.Template is null ? CreateAsync(request) : CreateFromTemplateAsync(request));
    }

    /// <summary>Crear y abrir: registra, pasa al hosts, (re)arranca, crea la base de datos, ofrece confiar en la CA y abre el navegador.</summary>
    private Task CreateAsync(NewSiteRequest request) => Main.RunAsync($"No se pudo crear {request.Name}.test", async () =>
    {
        var paths = Main.App.Paths;
        var sites = new SiteManager(paths);
        var change = sites.Add(request.Name, request.Folder, request.Root, request.Php ?? "global");
        // La base de datos elegida (nueva o una que ya existe) queda como la del sitio, para su panel.
        if (request.Database is { } chosen) change = change with { Site = sites.SetDatabase(change.Site.Name, chosen) };
        var host = change.Site.Host;
        Main.NotifyConfigChanged();

        // Si se cancela el permiso de Windows, el sitio ya está creado: se dice tal cual y se sigue
        // (se sirve, aunque el nombre no resuelva hasta que entre en el hosts).
        var hostsOk = await Main.SyncHostsAsync(showError: false);
        if (!hostsOk)
            await Main.InfoAsync($"{host} está creado, pero falta en el hosts",
                $"{Main.HostsError}\n\nEl sitio queda registrado y se sirve, pero {host} no resolverá en el navegador hasta que esté en el hosts. "
                + $"Para añadirlo: Sitios › {host} › «Añadir al hosts» (Windows volverá a pedir permiso), o pasa al DNS local en Ajustes › Nombres .test.",
                Tone.Warn);

        if (Main.Stack.IsRunning)
        {
            if (!await Main.ApplyAsync()) return;
        }
        else
        {
            await Main.StartStackAsync();
            if (!Main.Stack.IsRunning) return;
        }

        if (request.CreateDatabase && request.Database is { } database && Main.Stack.Status?.Db is not null)
        {
            var db = new StackPlanner(paths, ProcessRunner.Default).ResolveDb(RoostConfig.Load(paths.ConfigFile), new DbInstaller(paths).ListInstalled());
            if (db is not null)
            {
                var ini = new StackPlanner(paths, ProcessRunner.Default).DbIniPath(db);
                await Task.Run(() => DbAdmin.Create(db, ini, database, ProcessRunner.Default));
            }
        }

        Main.NotifyConfigChanged();
        Pick(_all.FirstOrDefault(r => r.Site.Name == change.Site.Name));
        if (!hostsOk) return;   // sin el hosts, el navegador no lo encontraría

        // Confiar en la CA antes de abrir el navegador: así la primera visita ya sale sin aviso.
        await Main.OfferTrustAsync();
        var config = RoostConfig.Load(paths.ConfigFile);
        Shell.OpenUrl(StackPlanner.SiteUrl(change.Site, config.HttpsPort));
    });
}
