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

namespace Roost.Desktop.ViewModels;

/// <summary>
/// Lo que pide el diálogo Nuevo sitio. <c>Root</c> es la raíz web relativa a la carpeta
/// (<c>.</c> = la carpeta misma); <c>Php</c> null = el global; <c>Database</c> es la base de datos
/// del sitio (null = ninguna), y <c>CreateDatabase</c> dice si hay que crearla o ya existe.
/// </summary>
public sealed record NewSiteRequest(string Name, string Folder, string Root, string? Php, string? Database, bool CreateDatabase = false);

/// <summary>Una plantilla de proyecto (de momento solo «Vacía»; el resto llega con <c>roost new</c>).</summary>
public sealed record SiteTemplate(string Label, bool Available, string Hint);

/// <summary>Qué hacer con la base de datos del sitio nuevo.</summary>
public enum NewSiteDb
{
    /// <summary>Crear una con el nombre del campo.</summary>
    Create,

    /// <summary>Usar una que ya existe en la global (no se crea nada).</summary>
    Existing,

    /// <summary>Ninguna.</summary>
    None,
}

/// <summary>Una versión de PHP que se puede instalar sin salir de Nuevo sitio.</summary>
public sealed record PhpInstallChoice(PhpBuild Build, string Label);

/// <summary>
/// «Nuevo sitio»: nombre (que sale de la carpeta si se elige primero), carpeta, raíz web (la que
/// toca al proyecto que se detecta: Laravel, Symfony, WordPress, o public/ si la tiene), PHP
/// (recomendado por el composer.json si lo hay, e «Instalar otra versión» sin perder lo
/// tecleado), base de datos (crear una, usar una que existe o ninguna) y plantilla, con lo que hará
/// Roost al crearlo. Devuelve un <see cref="NewSiteRequest"/>.
/// </summary>
public sealed partial class NewSiteDialogViewModel : DialogViewModel
{
    private readonly MainViewModel _main;
    private readonly IReadOnlyList<Site> _sites;
    private IReadOnlyList<InstalledPhp> _installed;
    private readonly InstalledPhp? _global;
    private readonly InstalledDb? _db;
    private bool _folderTouched;
    private bool _rootTouched;
    private bool _nameTouched;
    private bool _dbNameTouched;
    private bool _settingName;
    private bool _settingDbName;
    private string _suggestedRoot = "";

    // Lo que se lee de la carpeta (composer.json, el tipo de proyecto) se guarda por ruta: Update
    // corre en cada tecla y no hace falta ir al disco cada vez.
    private string? _detectedFor;
    private ProjectKind? _project;
    private string? _phpConstraint;

    public NewSiteDialogViewModel(MainViewModel main)
    {
        _main = main;
        var paths = main.App.Paths;
        try { _sites = new SiteRegistry(paths).Load(); } catch (RoostException) { _sites = []; }
        _installed = new PhpInstaller(paths).ListInstalled();
        RoostConfig config;
        try { config = RoostConfig.Load(paths.ConfigFile); } catch (RoostException) { config = new RoostConfig(); }
        var planner = new StackPlanner(paths, ProcessRunner.Default);
        try { _global = planner.ResolvePhp(config, _installed); } catch (RoostException) { }
        try { _db = planner.ResolveDb(config, new DbInstaller(paths).ListInstalled()); } catch (RoostException) { }
        UsesLocalDns = config.UsesLocalDns;
        Template = Templates[0];
        DbMode = _db is not null ? NewSiteDb.Create : NewSiteDb.None;
        Update();
        _ = LoadExistingDatabasesAsync();
    }

    public override bool Wide => true;

    public IReadOnlyList<SiteTemplate> Templates { get; } =
    [
        new("Vacía", true, "Una carpeta con un index.php. Pon tu proyecto dentro (o elige una carpeta que ya lo tenga)."),
        new("Laravel", false, "Próximamente: composer create-project laravel/laravel."),
        new("WordPress", false, "Próximamente: la última versión con su base de datos y su wp-config.php."),
        new("Symfony", false, "Próximamente: symfony/skeleton."),
        new("Desde git…", false, "Próximamente: clonar un repositorio."),
    ];

    [ObservableProperty] public partial string Name { get; set; } = "";
    [ObservableProperty] public partial string Folder { get; set; } = "";
    [ObservableProperty] public partial string NameHint { get; private set; } = "";
    [ObservableProperty] public partial bool NameValid { get; private set; }
    [ObservableProperty] public partial string FolderHint { get; private set; } = "";
    [ObservableProperty] public partial bool FolderExists { get; private set; }

    /// <summary>El proyecto que hay en la carpeta («Proyecto Laravel»), o vacío si no es ninguno conocido.</summary>
    [ObservableProperty] public partial string ProjectLabel { get; private set; } = "";

    /// <summary>La carpeta tiene composer.json y le falta vendor/: se dice en los pasos.</summary>
    [ObservableProperty] public partial bool NeedsComposerInstall { get; private set; }

    /// <summary>La raíz web, relativa a la carpeta; vacía = la carpeta misma. Se rellena sola con la del proyecto (public si existe).</summary>
    [ObservableProperty] public partial string Root { get; set; } = "";
    [ObservableProperty] public partial string RootHint { get; private set; } = "";
    [ObservableProperty] public partial bool RootValid { get; private set; } = true;
    [ObservableProperty] public partial IReadOnlyList<PhpOption> PhpOptions { get; private set; } = [];
    [ObservableProperty] public partial PhpOption? Php { get; set; }
    [ObservableProperty] public partial SiteTemplate Template { get; set; }
    [ObservableProperty] public partial string StepFolder { get; private set; } = "";

    public bool HasProject => ProjectLabel.Length > 0;

    public string Host => NameValid ? $"{Name.Trim().ToLowerInvariant()}.test" : "<nombre>.test";

    /// <summary>«Crear y abrir» solo con un nombre que vale, una raíz web que existe y una base de datos que vale.</summary>
    public bool CanCreate => NameValid && RootValid && DbValid;

    /// <summary>Con el DNS local, el nombre ya resuelve: no hay que tocar el hosts ni pedir permiso.</summary>
    public bool UsesLocalDns { get; private set; }
    public bool UsesHosts => !UsesLocalDns;

    partial void OnNameChanged(string value)
    {
        // Tecleado a mano ya no se cambia al elegir carpeta; vaciado, vuelve a seguirla.
        if (!_settingName) _nameTouched = value.Trim().Length > 0;
        Update();
    }

    partial void OnFolderChanged(string value)
    {
        if (value != DefaultFolder) _folderTouched = true;
        // Una raíz tecleada para otra carpeta no se arrastra: se vuelve a proponer.
        _rootTouched = false;
        // Elegida la carpeta antes que el nombre, el nombre sale de ella (tienda-online → tienda-online.test).
        if (_folderTouched && !_nameTouched && FolderPath is { } path
            && WelcomeDialogViewModel.SiteName(Path.GetFileName(Path.TrimEndingDirectorySeparator(path))) is { } proposed
            && proposed != Name)
        {
            _settingName = true;
            Name = proposed;
            _settingName = false;
        }
        Update();
    }

    partial void OnRootChanged(string value)
    {
        if (value != _suggestedRoot) _rootTouched = true;
        Update();
    }

    partial void OnNameValidChanged(bool value) => OnPropertyChanged(nameof(CanCreate));
    partial void OnRootValidChanged(bool value) => OnPropertyChanged(nameof(CanCreate));
    partial void OnProjectLabelChanged(string value) => OnPropertyChanged(nameof(HasProject));

    private string DefaultFolder => Path.Combine(_main.App.Paths.Www, Name.Trim().ToLowerInvariant());

    /// <summary>La carpeta como la verá Roost: en relativo cuelga de la www. null si está vacía.</summary>
    private string? FolderPath
    {
        get
        {
            var folder = Folder.Trim();
            if (folder.Length == 0) return null;
            try { return Path.GetFullPath(folder, _main.App.Paths.Www); }
            catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException) { return null; }
        }
    }

    private void Update()
    {
        var name = Name.Trim().ToLowerInvariant();
        NameValid = Site.IsValidName(name);
        NameHint = name.Length == 0 ? "Minúsculas, números y guiones: tienda, mi-blog…"
            : !NameValid ? "Solo minúsculas, números y guiones (sin empezar ni acabar en guion)."
            : _sites.Any(s => s.Name == name) ? $"Ya existe {name}.test: se actualizará."
            : $"Se servirá en https://{name}.test";

        if (!_folderTouched)
        {
            // Sin carpeta elegida, sigue al nombre: www\<nombre>.
            var folder = DefaultFolder;
            if (Folder != folder) Folder = folder;
        }

        var path = FolderPath;
        var exists = path is not null && Directory.Exists(path);
        FolderExists = exists;
        Detect(exists ? path : null);
        var project = _project;

        ProjectLabel = project?.Framework is { } framework ? $"Proyecto {framework}" : "";
        NeedsComposerInstall = project?.NeedsComposerInstall == true;
        FolderHint = !exists ? "No existe: Roost la crea con un index.php."
            : project?.Framework is { } kind
                ? kind == "WordPress" || project.WebRoot is null ? $"Proyecto {kind}: se sirve la carpeta." : $"Proyecto {kind}: raíz web {project.WebRoot}/."
            : project?.WebRoot is { } webRoot ? $"Existe y tiene {webRoot}/: será la raíz web."
            : "Existe: se sirve tal cual.";

        // La raíz web sigue a la carpeta (la del proyecto: public/ si la tiene) hasta que se toca.
        _suggestedRoot = project?.WebRoot ?? "";
        if (!_rootTouched && Root != _suggestedRoot) Root = _suggestedRoot;
        var root = CheckRoot(path, exists);

        StepFolder = !exists ? $"Crea {Folder} con un index.php" : $"Usa {Folder}{(root is null ? "" : $" (raíz web: {root}/)")}";

        BuildPhpOptions(exists ? Recommended() : null);
        OnPropertyChanged(nameof(Host));

        // El nombre de la base de datos nueva sigue al del sitio hasta que se toca.
        if (!_dbNameTouched)
        {
            _settingDbName = true;
            DbName = name.Length == 0 ? "" : DbAdmin.NameFor(name);
            _settingDbName = false;
        }
        UpdateDb();
    }

    /// <summary>Lee la carpeta (tipo de proyecto y composer.json) solo si ha cambiado.</summary>
    private void Detect(string? folder)
    {
        if (folder == _detectedFor) return;
        _detectedFor = folder;
        _project = folder is null ? null : ProjectKind.Detect(folder);
        _phpConstraint = folder is null ? null : ComposerJson.PhpConstraint(folder);
    }

    /// <summary>Valida la raíz tecleada y escribe su pista. Devuelve la raíz relativa que se servirá, o null si es la carpeta.</summary>
    private string? CheckRoot(string? folder, bool folderExists)
    {
        var root = Root.Trim().Replace('\\', '/').Trim('/');
        if (root.Length == 0 || root == ".")
        {
            RootValid = true;
            RootHint = "Vacía: se sirve la carpeta del proyecto tal cual.";
            return null;
        }
        if (!folderExists || folder is null)
        {
            RootValid = false;
            RootHint = "La carpeta no existe aún (Roost la crea vacía): déjala vacía o elige una carpeta que ya tenga esa subcarpeta.";
            return null;
        }

        var full = Path.GetFullPath(root, folder);
        var relative = Path.GetRelativePath(folder, full);
        if (relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative))
        {
            RootValid = false;
            RootHint = "Tiene que ser una subcarpeta de la carpeta del proyecto.";
            return null;
        }
        if (!Directory.Exists(full))
        {
            RootValid = false;
            RootHint = $"No existe {relative.Replace('\\', '/')}/ dentro de la carpeta.";
            return null;
        }
        RootValid = true;
        var shown = relative.Replace('\\', '/');
        RootHint = $"Se sirve {shown}/; el resto del proyecto queda fuera de la web.";
        return shown;
    }

    private void BuildPhpOptions((InstalledPhp Php, string Constraint)? recommended)
    {
        // Lo recomendado por un composer.json no se arrastra a otra carpeta: se vuelve al global.
        var previous = Php is { } p && !p.Label.Contains("recomendada", StringComparison.Ordinal) ? p.Spec : null;
        var options = new List<PhpOption>();
        if (recommended is { } r)
            options.Add(new PhpOption($"{r.Php.Version} · recomendada por composer.json ({r.Constraint})", HomeViewModel.Branch(r.Php.Name)));
        options.Add(new PhpOption($"Global (ahora {_global?.Version?.ToString() ?? "ninguno"})", null));
        foreach (var php in _installed.Where(p => !p.ThreadSafe))
        {
            var eol = php.Version is { } v && _main.Audit.Evaluate(ComponentKind.Php, v).Verdict == AuditVerdict.Eol;
            options.Add(new PhpOption($"{php.Version}{(eol ? " (EOL)" : "")}", HomeViewModel.Branch(php.Name)));
        }
        PhpOptions = options;
        Php = recommended is not null ? options[0] : options.FirstOrDefault(o => o.Spec == previous) ?? options.First(o => o.Spec is null);
    }

    /// <summary>
    /// El PHP que pide el composer.json de la carpeta (<c>require.php</c>): el más nuevo de los
    /// instalados que cumple la versión mínima (y, con <c>^</c>, su misma versión mayor).
    /// </summary>
    private (InstalledPhp, string)? Recommended() =>
        _phpConstraint is { } constraint && ComposerJson.Recommended(constraint, _installed) is { } best
            ? (best, constraint)
            : null;

    // La base de datos

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DbIsCreate), nameof(DbIsExisting), nameof(DbIsNone), nameof(CreateDatabase), nameof(ShowExistingList), nameof(ShowExistingText))]
    public partial NewSiteDb DbMode { get; set; }

    /// <summary>El nombre de la base de datos nueva: sale del nombre del sitio (mi-blog → mi_blog) hasta que se toca.</summary>
    [ObservableProperty] public partial string DbName { get; set; } = "";

    /// <summary>La que ya existe (de la lista, o tecleada si no se ha podido leer).</summary>
    [ObservableProperty] public partial string ExistingDb { get; set; } = "";

    /// <summary>Las bases de datos de la global en marcha; vacía si está parada o no se ha podido leer.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowExistingList), nameof(ShowExistingText))]
    public partial IReadOnlyList<string> ExistingDbs { get; private set; } = [];

    [ObservableProperty] public partial string DbHint { get; private set; } = "";
    [ObservableProperty] public partial bool DbValid { get; private set; } = true;

    public bool HasDb => _db is not null;
    public string DbLabel => _db is null ? "No hay base de datos global" : $"{_db.Engine.DisplayName()} {_db.Branch}";

    public bool DbIsCreate { get => DbMode == NewSiteDb.Create; set { if (value) DbMode = NewSiteDb.Create; } }
    public bool DbIsExisting { get => DbMode == NewSiteDb.Existing; set { if (value) DbMode = NewSiteDb.Existing; } }
    public bool DbIsNone { get => DbMode == NewSiteDb.None; set { if (value) DbMode = NewSiteDb.None; } }

    /// <summary>Si al crear el sitio se crea también su base de datos (la casilla de antes).</summary>
    public bool CreateDatabase { get => DbMode == NewSiteDb.Create; set => DbMode = value ? NewSiteDb.Create : NewSiteDb.None; }

    public bool ShowExistingList => DbIsExisting && ExistingDbs.Count > 0;
    public bool ShowExistingText => DbIsExisting && ExistingDbs.Count == 0;

    /// <summary>La base de datos que se pasará al crear (null = ninguna).</summary>
    public string? ChosenDb => DbMode switch
    {
        NewSiteDb.Create => DbName.Trim(),
        NewSiteDb.Existing => ExistingDb.Trim(),
        _ => null,
    };

    partial void OnDbModeChanged(NewSiteDb value) => UpdateDb();

    partial void OnDbNameChanged(string value)
    {
        if (!_settingDbName) _dbNameTouched = value.Trim().Length > 0;
        UpdateDb();
    }

    partial void OnExistingDbChanged(string value) => UpdateDb();
    partial void OnDbValidChanged(bool value) => OnPropertyChanged(nameof(CanCreate));

    private void UpdateDb()
    {
        if (_db is null && DbMode != NewSiteDb.None) DbMode = NewSiteDb.None;
        var name = ChosenDb ?? "";
        var valid = DbAdmin.IsValidName(name) && !DbAdmin.SystemSchemas.Contains(name);
        (DbValid, DbHint) = DbMode switch
        {
            NewSiteDb.None => (true, _db is null ? "No hay base de datos global: instálala en Bases de datos." : "El sitio no tendrá base de datos (se puede crear luego desde su panel)."),
            _ when name.Length == 0 => (false, DbMode == NewSiteDb.Create ? "Escribe el nombre de la base de datos." : "Elige o escribe la que ya existe."),
            _ when !valid => (false, DbAdmin.SystemSchemas.Contains(name) ? $"{name} es del propio servidor: elige otra." : "Solo letras, números y _ (hasta 64)."),
            NewSiteDb.Create when ExistingDbs.Contains(name, StringComparer.OrdinalIgnoreCase) => (true, $"{name} ya existe en {DbLabel}: se usará esa."),
            NewSiteDb.Create => (true, $"Se crea en {DbLabel} (utf8mb4) al terminar."),
            _ => (true, ExistingDbs.Count == 0 ? $"No se ha podido leer la lista de {DbLabel} (¿está parada?): escribe su nombre." : $"El sitio usará {name}; no se crea nada."),
        };
        OnPropertyChanged(nameof(ChosenDb));
    }

    /// <summary>Las bases de datos que ya hay, si la global está en marcha (fuera del hilo de la UI; sin ella, se teclea).</summary>
    private async Task LoadExistingDatabasesAsync()
    {
        if (_db is not { } db) return;
        var status = _main.Stack.Status;
        var service = $"{db.Engine.Key()}-{db.Branch}";
        if (status?.Running != true || !status.Services.Any(s => s.Name == service && s.State == "Running")) return;
        try
        {
            var ini = new StackPlanner(_main.App.Paths, ProcessRunner.Default).DbIniPath(db);
            ExistingDbs = await Task.Run(() => DbAdmin.List(db, ini, ProcessRunner.Default));
            UpdateDb();
        }
        catch (RoostException) { /* el cliente no contesta: se teclea */ }
    }

    // Instalar otra versión de PHP sin salir del diálogo

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasInstallChoices))]
    public partial IReadOnlyList<PhpInstallChoice> InstallChoices { get; private set; } = [];

    [ObservableProperty] public partial bool ShowInstallChoices { get; private set; }
    [ObservableProperty] public partial string InstallChoicesHint { get; private set; } = "";

    public bool HasInstallChoices => InstallChoices.Count > 0;

    /// <summary>«Instalar otra versión…»: las ramas del catálogo que no están, para instalar una aquí mismo.</summary>
    [RelayCommand]
    private async Task InstallOtherAsync()
    {
        if (ShowInstallChoices)
        {
            ShowInstallChoices = false;
            return;
        }
        ShowInstallChoices = true;
        InstallChoicesHint = "Leyendo el catálogo de windows.php.net…";
        try
        {
            var catalog = await WindowsPhpCatalog.FetchAsync(_main.App.Http);
            var installed = _installed.Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            InstallChoices = catalog
                .Where(b => !b.ThreadSafe && b.Architecture == "x64" && !installed.Contains(b.InstallName))
                .GroupBy(b => b.Branch)
                .Select(g => g.First())
                .OrderByDescending(b => b.Version)
                .Select(b => new PhpInstallChoice(b, _main.Audit.Evaluate(ComponentKind.Php, b.Version).Verdict == AuditVerdict.Eol ? $"{b.Version} · EOL" : b.Version.ToString()))
                .ToList();
            InstallChoicesHint = InstallChoices.Count == 0
                ? "Ya tienes la última de cada rama."
                : "Elige una: se instala sin cerrar este diálogo y queda elegida.";
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or RoostException or System.Text.Json.JsonException)
        {
            InstallChoices = [];
            InstallChoicesHint = $"No se pudo leer el catálogo de windows.php.net: {ex.Message}";
        }
    }

    /// <summary>Instala la versión elegida con el diálogo Instalando (encima de este) y la deja elegida; lo tecleado sigue ahí.</summary>
    [RelayCommand]
    private async Task InstallPhpAsync(PhpInstallChoice choice)
    {
        await _main.Php.InstallBuildAsync(choice.Build);
        _installed = new PhpInstaller(_main.App.Paths).ListInstalled();
        if (!_installed.Any(p => p.Name == choice.Build.InstallName)) return;   // cancelado o fallido: su diálogo ya lo ha dicho

        InstallChoices = InstallChoices.Where(c => c != choice).ToList();
        ShowInstallChoices = false;
        BuildPhpOptions(FolderExists ? Recommended() : null);
        Php = PhpOptions.FirstOrDefault(o => o.Spec == choice.Build.Branch && !o.Label.Contains("recomendada", StringComparison.Ordinal)) ?? Php;
    }

    [RelayCommand]
    private void PickTemplate(SiteTemplate template)
    {
        if (template.Available) Template = template;
    }

    [RelayCommand]
    private async Task BrowseAsync()
    {
        if (await _main.PickFolderAsync("Carpeta del sitio") is { } folder) Folder = folder;
    }

    /// <summary>El selector de carpetas, abierto en la del proyecto; lo elegido se guarda relativo (y se avisa si queda fuera).</summary>
    [RelayCommand]
    private async Task BrowseRootAsync()
    {
        if (FolderPath is not { } folder || !Directory.Exists(folder)) return;
        if (await SitesViewModel.PickFolderAsync(_main, "Raíz web (una subcarpeta del proyecto)", folder) is not { } picked) return;
        var relative = Path.GetRelativePath(folder, picked);
        Root = relative == "." ? "" : relative.Replace('\\', '/');
    }

    [RelayCommand]
    private void Create()
    {
        if (!CanCreate) return;
        // Con la raíz vacía se pasa «.» y no null: null dejaría a Add detectar public/, y si el
        // usuario la ha borrado a propósito es porque quiere la carpeta.
        var root = Root.Trim();
        var database = HasDb ? ChosenDb : null;
        Close(new NewSiteRequest(Name.Trim().ToLowerInvariant(), Folder, root.Length == 0 ? "." : root, Php?.Spec, database,
            CreateDatabase: database is not null && DbMode == NewSiteDb.Create));
    }
}
