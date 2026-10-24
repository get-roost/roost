using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Roost.Core;
using Roost.Core.Audit;
using Roost.Core.Config;
using Roost.Core.Deploy;
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
/// <c>Template</c> es la plantilla (null = la carpeta de siempre) y <c>Webapp</c>, en Symfony, si va con webapp.
/// </summary>
public sealed record NewSiteRequest(string Name, string Folder, string Root, string? Php, string? Database, bool CreateDatabase = false,
    ProjectTemplate? Template = null, bool Webapp = false);

/// <summary>Lo que devuelve Nuevo sitio al elegir «Desde git…»: quien lo abrió lanza el asistente de clonar.</summary>
public sealed record NewSiteFromGit;

/// <summary>Una plantilla de Nuevo sitio: la carpeta de siempre, Laravel, WordPress, Symfony o «Desde git…» (el asistente de clonar).</summary>
public sealed partial class SiteTemplate(string label, bool available, string hint, TemplateInfo? project = null) : ObservableObject
{
    public string Label { get; } = label;

    public bool Available { get; } = available;

    public string Hint { get; } = hint;

    /// <summary>La plantilla de proyecto; null = la carpeta de siempre (o una que aún no está).</summary>
    public TemplateInfo? Project { get; } = project;

    [ObservableProperty] public partial bool IsSelected { get; set; }
}

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
/// Roost al crearlo. Con una plantilla (Laravel, WordPress, Symfony; <see cref="ProjectTemplates"/>)
/// la carpeta tiene que ser nueva o estar vacía, la raíz web la pone ella y solo salen los PHP que
/// valen para su versión actual. Devuelve un <see cref="NewSiteRequest"/>.
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
        Templates[0].IsSelected = true;
        DbMode = _db is not null ? NewSiteDb.Create : NewSiteDb.None;
        Update();
        _ = LoadExistingDatabasesAsync();
    }

    public override bool Wide => true;

    // Pasos y plantillas: las plantillas de verdad (C12). «Desde git…» cierra el diálogo y abre el asistente de Git-app.
    public IReadOnlyList<SiteTemplate> Templates { get; } =
    [
        new("Carpeta (lo de siempre)", true, "Una carpeta con un index.php, o la tuya con tu proyecto dentro: se sirve tal cual."),
        new("Laravel", true, "composer create-project laravel/laravel: Laravel 13, que pide PHP 8.3 o más nuevo. Después, el .env con su base de datos y su URL, las migraciones y storage:link.", ProjectTemplates.Laravel),
        new("WordPress", true, "La última versión de wordpress.org, comprobada con su SHA-1, con su base de datos y un wp-config.php con claves nuevas. Luego, su instalador en el navegador.", ProjectTemplates.WordPress),
        new("Symfony", true, "composer create-project symfony/skeleton: Symfony 8.1 con PHP 8.4 o más nuevo (la 7.4 LTS con 8.2 y 8.3), con su .env.local. Con «webapp», también Twig, Doctrine y formularios.", ProjectTemplates.Symfony),
        new("Desde git…", true, "Clona un repositorio en tres pasos: la dirección, cómo entrar y el sitio."),
    ];

    /// <summary>La plantilla de proyecto elegida; null = la carpeta de siempre.</summary>
    public TemplateInfo? Project => Template.Project;

    public bool IsProjectTemplate => Template.Project is not null;

    public bool IsFolderTemplate => Template.Project is null;

    public bool IsSymfony => Template.Project?.Template == ProjectTemplate.Symfony;

    /// <summary>Symfony: también <c>composer require webapp</c> (Twig, Doctrine, formularios, seguridad…).</summary>
    [ObservableProperty] public partial bool Webapp { get; set; }

    /// <summary>La carpeta vale para lo elegido (una plantilla necesita una nueva o vacía).</summary>
    [ObservableProperty] public partial bool FolderValid { get; private set; } = true;

    /// <summary>Hay un PHP que vale para la plantilla (con la carpeta de siempre, siempre).</summary>
    [ObservableProperty] public partial bool PhpValid { get; private set; } = true;

    /// <summary>Con una plantilla: qué PHP pide; vacío con la carpeta de siempre.</summary>
    [ObservableProperty] public partial string PhpHint { get; private set; } = "";

    /// <summary>Con una plantilla: lo que hace Roost después de crear el proyecto.</summary>
    [ObservableProperty] public partial string TemplateStep { get; private set; } = "";

    public bool HasPhpHint => PhpHint.Length > 0;

    /// <summary>La raíz web se puede tocar (con una plantilla la pone ella).</summary>
    public bool RootEditable => IsFolderTemplate;

    public bool CanBrowseRoot => IsFolderTemplate && FolderExists;

    partial void OnTemplateChanged(SiteTemplate value)
    {
        if (value == Templates[^1])
        {
            Close(new NewSiteFromGit());
            return;
        }
        foreach (var t in Templates) t.IsSelected = t == value;
        // La base de datos que pide cada plantilla (si hay una global): Laravel y WordPress sí; Symfony, con webapp.
        if (value.Project is { } project && HasDb)
            DbMode = project.Template == ProjectTemplate.Symfony && !Webapp ? NewSiteDb.None : NewSiteDb.Create;
        _rootTouched = false;
        OnPropertyChanged(nameof(Project));
        OnPropertyChanged(nameof(IsProjectTemplate));
        OnPropertyChanged(nameof(IsFolderTemplate));
        OnPropertyChanged(nameof(IsSymfony));
        OnPropertyChanged(nameof(RootEditable));
        OnPropertyChanged(nameof(CanBrowseRoot));
        Update();
    }

    partial void OnWebappChanged(bool value)
    {
        if (IsSymfony && HasDb) DbMode = value ? NewSiteDb.Create : NewSiteDb.None;
        Update();
    }

    partial void OnFolderValidChanged(bool value) => OnPropertyChanged(nameof(CanCreate));
    partial void OnPhpValidChanged(bool value) => OnPropertyChanged(nameof(CanCreate));
    partial void OnPhpHintChanged(string value) => OnPropertyChanged(nameof(HasPhpHint));
    partial void OnFolderExistsChanged(bool value) => OnPropertyChanged(nameof(CanBrowseRoot));

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

    /// <summary>«Crear y abrir» solo con un nombre que vale, una raíz web que existe y una base de datos que vale (y, con una plantilla, una carpeta nueva y un PHP que valga).</summary>
    public bool CanCreate => NameValid && RootValid && DbValid && FolderValid && PhpValid;

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
        // Una plantilla crea un sitio nuevo: sobre uno que ya existe, no.
        if (Template.Project is not null && NameValid && _sites.Any(s => s.Name == name))
        {
            NameValid = false;
            NameHint = $"Ya existe {name}.test: una plantilla crea un sitio nuevo; elige otro nombre.";
        }

        if (!_folderTouched)
        {
            // Sin carpeta elegida, sigue al nombre: www\<nombre>.
            var folder = DefaultFolder;
            if (Folder != folder) Folder = folder;
        }

        var path = FolderPath;
        var exists = path is not null && Directory.Exists(path);
        FolderExists = exists;
        if (Template.Project is { } template)
            UpdateForTemplate(template, path, exists);
        else
            UpdateForFolder(path, exists);
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

    /// <summary>La carpeta de siempre: se detecta lo que haya dentro y se sirve tal cual.</summary>
    private void UpdateForFolder(string? path, bool exists)
    {
        FolderValid = true;
        PhpValid = true;
        PhpHint = "";
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
    }

    /// <summary>
    /// Una plantilla: la carpeta tiene que ser nueva (o estar vacía), la raíz web la pone ella, y solo
    /// salen los PHP que valen para su versión actual (Laravel 13 pide 8.3).
    /// </summary>
    private void UpdateForTemplate(TemplateInfo template, string? path, bool exists)
    {
        var empty = exists && !Directory.EnumerateFileSystemEntries(path!).Any();
        FolderValid = path is not null && (!exists || empty) && !File.Exists(path);
        ProjectLabel = "";
        NeedsComposerInstall = false;
        FolderHint = path is null ? "Escribe la carpeta del proyecto (o elígela)."
            : File.Exists(path) ? "Es un fichero, no una carpeta."
            : !exists ? $"No existe: Roost crea aquí el proyecto {template.Name}."
            : empty ? $"Existe y está vacía: Roost crea aquí el proyecto {template.Name}."
            : "Ya existe y no está vacía: una plantilla necesita una carpeta nueva (o vacía). Elige otra, o usa «Carpeta» para servir ese proyecto.";

        // La raíz web la pone la plantilla.
        var webRoot = template.Expected(Webapp).WebRoot ?? "";
        _suggestedRoot = webRoot;
        if (Root != webRoot) Root = webRoot;
        RootValid = true;
        RootHint = webRoot.Length == 0 ? "WordPress se sirve desde su carpeta." : $"La pone la plantilla: {webRoot}/ (el resto del proyecto queda fuera de la web).";

        StepFolder = template.Template switch
        {
            ProjectTemplate.Laravel => $"Crea el proyecto en {Folder} con composer create-project laravel/laravel ({template.Version})",
            ProjectTemplate.Symfony => $"Crea el proyecto en {Folder} con composer create-project symfony/skeleton{(Webapp ? " y composer require webapp" : "")}",
            _ => $"Descarga WordPress en {Folder}: la última de wordpress.org, comprobada con su SHA-1",
        };
        BuildTemplatePhpOptions(template);
    }

    /// <summary>Los PHP que valen para la plantilla: el global si llega, y los instalados que llegan.</summary>
    private void BuildTemplatePhpOptions(TemplateInfo template)
    {
        var previous = Php?.Spec;
        var options = new List<PhpOption>();
        if (_global?.Version is { } global && global >= template.MinPhp) options.Add(new PhpOption($"Global (ahora {global})", null));
        foreach (var php in ProjectTemplates.SuitablePhp(template, _installed))
        {
            var eol = php.Version is { } v && _main.Audit.Evaluate(ComponentKind.Php, v).Verdict == AuditVerdict.Eol;
            options.Add(new PhpOption($"{php.Version}{(eol ? " (EOL)" : "")}", HomeViewModel.Branch(php.Name)));
        }
        PhpOptions = options;
        Php = options.FirstOrDefault(o => o.Spec == previous) ?? options.FirstOrDefault();
        PhpValid = options.Count > 0;
        var min = $"{template.MinPhp.Major}.{template.MinPhp.Minor}";
        PhpHint = options.Count == 0
            ? $"{template.Name} pide PHP {min} o más nuevo y no tienes ninguno: instálalo con «Instalar otra versión…»."
            : template.Template == ProjectTemplate.Symfony
                ? $"Symfony pide PHP {min} o más nuevo: con 8.4 o más sale la 8.1; con 8.2 y 8.3, la 7.4 (LTS)."
                : $"{template.Version.Split(';')[0]} pide PHP {min} o más nuevo: solo salen los que valen.";
    }

    /// <summary>Lo que hace Roost tras crear el proyecto, con la base de datos elegida.</summary>
    private void UpdateTemplateStep()
    {
        var db = ChosenDb;
        TemplateStep = Template.Project?.Template switch
        {
            ProjectTemplate.Laravel => $"Deja el .env con {(db is null ? "" : $"la base de datos {db} y ")}https://{Host}, y hace migrate y storage:link, con la salida en vivo",
            ProjectTemplate.Symfony => $"Deja el .env.local con {(Webapp && db is not null ? $"DATABASE_URL a {db} y " : "")}la URL del sitio{(Webapp ? ", y hace las migraciones" : "")}, con la salida en vivo",
            ProjectTemplate.WordPress => "Escribe el wp-config.php con la base de datos y claves nuevas (y un mu-plugin para los certificados .test); el instalador de WordPress sale en el navegador",
            _ => "",
        };
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
        // WordPress sin base de datos no se puede dejar listo (su wp-config.php la necesita).
        if (Template.Project?.Template == ProjectTemplate.WordPress && DbMode == NewSiteDb.None)
            (DbValid, DbHint) = (false, _db is null
                ? "WordPress necesita una base de datos y no hay ninguna global: instálala en Bases de datos."
                : "WordPress necesita una base de datos: crea una o usa una que ya exista.");
        OnPropertyChanged(nameof(ChosenDb));
        UpdateTemplateStep();
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
            // Con una plantilla, solo las que valen para ella.
            var min = Template.Project?.MinPhp;
            InstallChoices = catalog
                .Where(b => !b.ThreadSafe && b.Architecture == "x64" && !installed.Contains(b.InstallName) && (min is null || b.Version >= min.Value))
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
        if (Template.Project is { } template) BuildTemplatePhpOptions(template);
        else BuildPhpOptions(FolderExists ? Recommended() : null);
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
        var template = Template.Project?.Template;
        Close(new NewSiteRequest(Name.Trim().ToLowerInvariant(), Folder, root.Length == 0 ? "." : root, Php?.Spec, database,
            CreateDatabase: database is not null && DbMode == NewSiteDb.Create,
            Template: template, Webapp: template == ProjectTemplate.Symfony && Webapp));
    }
}
