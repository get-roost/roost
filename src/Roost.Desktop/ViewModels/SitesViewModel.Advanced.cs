using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Roost.Core;
using Roost.Core.Config;
using Roost.Core.Scanning;
using Roost.Core.Sites;
using Roost.Core.Stack;
using Roost.Desktop.Controls;
using Roost.Desktop.Services;

namespace Roost.Desktop.ViewModels;

/// <summary>Una carpeta aparcada de la lista de Sitios: la ruta, cuántos sitios salen de ella y si está.</summary>
public sealed record ParkedRow(string Folder, int Count, bool Exists)
{
    public string Detail => !Exists ? "no está ahora: sus sitios se quedan" : Count == 1 ? "1 sitio" : $"{Count} sitios";

    public Tone Tone => Exists ? Tone.Neutral : Tone.Warn;
}

/// <summary>
/// Sitios avanzados (oleada 6): «Editar…» (renombrar y mover, C2), «Alias…» (C7), «Registros» (C5),
/// el <c>.roost.toml</c> del proyecto (C11) y las carpetas aparcadas (C3), con su vigilante mientras
/// la app está abierta. Con el hosts, lo que falta en él se junta en un aviso con un solo botón (un
/// solo permiso de administrador); con el DNS local, no hace falta nada.
/// </summary>
public sealed partial class SitesViewModel
{
    private ParkWatcher? _parkWatcher;
    private DispatcherTimer? _parkRefresh;
    private bool _parkBusy;
    private bool _parkAgain;
    private readonly HashSet<string> _skipsSeen = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Los alias y subdominios del sitio elegido, uno por línea; vacío si no tiene.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAliases))]
    public partial string AliasesText { get; private set; } = "";

    public bool HasAliases => AliasesText.Length > 0;

    /// <summary>Lo que se sirve sin alias: el comodín del nombre del sitio.</summary>
    [ObservableProperty] public partial string AliasesHint { get; private set; } = "";

    /// <summary>Lo que dice el <c>.roost.toml</c> del sitio elegido; vacío si no tiene.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasProjectFile))]
    public partial string ProjectFileText { get; private set; } = "";

    public bool HasProjectFile => ProjectFileText.Length > 0;

    /// <summary>Si el sitio elegido sale de una carpeta aparcada, cuál.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsParkedSite))]
    public partial string ParkedText { get; private set; } = "";

    public bool IsParkedSite => ParkedText.Length > 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasParked))]
    public partial IReadOnlyList<ParkedRow> ParkedRows { get; private set; } = [];

    public bool HasParked => ParkedRows.Count > 0;

    /// <summary>Los nombres que faltan en el hosts (todos los sitios, alias incluidos), en un aviso; vacío si ninguno o con el DNS local.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasHostsPending))]
    public partial string HostsPending { get; private set; } = "";

    public bool HasHostsPending => HostsPending.Length > 0;

    /// <summary>Lo que no se ha podido aplicar de un <c>.roost.toml</c> o de una carpeta aparcada.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSourcesWarning))]
    public partial string SourcesWarning { get; private set; } = "";

    public bool HasSourcesWarning => SourcesWarning.Length > 0;

    /// <summary>Lo del panel que va con el sitio elegido (lo llama <see cref="Pick"/>).</summary>
    private void PickAdvanced(Site site)
    {
        AliasesText = site.Aliases is { Count: > 0 } aliases ? string.Join(Environment.NewLine, aliases) : "";
        AliasesHint = HasAliases ? $"Y cualquier *.{site.Host}." : $"Ninguno; ya responde por cualquier *.{site.Host}.";
        ParkedText = site.ParkedIn is { } parked ? $"Sale de la carpeta aparcada {parked}" : "";
        ProjectFileText = "";
        try
        {
            if (Directory.Exists(site.Folder) && ProjectFile.Read(site.Folder) is { } project)
            {
                var keys = new List<string>();
                if (project.Php is { } php) keys.Add($"php {php}");
                if (project.Root is { } root) keys.Add($"raíz {root}");
                if (project.Db is { } db) keys.Add($"base de datos {db}");
                if (project.Aliases is { } list) keys.Add(list.Count == 1 ? "1 alias" : $"{list.Count} alias");
                if (project.Name is { } name) keys.Add($"nombre {name}");
                ProjectFileText = keys.Count == 0 ? "Tiene .roost.toml, sin nada para Roost." : "Tiene .roost.toml: " + string.Join(", ", keys) + ".";
            }
        }
        catch (RoostException)
        {
            ProjectFileText = "Su .roost.toml no se puede leer: mira el aviso de arriba o ábrelo en el editor.";
        }
    }

    /// <summary>Las carpetas aparcadas y el aviso del hosts (lo llama <see cref="Build"/>).</summary>
    private void BuildAdvanced(IReadOnlyList<Site> sites, RoostConfig config, IReadOnlySet<string> inHosts)
    {
        IReadOnlyList<string> parked;
        try { parked = new ParkedFolders(Main.App.Paths).Load(); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { parked = []; }
        ParkedRows = parked.Select(folder => new ParkedRow(folder,
            sites.Count(s => s.ParkedIn is { } p && Roost.Core.Platform.RoostPaths.SameFolder(p, folder)),
            Directory.Exists(folder))).ToList();

        var missing = config.UsesLocalDns ? [] : SiteManager.HostNames(sites).Where(n => !inHosts.Contains(n)).ToList();
        HostsPending = missing.Count switch
        {
            0 => "",
            1 => $"Falta en el hosts {missing[0]}: no resolverá hasta que se añada.",
            <= 6 => $"Faltan en el hosts {missing.Count} nombres: {string.Join(", ", missing)}.",
            _ => $"Faltan en el hosts {missing.Count} nombres: {string.Join(", ", missing.Take(5))} y {missing.Count - 5} más.",
        };
    }

    // C2: Editar… (renombrar y mover)

    [RelayCommand]
    private async Task EditAsync()
    {
        if (Selected is not { } row) return;
        IReadOnlyList<Site> all;
        try { all = new SiteRegistry(Main.App.Paths).Load(); } catch (RoostException ex) { await Main.InfoAsync("No se pueden leer los sitios", ex.Message, Tone.Bad); return; }
        if (await Main.ShowDialogAsync(new EditSiteDialogViewModel(Main, row.Site, all, SiteDbRunning)) is not EditSiteRequest request) return;
        await ApplyEditAsync(row, request);
    }

    /// <summary>
    /// Lo de «Editar…»: primero la copia de la base de datos (si falla, no se ha tocado el sitio), luego
    /// el nombre, luego la carpeta; después el hosts (un permiso, si cambia el nombre) y el stack.
    /// </summary>
    internal Task ApplyEditAsync(SiteRow row, EditSiteRequest request) => Main.RunAsync($"No se pudo cambiar {row.Host}", async () =>
    {
        var paths = Main.App.Paths;
        var sites = new SiteManager(paths);
        var name = row.Site.Name;
        var notes = new List<string>();

        SiteDatabaseCopied? copied = null;
        if (request.CopyDatabase && request.NewName is { } target)
        {
            var (db, ini) = RequireSiteDb();
            var from = SiteManager.DatabaseOf(row.Site);
            var to = DbAdmin.NameFor(target);
            Main.Flash($"Copiando {from} a {to}…");
            var runner = Runner;
            var admin = DbRunner;
            copied = await Task.Run(() => SiteDatabaseCopy.CopyAsync(paths, db, ini, from, to, runner, admin, null, CancellationToken.None));
            notes.Add($"Su base de datos se ha copiado a {to}; {from} se queda como estaba (bórrala en Bases de datos cuando lo compruebes).");
        }

        if (request.NewName is { } newName)
        {
            var renamed = sites.Rename(name, newName);
            name = renamed.Site.Name;
            if (copied is not null)
            {
                sites.SetDatabase(name, copied.To == DbAdmin.NameFor(name) ? null : copied.To);
                if (SiteDatabaseCopy.UpdateEnv(renamed.Site.Folder, copied.From, copied.To) is { } env)
                    notes.Add($"En {env}, DB_DATABASE pasa a {copied.To}.");
            }
            foreach (var (from, to) in renamed.AliasesMoved) notes.Add($"El subdominio {from} pasa a {to}.");
            if (renamed.ConfsKept.Count > 0)
                notes.Add($"El vhost de antes queda como {Path.GetFileName(renamed.ConfsKept[0])}, por si tenía cambios tuyos.");
        }

        if (request.NewFolder is { } folder)
        {
            var moved = sites.Move(name, folder, request.MoveFiles);
            if (moved.Old.Git is not null && moved.Site.Git is null)
                notes.Add("Ya no es el checkout de su repositorio: si la carpeta nueva tiene .git, adóptalo desde el bloque Git.");
        }

        ForgetDatabases();
        Main.NotifyConfigChanged();
        var hostsOk = request.NewName is null || await Main.SyncHostsAsync(showError: false);
        var applied = await Main.ApplyAsync();
        Pick(_all.FirstOrDefault(r => r.Site.Name == name));

        var host = $"{name}.test";
        if (!hostsOk)
            notes.Insert(0, $"{Main.HostsError} {host} no resolverá hasta que esté en el hosts: «Añadir al hosts» en el panel.");
        if (notes.Count > 0)
            await Main.InfoAsync(request.NewName is null ? $"{host} se sirve desde otra carpeta" : $"{row.Host} es ahora {host}", string.Join("\n\n", notes), hostsOk ? Tone.Ok : Tone.Warn);
        else if (applied)
            Main.Flash(request.NewName is null ? $"{host} se sirve desde {Selected?.Site.Folder}" : $"{row.Host} es ahora {host}");
    });

    // C7: Alias…

    [RelayCommand]
    private async Task AliasesAsync()
    {
        if (Selected is not { } row) return;
        IReadOnlyList<Site> all;
        RoostConfig config;
        try
        {
            all = new SiteRegistry(Main.App.Paths).Load();
            config = RoostConfig.Load(Main.App.Paths.ConfigFile);
        }
        catch (RoostException ex)
        {
            await Main.InfoAsync("No se pueden leer los sitios", ex.Message, Tone.Bad);
            return;
        }
        var wordpress = Directory.Exists(row.Site.Folder) && ProjectKind.Detect(row.Site.Folder).Type == ProjectType.WordPress;
        var dialog = new SiteAliasesDialogViewModel(all.FirstOrDefault(s => s.Name == row.Site.Name) ?? row.Site, all, config.UsesLocalDns, wordpress);
        if (await Main.ShowDialogAsync(dialog) is not IReadOnlyList<string> aliases) return;
        await ApplyAliasesAsync(row, aliases);
    }

    internal Task ApplyAliasesAsync(SiteRow row, IReadOnlyList<string> aliases) => Main.RunAsync($"No se pudieron cambiar los alias de {row.Host}", async () =>
    {
        new SiteManager(Main.App.Paths).SetAliases(row.Site.Name, aliases);
        Main.NotifyConfigChanged();
        // Un solo permiso para todos los nombres nuevos (con el DNS local, ninguno).
        var hostsOk = await Main.SyncHostsAsync(showError: false);
        var applied = await Main.ApplyAsync();
        if (!hostsOk)
            await Main.InfoAsync($"Los alias de {row.Host} están guardados, pero faltan en el hosts",
                $"{Main.HostsError}\n\nSe sirven, pero no resolverán hasta que estén en el hosts: «Añadir al hosts» (Windows volverá a pedir permiso), o pasa al DNS local en Ajustes › Nombres .test.",
                Tone.Warn);
        else if (applied)
            Main.Flash(aliases.Count == 0 ? $"{row.Host} ya no tiene alias" : $"{row.Host} responde también por {string.Join(", ", aliases)}");
    });

    // C5: los registros del sitio

    [RelayCommand]
    private async Task ViewLogsAsync()
    {
        if (Selected is not { } row) return;
        await Main.NavigateToAsync(Main.Logs);
        await Main.Logs.SelectSiteAsync(row.Site.Name);
    }

    // C11: el .roost.toml

    [RelayCommand]
    private Task SaveProjectFileAsync() => Main.RunAsync("No se pudo guardar el .roost.toml", () =>
    {
        if (Selected is not { } row) return Task.CompletedTask;
        var file = new SiteManager(Main.App.Paths).SaveProjectFile(row.Site.Name);
        PickAdvanced(new SiteRegistry(Main.App.Paths).Find(row.Site.Name) ?? row.Site);
        Main.Flash($"Guardado {file}");
        return Task.CompletedTask;
    });

    [RelayCommand]
    private Task OpenProjectFileAsync() => Main.RunAsync("No se pudo abrir el .roost.toml", () =>
    {
        if (Selected is { } row && ProjectFile.ExistsIn(row.Site.Folder)) Shell.OpenFile(ProjectFile.PathIn(row.Site.Folder));
        return Task.CompletedTask;
    });

    [RelayCommand]
    private void DismissSourcesWarning() => SourcesWarning = "";

    // C3: carpetas aparcadas

    [RelayCommand]
    private async Task ParkFolderAsync()
    {
        if (await PickFolderAsync(Main, "Aparcar una carpeta: cada subcarpeta será un sitio <subcarpeta>.test", Main.App.Paths.Www) is not { } folder) return;
        await ParkAsync(folder);
    }

    internal Task ParkAsync(string folder) => Main.RunAsync($"No se pudo aparcar {folder}", async () =>
    {
        var paths = Main.App.Paths;
        var sync = await Task.Run(() => new SiteManager(paths).Park(folder));
        foreach (var skip in sync.Skipped) _skipsSeen.Add(skip.Folder);
        Main.NotifyConfigChanged();
        var hostsOk = !sync.Changed || await Main.SyncHostsAsync(showError: false);
        if (sync.Changed) await Main.ApplyAsync();
        Pick(_all.FirstOrDefault(r => sync.Added.Any(a => a.Name == r.Site.Name)) ?? Selected);

        var text = sync.Added.Count switch
        {
            0 => "No ha salido ningún sitio nuevo: sus subcarpetas ya eran sitios, o no valen como nombre.",
            1 => $"Un sitio nuevo: {sync.Added[0].Host}.",
            _ => $"{sync.Added.Count} sitios nuevos: {string.Join(", ", sync.Added.Select(s => s.Host))}.",
        };
        text += "\n\nMientras la app esté abierta (o el stack en marcha), una subcarpeta nueva será un sitio nuevo, y una borrada se quitará.";
        if (!hostsOk) text += $"\n\n{Main.HostsError} Los nombres nuevos no resolverán hasta que estén en el hosts: «Añadir al hosts» arriba.";
        var detail = sync.Skipped.Count == 0 ? null
            : "Se saltan:\n" + string.Join("\n", sync.Skipped.Select(s => $"· {Path.GetFileName(s.Folder)}: {SkipText(s)}"));
        await Main.InfoAsync($"Aparcada {folder}", text, hostsOk ? Tone.Ok : Tone.Warn, detail);
    });

    [RelayCommand]
    private async Task UnparkAsync(ParkedRow row)
    {
        if (!await Main.ConfirmAsync($"Dejar de aparcar {Path.GetFileName(row.Folder)}",
                $"Se quitan los {row.Count} sitios que salen de {row.Folder} (como «Quitar sitio»: sus carpetas no se tocan). Los que diste de alta a mano se quedan.",
                "Dejar de aparcar", danger: true))
            return;
        await Main.RunAsync($"No se pudo dejar de aparcar {row.Folder}", async () =>
        {
            var paths = Main.App.Paths;
            var removed = await Task.Run(() => new SiteManager(paths).Unpark(row.Folder));
            Main.NotifyConfigChanged();
            await Main.SyncHostsAsync(showError: false);
            await Main.ApplyAsync();
            Main.Flash(removed.Count == 0 ? $"{row.Folder} ya no está aparcada" : $"Quitados {removed.Count} sitios de {Path.GetFileName(row.Folder)}");
        });
    }

    /// <summary>Por qué se salta una carpeta, con las palabras de la app.</summary>
    private static string SkipText(ParkSkip skip) =>
        skip.Reason.Contains("no vale como nombre", StringComparison.Ordinal)
            ? "su nombre no vale como .test (solo minúsculas, números y guiones, sin espacios ni acentos): renómbrala o pon name en su .roost.toml"
        : skip.Reason.StartsWith("ya hay un sitio", StringComparison.Ordinal) ? skip.Reason.Replace("ya hay un sitio", "ya hay un sitio con ese nombre:", StringComparison.Ordinal)
        : skip.Reason.Contains("no esta ahora", StringComparison.Ordinal) ? "no está ahora (¿un disco que no está puesto?): sus sitios se quedan"
        : skip.Reason;

    /// <summary>
    /// Al abrir la app: pone al día los sitios con su <c>.roost.toml</c> y las carpetas aparcadas (lo
    /// que cambió con la app cerrada) y empieza a vigilar las aparcadas. Lo llama <see cref="MainViewModel.InitializeAsync"/>.
    /// </summary>
    public void StartParkWatch()
    {
        _ = SyncSourcesAsync(onOpen: true);
        if (_parkWatcher is not null) return;
        _parkWatcher = new ParkWatcher();
        _parkWatcher.Changed += () => Dispatcher.UIThread.Post(() => _ = SyncSourcesAsync(onOpen: false));
        _parkWatcher.Watch(new ParkedFolders(Main.App.Paths).Load());
        // La lista de carpetas puede cambiar desde la CLI (roost park): se vuelve a mirar cada poco.
        _parkRefresh = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
        _parkRefresh.Tick += (_, _) => _parkWatcher?.Watch(new ParkedFolders(Main.App.Paths).Load());
        _parkRefresh.Start();
    }

    /// <summary>Deja de vigilar las carpetas aparcadas (los tests, al acabar).</summary>
    internal void StopParkWatch()
    {
        _parkRefresh?.Stop();
        _parkRefresh = null;
        _parkWatcher?.Dispose();
        _parkWatcher = null;
    }

    /// <summary>
    /// El <c>.roost.toml</c> de cada sitio (solo al abrir) y las carpetas aparcadas, al registro; si cambia
    /// algo con el stack en marcha, se aplica (recarga el servidor web). Lo nuevo que falte en el hosts
    /// queda en el aviso de arriba, con un solo botón: así varias carpetas nuevas piden un solo permiso.
    /// </summary>
    internal async Task SyncSourcesAsync(bool onOpen)
    {
        if (_parkBusy)
        {
            _parkAgain = true;
            return;
        }
        _parkBusy = true;
        try
        {
            do
            {
                _parkAgain = false;
                var paths = Main.App.Paths;
                (ProjectSync? Project, ParkSync Parked) result;
                try
                {
                    result = await Task.Run(() =>
                    {
                        var sites = new SiteManager(paths);
                        return (onOpen ? sites.SyncProjectFiles() : null, sites.SyncParked());
                    });
                }
                catch (Exception ex) when (ex is RoostException or IOException or UnauthorizedAccessException)
                {
                    SourcesWarning = ex.Message;
                    return;
                }

                var (project, parked) = result;
                var warnings = new List<string>(project?.Warnings ?? []);
                foreach (var skip in parked.Skipped.Where(s => _skipsSeen.Add(s.Folder)))
                    warnings.Add($"{skip.Folder}: {SkipText(skip)}");
                if (warnings.Count > 0) SourcesWarning = string.Join("\n", warnings);

                var changed = (project?.Changed ?? false) || parked.Changed;
                if (!changed) continue;
                Main.NotifyConfigChanged();
                if (Main.Stack.IsRunning) await Main.ApplyAsync();

                var parts = new List<string>();
                if (parked.Added.Count > 0) parts.Add(parked.Added.Count == 1 ? $"{parked.Added[0].Host} nuevo" : $"{parked.Added.Count} sitios nuevos");
                if (parked.Removed.Count > 0) parts.Add(parked.Removed.Count == 1 ? $"{parked.Removed[0].Host} quitado" : $"{parked.Removed.Count} sitios quitados");
                if (project?.Changed == true) parts.Add(project.Changes.Count == 1 ? $"{project.Changes[0].Site}.test al día con su .roost.toml" : $"{project.Changes.Count} cambios de .roost.toml");
                var newNames = parked.Added.Count > 0 || project?.NamesChanged == true;
                Main.Flash(string.Join(" · ", parts) + (newNames && HasHostsPending ? " · faltan en el hosts" : ""));
            }
            while (_parkAgain);
        }
        finally
        {
            _parkBusy = false;
        }
    }
}
