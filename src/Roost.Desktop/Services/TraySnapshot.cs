using Roost.Core;
using Roost.Core.Audit;
using Roost.Core.Config;
using Roost.Core.Install;
using Roost.Core.Models;
using Roost.Core.Platform;
using Roost.Core.Scanning;
using Roost.Core.Sites;
using Roost.Core.Stack;
using Roost.Daemon;
using Roost.Desktop.ViewModels;

namespace Roost.Desktop.Services;

/// <summary>Un PHP instalado tal como sale en el submenú de la bandeja: la versión exacta (J4).</summary>
/// <param name="Spec">Lo que se guarda al elegirlo: la rama si es el mejor de su rama (así entran solas las versiones nuevas) o la instalación.</param>
public sealed record TrayPhp(string Name, string Label, string Spec, bool Eol);

/// <summary>
/// Lo que la bandeja necesita de la ventana, leído en el hilo de la UI: el estado del stack, la
/// auditoría y si hay una Roost nueva. Lo demás (disco) lo lee <see cref="TraySnapshot.Read"/> fuera.
/// </summary>
public sealed record TrayUi(
    StatusResponse? Status,
    bool Busy,
    string? BusyText,
    string ToggleLabel,
    string AuditSummary,
    string? NewVersion);

/// <summary>
/// Lo que enseña la bandeja, leído de una vez y <b>fuera del hilo de la UI</b> (A16): roost.toml,
/// las instalaciones de PHP y de bases de datos, los sitios y la auditoría por versión. Su firma
/// dice si hay que rehacer el menú.
/// </summary>
public sealed record TraySnapshot(
    string Headline,
    IReadOnlyList<string> Sites,
    IReadOnlyList<TrayPhp> Php,
    string? GlobalPhp,
    IReadOnlyList<(DbEngine Engine, string Branch, string Label)> Databases,
    string? GlobalDb,
    string WebServer,
    bool ApacheInstalled,
    string WebUrl,
    TrayUi Ui)
{
    public bool Running => Ui.Status?.Running == true;

    /// <summary>«nginx» o «Apache», el servidor web de roost.toml.</summary>
    public string WebLabel => WebServer == RoostConfig.WebApache ? "Apache" : "nginx";

    /// <summary>La versión del PHP global («8.4.27»), para el título del submenú.</summary>
    public string? GlobalPhpLabel => Php.FirstOrDefault(p => p.Name == GlobalPhp)?.Label.Replace(" (EOL)", "");

    public string Signature =>
        string.Join("|", Headline, string.Join(",", Sites), string.Join(",", Php), GlobalPhp,
            string.Join(",", Databases.Select(d => d.Label)), GlobalDb, WebServer, ApacheInstalled, WebUrl, Running,
            Ui.ToggleLabel, Ui.Busy, Ui.AuditSummary, Ui.NewVersion);

    /// <summary>Lo de la ventana: se llama en el hilo de la UI.</summary>
    public static TrayUi Capture(MainViewModel main) => new(
        main.Stack.Status, main.Stack.Busy, main.Stack.BusyText, main.ToggleLabel, main.Audit.Summary,
        main.Updates.HasUpdate ? main.Updates.LatestVersion : null);

    /// <summary>
    /// Lee el disco (se llama con <c>Task.Run</c>). <paramref name="isEol"/> dice si una versión de
    /// PHP está fuera de soporte (la auditoría ya cargada; no va a la red).
    /// </summary>
    public static TraySnapshot Read(RoostPaths paths, TrayUi ui, Func<ComponentVersion, bool> isEol)
    {
        RoostConfig config;
        try { config = RoostConfig.Load(paths.ConfigFile); }
        catch (RoostException) { config = new RoostConfig(); }

        var planner = new StackPlanner(paths, ProcessRunner.Default);
        var installedPhp = new PhpInstaller(paths).ListInstalled();
        string? globalPhp = null;
        try { globalPhp = planner.ResolvePhp(config, installedPhp).Name; } catch (RoostException) { }

        // Cada instalación, de la más nueva a la más vieja; la rama si es la mejor de la suya.
        var php = installedPhp
            .Where(p => p.Version is not null)
            .OrderByDescending(p => p.Version!.Value)
            .ThenBy(p => p.Name, StringComparer.Ordinal)
            .Select(p =>
            {
                var branch = HomeViewModel.Branch(p.Name);
                string best;
                try { best = planner.ResolvePhp(new RoostConfig { PhpVersion = branch }, installedPhp).Name; }
                catch (RoostException) { best = p.Name; }
                var variant = string.Join(" · ", new[] { p.ThreadSafe ? "TS" : null, p.Name.EndsWith("-x86", StringComparison.OrdinalIgnoreCase) ? "x86" : null }.Where(x => x is not null));
                var eol = isEol(p.Version!.Value);
                var label = p.Version!.Value + (variant.Length > 0 ? $" · {variant}" : "") + (eol ? " (EOL)" : "");
                return new TrayPhp(p.Name, label, best == p.Name ? branch : p.Name, eol);
            })
            .ToList();

        var installedDb = new DbInstaller(paths).ListInstalled();
        string? globalDb = null;
        try
        {
            if (planner.ResolveDb(config, installedDb) is { } db) globalDb = $"{db.Engine.DisplayName()} {db.Branch}";
        }
        catch (RoostException) { }
        var databases = installedDb
            .Where(d => d.Branch is not null)
            .Select(d => (d.Engine, Branch: d.Branch!, Label: $"{d.Engine.DisplayName()} {d.Branch}"))
            .Distinct()
            .ToList();

        IReadOnlyList<string> sites = [];
        try { sites = new SiteRegistry(paths).Load().Select(s => StackPlanner.SiteUrl(s, config.HttpsPort).TrimEnd('/')).ToList(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or RoostException) { }

        var apache = false;
        try { apache = new ApacheInstaller(paths).Find(ApacheCatalog.Windows) is not null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }

        var webUrl = ui.Status?.Url ?? (config.HttpPort == 80 ? "http://localhost/" : $"http://localhost:{config.HttpPort}/");
        var phpLabel = php.FirstOrDefault(p => p.Name == globalPhp)?.Label.Replace(" (EOL)", "");
        var headline = ui.Busy ? ui.BusyText ?? "..."
            : ui.Status?.Running == true ? $"En marcha · PHP {phpLabel ?? "?"} · {globalDb ?? "sin base de datos"}"
            : "Parado";
        return new TraySnapshot(headline, sites, php, globalPhp, databases, globalDb, config.WebServer, apache, webUrl, ui);
    }

    /// <summary>
    /// El nombre del servicio del servidor web para <c>/restart</c> («nginx» o «apache»): el que
    /// cuenta el daemon si está en marcha y, si no, el de roost.toml.
    /// </summary>
    public string WebService => Ui.Status?.Web ?? (WebServer == RoostConfig.WebApache ? "apache" : "nginx");
}
