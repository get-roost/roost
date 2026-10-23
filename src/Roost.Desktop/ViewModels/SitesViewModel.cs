using System.Security.Cryptography.X509Certificates;
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

/// <summary>
/// Sitios: cada proyecto en https://&lt;nombre&gt;.test con su certificado, su PHP (el global o
/// uno propio) y su línea en el hosts. Alta con el diálogo Nuevo sitio; la carpeta del
/// proyecto nunca se toca al quitarlo.
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
    [ObservableProperty] public partial string DbText { get; private set; } = "";
    [ObservableProperty] public partial string ConfPath { get; private set; } = "";

    public bool HasSelection => Selected is not null;
    public bool HasSites => _all.Count > 0;
    public bool MissingFromHosts => Selected is { InHosts: false, ByDns: false };

    partial void OnSearchChanged(string value) => ApplySearch();
    partial void OnSelectedChanged(SiteRow? value)
    {
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(MissingFromHosts));
    }

    partial void OnSelectedPhpChanged(PhpOption? value)
    {
        if (_settingPhp || value is null || Selected is not { } row || value.Spec == row.Site.Php) return;
        _ = SetPhpAsync(row, value.Spec);
    }

    public override Task ShownAsync()
    {
        Build();
        return Task.CompletedTask;
    }

    public override void StackChanged() => Build();

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
        if (row is null) return;

        var paths = Main.App.Paths;
        if (config is null)
        {
            try { config = RoostConfig.Load(paths.ConfigFile); } catch (RoostException) { config = new RoostConfig(); }
        }
        var site = row.Site;
        var http = config.HttpPort == 80 ? $"http://{site.Host}" : $"http://{site.Host}:{config.HttpPort}";
        Urls = StackPlanner.SiteUrl(site, config.HttpsPort).TrimEnd('/') + Environment.NewLine + http;
        RootText = site.Root is null ? "(la carpeta)" : site.Root.Replace('\\', '/').TrimEnd('/') + "/";
        CertDetail = $"Roost Local CA · {site.Host}, *.{site.Host}";
        ConfPath = new SiteManager(paths).ConfFile(site);
        var db = Main.Stack.Status?.Db;
        DbText = db is null ? "La global no está en marcha" : $"La global: {db} en 127.0.0.1:{Main.Stack.Status!.DbPort}";

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

    // Acciones

    [RelayCommand]
    private void Choose(SiteRow row) => Pick(row);

    private Task SetPhpAsync(SiteRow row, string? spec) => Main.RunAsync($"No se pudo cambiar el PHP de {row.Host}", async () =>
    {
        new SiteRegistry(Main.App.Paths).Upsert(row.Site with { Php = spec });
        await Main.ApplyAsync();
        Main.NotifyConfigChanged();
    });

    [RelayCommand]
    private Task OpenAsync() => Main.RunAsync("No se pudo abrir el navegador", () =>
    {
        if (Selected is { } row) Shell.OpenUrl(Urls.Split(Environment.NewLine)[0] + "/");
        return Task.CompletedTask;
    });

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
        await Main.ApplyAsync();
        Main.NotifyConfigChanged();
        Main.Flash($"Certificado de {row.Host} renovado");
    });

    [RelayCommand]
    private Task SyncHostsAsync() => Main.RunAsync("No se pudo actualizar el hosts", Main.SyncHostsAsync);

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

        await Main.RunAsync($"No se pudo quitar {row.Host}", async () =>
        {
            new SiteManager(Main.App.Paths).Remove(row.Site.Name);
            Main.NotifyConfigChanged();
            await Main.SyncHostsAsync();
            await Main.ApplyAsync();
        });
        Main.NotifyConfigChanged();
    }

    [RelayCommand]
    private async Task NewSiteAsync()
    {
        var dialog = new NewSiteDialogViewModel(Main);
        if (await Main.ShowDialogAsync(dialog) is not NewSiteRequest request) return;
        await CreateAsync(request);
    }

    /// <summary>Crear y abrir: registra, pasa al hosts, (re)arranca, crea la base de datos y abre el navegador.</summary>
    private Task CreateAsync(NewSiteRequest request) => Main.RunAsync($"No se pudo crear {request.Name}.test", async () =>
    {
        var paths = Main.App.Paths;
        var change = new SiteManager(paths).Add(request.Name, request.Folder, php: request.Php ?? "global");
        Main.NotifyConfigChanged();
        await Main.SyncHostsAsync();

        if (Main.Stack.IsRunning) await Main.ApplyAsync();
        else await Main.StartStackAsync();
        if (!Main.Stack.IsRunning) return;

        if (request.Database is { } database && Main.Stack.Status?.Db is not null)
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
        var config = RoostConfig.Load(paths.ConfigFile);
        Shell.OpenUrl(StackPlanner.SiteUrl(change.Site, config.HttpsPort));
        await Main.OfferTrustAsync();
    });
}
