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
using Roost.Daemon;
using Roost.Desktop.Controls;
using Roost.Desktop.Services;

namespace Roost.Desktop.ViewModels;

/// <summary>Una fila de la tabla Servicios de Inicio: un servicio o un pool de php-cgi entero.</summary>
public sealed record ServiceRow(
    string Name,
    string? Tag,
    Tone TagTone,
    string Detail,
    string Version,
    string Listen,
    string State,
    Tone StateTone,
    int Restarts,
    string? RestartKey)
{
    public bool HasTag => Tag is not null;
    public bool CanRestart => RestartKey is not null;
}

/// <summary>Un sitio en la lista de Inicio.</summary>
public sealed record SiteLine(string Url, string Php, Tone PhpTone, Tone DotTone);

/// <summary>Un hallazgo de la auditoría en la lista de Inicio.</summary>
public sealed record FindingLine(string Name, string Source, string Chip, Tone Tone);

/// <summary>Inicio: los accesos de siempre, los servicios y un resumen de sitios y auditoría.</summary>
public sealed partial class HomeViewModel : PageViewModel
{
    public HomeViewModel(MainViewModel main) : base(main)
    {
        main.Audit.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(AuditState.Findings) or nameof(AuditState.Scanning)) BuildAudit();
        };
    }

    public override string Title => "Inicio";

    [ObservableProperty] public partial IReadOnlyList<ServiceRow> Services { get; private set; } = [];
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSites))]
    public partial IReadOnlyList<SiteLine> SiteLines { get; private set; } = [];
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFindings))]
    public partial IReadOnlyList<FindingLine> Findings { get; private set; } = [];
    [ObservableProperty] public partial string WebUrl { get; private set; } = "http://localhost/";
    [ObservableProperty] public partial string TerminalHint { get; private set; } = "";
    [ObservableProperty] public partial string DbHint { get; private set; } = "";
    [ObservableProperty] public partial string AuditHint { get; private set; } = "";
    [ObservableProperty] public partial Tone AuditTone { get; private set; } = Tone.Accent;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasConfigError))]
    public partial string? ConfigError { get; private set; }

    public string WwwPath => Main.App.Paths.Www;

    public bool HasSites => SiteLines.Count > 0;

    public bool HasConfigError => ConfigError is not null;

    public bool HasFindings => Findings.Count > 0;

    public string AuditCardHint => Main.Audit.Dataset.Generated == default ? ""
        : $"dataset del {Main.Audit.Dataset.Generated.LocalDateTime:d-MMM-yyyy}";

    public override Task ShownAsync()
    {
        Build();
        return Task.CompletedTask;
    }

    public override void StackChanged() => Build();

    private void Build()
    {
        var paths = Main.App.Paths;
        var status = Main.Stack.Status;
        RoostConfig config;
        try
        {
            config = RoostConfig.Load(paths.ConfigFile);
            ConfigError = null;
        }
        catch (RoostException ex)
        {
            config = new RoostConfig();
            ConfigError = ex.Message;
        }

        var installedPhp = new PhpInstaller(paths).ListInstalled();
        var installedDb = new DbInstaller(paths).ListInstalled();
        var sites = SafeLoadSites(paths);
        var planner = new StackPlanner(paths, ProcessRunner.Default);
        InstalledPhp? globalPhp = null;
        InstalledDb? globalDb = null;
        try { globalPhp = planner.ResolvePhp(config, installedPhp); } catch (RoostException) { }
        try { globalDb = planner.ResolveDb(config, installedDb); } catch (RoostException) { }

        var running = status?.Running == true;
        WebUrl = status?.Url ?? (config.HttpPort == 80 ? "http://localhost/" : $"http://localhost:{config.HttpPort}/");
        TerminalHint = globalPhp is null ? "PHP no instalado"
            : new Roost.Core.Shims.ShimInstaller(paths).IsInstalled ? "php según la carpeta"
            : $"Con PHP {globalPhp.Version?.Major}.{globalPhp.Version?.Minor} en el PATH";
        DbHint = running && status!.Db is { } db ? $"{Short(db)} · :{status.DbPort}"
            : globalDb is not null ? $"{Short(globalDb.Label)} · :{config.DbPort}"
            : "Sin base de datos";

        // Sitios: del registro, con el PHP que les toca (EOL en rojo).
        var lines = new List<SiteLine>();
        foreach (var site in sites)
        {
            var php = status?.Sites.FirstOrDefault(s => s.Name == site.Name)?.Php
                      ?? (globalPhp is null ? null : SafeSitePhp(planner, site, globalPhp, installedPhp));
            var (label, tone) = PhpChip(php);
            lines.Add(new SiteLine(StackPlanner.SiteUrl(site, config.HttpsPort).TrimEnd('/'), label, tone, running ? Tone.Ok : Tone.Off));
        }
        SiteLines = lines;

        Services = running ? RunningRows(status!, config, sites) : PlannedRows(config, globalPhp, globalDb, sites, installedPhp, planner);
        Subtitle = running
            ? $"Todo en marcha{(status!.Since is { } since ? $" desde las {since.LocalDateTime:HH:mm}" : "")} · "
              + $"{Count(sites.Count, "sitio", "sitios")}, PHP {Branch(status.Php)} global, {(status.Db is null ? "sin base de datos" : Short(status.Db))}"
            : "Parado. Iniciar todo arranca la base de datos, los PHP en uso y nginx.";
        BuildAudit();
    }

    private IReadOnlyList<ServiceRow> RunningRows(StatusResponse status, RoostConfig config, IReadOnlyList<Site> sites)
    {
        var rows = new List<ServiceRow>();
        var byGroup = status.Services.GroupBy(s => s.Name.StartsWith("php-", StringComparison.Ordinal) ? s.Name[..s.Name.LastIndexOf('-')] : s.Name);
        foreach (var group in byGroup)
        {
            var list = group.ToList();
            var (state, tone) = GroupState(list);
            var restarts = list.Sum(s => s.Restarts);
            if (group.Key.StartsWith("php-", StringComparison.Ordinal))
            {
                var branch = group.Key["php-".Length..];
                var install = new[] { status.Php }.Concat(status.Sites.Select(s => s.Php)).FirstOrDefault(p => Branch(p) == branch);
                var users = status.Sites.Where(s => Branch(s.Php) == branch).Select(s => s.Name + ".test").ToList();
                if (Branch(status.Php) == branch) users.Add("localhost");
                var ports = list.Where(s => s.Port is not null).Select(s => s.Port!.Value).Order().ToList();
                var (tag, tagTone) = Branch(status.Php) == branch ? ("global", Tone.Accent) : EolTag(install);
                rows.Add(new ServiceRow(
                    $"PHP {branch}", tag, tagTone,
                    $"{Count(list.Count, "proceso php-cgi", "procesos php-cgi")} · lo usa{(users.Count == 1 ? "" : "n")} {Join(users)}",
                    PhpLabel(install), ports.Count == 0 ? "-" : ports.Count == 1 ? $"{ports[0]}" : $"{ports[0]}–{ports[^1]}",
                    state, tone, restarts, group.Key));
            }
            else if (group.Key == "nginx")
            {
                var listen = sites.Count > 0 ? $":{config.HttpPort} · :{config.HttpsPort}" : $":{config.HttpPort}";
                rows.Add(new ServiceRow("nginx", null, Tone.Neutral,
                    sites.Count == 0 ? "localhost" : $"localhost y {Count(sites.Count, "sitio .test", "sitios .test")}",
                    status.Nginx ?? "", listen, state, tone, restarts, "nginx"));
            }
            else if (group.Key == "dns")
            {
                rows.Add(new ServiceRow("DNS local", null, Tone.Neutral, "Responde *.test con 127.0.0.1 (dentro de roostd)", "",
                    $"{Roost.Core.Dns.LocalDns.Address}:{Roost.Core.Dns.LocalDns.Port}", state, tone, restarts, "dns"));
            }
            else if (!group.Key.StartsWith("mariadb-", StringComparison.Ordinal) && !group.Key.StartsWith("mysql-", StringComparison.Ordinal))
            {
                // Un servicio de una extensión (un Redis, un Mailpit): con su nombre y su puerto.
                var port = list.FirstOrDefault(s => s.Port is not null)?.Port;
                rows.Add(new ServiceRow(group.Key, "extensión", Tone.Neutral, "Lo añade una extensión", "",
                    port is null ? "-" : $"127.0.0.1:{port}", state, tone, restarts, group.Key));
            }
            else
            {
                var branch = group.Key[(group.Key.IndexOf('-') + 1)..];
                var engine = group.Key[..group.Key.IndexOf('-')];
                rows.Add(new ServiceRow(engine == "mysql" ? "MySQL" : "MariaDB", null, Tone.Neutral,
                    $"Base de datos global · datos en data\\{engine}\\{branch}",
                    status.Db?.Split(' ').LastOrDefault() ?? branch, $"127.0.0.1:{status.DbPort}", state, tone, restarts, group.Key));
            }
        }

        // Mismo orden que el arranque: base de datos, extensiones, PHP, nginx.
        return rows.OrderBy(r => r.Name == "nginx" ? 3 : r.Name.StartsWith("PHP", StringComparison.Ordinal) ? 2 : r.Tag == "extensión" ? 1 : r.Name == "DNS local" ? -1 : 0).ToList();
    }

    private IReadOnlyList<ServiceRow> PlannedRows(
        RoostConfig config, InstalledPhp? php, InstalledDb? db, IReadOnlyList<Site> sites,
        IReadOnlyList<InstalledPhp> installedPhp, StackPlanner planner)
    {
        var rows = new List<ServiceRow>();
        if (db is not null)
        {
            rows.Add(new ServiceRow(db.Engine.DisplayName(), null, Tone.Neutral,
                $"Base de datos global · datos en data\\{db.Engine.Key()}\\{db.Branch}",
                db.Name, $"127.0.0.1:{config.DbPort}", "Parado", Tone.Off, 0, null));
        }

        if (php is not null)
        {
            var pools = new Dictionary<string, (InstalledPhp Php, List<string> Users)>();
            pools[Branch(php.Name)] = (php, []);
            foreach (var site in sites)
            {
                var sitePhp = SafeSitePhpInstall(planner, site, php, installedPhp);
                if (sitePhp is null) continue;
                var branch = Branch(sitePhp.Name);
                if (!pools.TryGetValue(branch, out var pool)) pools[branch] = pool = (sitePhp, []);
                pool.Users.Add(site.Host);
            }
            pools[Branch(php.Name)].Users.Add("localhost");

            foreach (var (branch, pool) in pools.OrderByDescending(p => p.Key == Branch(php.Name)))
            {
                var (tag, tagTone) = pool.Php == php ? ("global", Tone.Accent) : EolTag(pool.Php.Name);
                rows.Add(new ServiceRow($"PHP {branch}", tag, tagTone,
                    $"{Count(config.PhpPool, "proceso php-cgi", "procesos php-cgi")} · lo usa{(pool.Users.Count == 1 ? "" : "n")} {Join(pool.Users)}",
                    PhpLabel(pool.Php.Name), "-", "Parado", Tone.Off, 0, null));
            }
        }

        var nginx = new NginxInstaller(Main.App.Paths).Find(NginxCatalog.Windows);
        rows.Add(new ServiceRow("nginx", null, Tone.Neutral,
            nginx is null ? "Se instala solo al iniciar por primera vez" : sites.Count == 0 ? "localhost" : $"localhost y {Count(sites.Count, "sitio .test", "sitios .test")}",
            nginx?.Version ?? NginxCatalog.Windows.Version,
            sites.Count > 0 ? $":{config.HttpPort} · :{config.HttpsPort}" : $":{config.HttpPort}", "Parado", Tone.Off, 0, null));
        return rows;
    }

    private void BuildAudit()
    {
        var audit = Main.Audit;
        AuditHint = audit.Summary;
        AuditTone = audit.LastScan is null ? Tone.Accent : audit.RedCount > 0 ? Tone.Bad : audit.AmberCount > 0 ? Tone.Warn : Tone.Ok;
        Findings = audit.Findings
            .OrderByDescending(f => f.Color)
            .Take(3)
            .Select(f => new FindingLine(
                $"{Kind(f.Component.Kind)} {f.Component.Version}", $"({f.Component.Source})", AuditState.ChipText(f), AuditState.ToneOf(f.Color)))
            .ToList();
        OnPropertyChanged(nameof(AuditCardHint));
    }

    private (string Label, Tone Tone) PhpChip(string? phpName)
    {
        if (phpName is null) return ("sin PHP", Tone.Warn);
        var (tag, tone) = EolTag(phpName);
        return tag is null ? ($"PHP {Branch(phpName)}", Tone.Neutral) : ($"PHP {Branch(phpName)} · EOL", tone);
    }

    private (string? Tag, Tone Tone) EolTag(string? phpName)
    {
        if (phpName is null || !ComponentVersion.TryParse(phpName, out var version)) return (null, Tone.Neutral);
        return Main.Audit.Evaluate(ComponentKind.Php, version).Verdict == AuditVerdict.Eol ? ("EOL", Tone.Bad) : (null, Tone.Neutral);
    }

    private static (string State, Tone Tone) GroupState(IReadOnlyList<ServiceDto> services)
    {
        if (services.Any(s => s.State == "Failed")) return ("Fallando", Tone.Bad);
        var running = services.Count(s => s.State == "Running");
        if (running == services.Count) return (services.Count > 1 ? $"{running} de {services.Count} en marcha" : "En marcha", Tone.Ok);
        if (services.Any(s => s.State == "Restarting")) return (services.Count > 1 ? $"{running} de {services.Count} en marcha" : "Reiniciando", Tone.Warn);
        return ("Parado", Tone.Off);
    }

    private static IReadOnlyList<Site> SafeLoadSites(Roost.Core.Platform.RoostPaths paths)
    {
        try
        {
            return new SiteRegistry(paths).Load();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or RoostException)
        {
            return [];
        }
    }

    private static string? SafeSitePhp(StackPlanner planner, Site site, InstalledPhp global, IReadOnlyList<InstalledPhp> installed) =>
        SafeSitePhpInstall(planner, site, global, installed)?.Name;

    private static InstalledPhp? SafeSitePhpInstall(StackPlanner planner, Site site, InstalledPhp global, IReadOnlyList<InstalledPhp> installed)
    {
        try
        {
            return planner.ResolveSitePhp(site, global, installed);
        }
        catch (RoostException)
        {
            return null;
        }
    }

    /// <summary>"8.4.27-nts-x64" → "8.4".</summary>
    public static string Branch(string? phpName) =>
        phpName is not null && ComponentVersion.TryParse(phpName, out var v) ? $"{v.Major}.{v.Minor}" : phpName ?? "";

    /// <summary>"8.4.27-nts-x64" → "8.4.27 NTS x64".</summary>
    public static string PhpLabel(string? phpName)
    {
        if (phpName is null) return "";
        var parts = phpName.Split('-');
        return parts.Length == 3 ? $"{parts[0]} {parts[1].ToUpperInvariant()} {parts[2]}" : phpName;
    }

    /// <summary>"MariaDB 11.4.13" → "MariaDB 11.4".</summary>
    private static string Short(string dbLabel)
    {
        var space = dbLabel.LastIndexOf(' ');
        return space > 0 && ComponentVersion.TryParse(dbLabel[(space + 1)..], out var v) ? $"{dbLabel[..space]} {v.Major}.{v.Minor}" : dbLabel;
    }

    private static string Kind(ComponentKind kind) => kind switch
    {
        ComponentKind.Php => "PHP",
        ComponentKind.MySql => "MySQL",
        ComponentKind.MariaDb => "MariaDB",
        _ => kind.ToString(),
    };

    private static string Count(int n, string one, string many) => $"{n} {(n == 1 ? one : many)}";

    private static string Join(IReadOnlyList<string> items) =>
        items.Count <= 1 ? string.Concat(items) : string.Join(", ", items.Take(items.Count - 1)) + " y " + items[^1];

    // Acciones

    [RelayCommand]
    private Task OpenWebAsync() => Main.RunAsync("No se pudo abrir el navegador", () => { Shell.OpenUrl(WebUrl); return Task.CompletedTask; });

    [RelayCommand]
    private Task OpenSiteAsync(string url) => Main.RunAsync("No se pudo abrir el navegador", () => { Shell.OpenUrl(url); return Task.CompletedTask; });

    [RelayCommand]
    private Task OpenTerminalAsync() => Main.RunAsync("No se pudo abrir la terminal", () => { Shell.OpenTerminal(Main.App.Paths); return Task.CompletedTask; });

    [RelayCommand]
    private Task OpenWwwAsync() => Main.RunAsync("No se pudo abrir la carpeta", () => { Shell.OpenFolder(WwwPath); return Task.CompletedTask; });

    [RelayCommand]
    private Task GoSitesAsync() => Main.NavigateToAsync(Main.Sites);

    [RelayCommand]
    private async Task NewSiteAsync()
    {
        await Main.NavigateToAsync(Main.Sites);
        await Main.Sites.NewSiteCommand.ExecuteAsync(null);
    }

    [RelayCommand]
    private Task GoDatabasesAsync() => Main.NavigateToAsync(Main.Databases);

    [RelayCommand]
    private Task GoAuditAsync() => Main.NavigateToAsync(Main.AuditPage);

    [RelayCommand]
    private Task GoLogsAsync() => Main.NavigateToAsync(Main.Logs);

    [RelayCommand]
    private Task RestartAsync(ServiceRow row) => Main.RunAsync($"No se pudo reiniciar {row.Name}", async () =>
    {
        if (row.RestartKey is null) return;
        var response = await Main.App.Daemon.RestartAsync(row.RestartKey);
        if (!response.Ok) throw new RoostException(response.Error ?? "El daemon no ha dicho por qué.");
        await Main.Stack.RefreshAsync();
    });
}
