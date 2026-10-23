using System.Text.RegularExpressions;
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
/// (<c>.</c> = la carpeta misma); <c>Php</c> null = el global; <c>Database</c> null = no crearla.
/// </summary>
public sealed record NewSiteRequest(string Name, string Folder, string Root, string? Php, string? Database);

/// <summary>Una plantilla de proyecto (de momento solo «Vacía»; el resto llega con <c>roost new</c>).</summary>
public sealed record SiteTemplate(string Label, bool Available, string Hint);

/// <summary>
/// «Nuevo sitio»: nombre, carpeta, raíz web (public/ si la carpeta la tiene), PHP (recomendado
/// por el composer.json si lo hay), base de datos y plantilla, con lo que hará Roost al crearlo.
/// Devuelve un <see cref="NewSiteRequest"/>.
/// </summary>
public sealed partial class NewSiteDialogViewModel : DialogViewModel
{
    private readonly MainViewModel _main;
    private readonly IReadOnlyList<Site> _sites;
    private readonly IReadOnlyList<InstalledPhp> _installed;
    private readonly InstalledPhp? _global;
    private readonly InstalledDb? _db;
    private bool _folderTouched;
    private bool _rootTouched;
    private string _suggestedRoot = "";

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
        CreateDatabase = _db is not null;
        Update();
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

    /// <summary>La raíz web, relativa a la carpeta; vacía = la carpeta misma. Se rellena sola con <c>public</c> si existe.</summary>
    [ObservableProperty] public partial string Root { get; set; } = "";
    [ObservableProperty] public partial string RootHint { get; private set; } = "";
    [ObservableProperty] public partial bool RootValid { get; private set; } = true;
    [ObservableProperty] public partial IReadOnlyList<PhpOption> PhpOptions { get; private set; } = [];
    [ObservableProperty] public partial PhpOption? Php { get; set; }
    [ObservableProperty] public partial bool CreateDatabase { get; set; }
    [ObservableProperty] public partial SiteTemplate Template { get; set; }
    [ObservableProperty] public partial string StepFolder { get; private set; } = "";

    public string Host => NameValid ? $"{Name.Trim().ToLowerInvariant()}.test" : "<nombre>.test";

    /// <summary>«Crear y abrir» solo con un nombre que vale y una raíz web que existe.</summary>
    public bool CanCreate => NameValid && RootValid;

    /// <summary>Con el DNS local, el nombre ya resuelve: no hay que tocar el hosts ni pedir permiso.</summary>
    public bool UsesLocalDns { get; private set; }
    public bool UsesHosts => !UsesLocalDns;
    public string DbName => Regex.Replace(Name.Trim().ToLowerInvariant(), "[^a-z0-9_]", "_");
    public bool HasDb => _db is not null;
    public string DbLabel => _db is null ? "No hay base de datos global" : $"{_db.Engine.DisplayName()} {_db.Branch}";

    partial void OnNameChanged(string value) => Update();

    partial void OnFolderChanged(string value)
    {
        if (value != DefaultFolder) _folderTouched = true;
        // Una raíz tecleada para otra carpeta no se arrastra: se vuelve a proponer.
        _rootTouched = false;
        Update();
    }

    partial void OnRootChanged(string value)
    {
        if (value != _suggestedRoot) _rootTouched = true;
        Update();
    }

    partial void OnNameValidChanged(bool value) => OnPropertyChanged(nameof(CanCreate));
    partial void OnRootValidChanged(bool value) => OnPropertyChanged(nameof(CanCreate));

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
        var hasPublic = exists && Directory.Exists(Path.Combine(path!, "public"));
        FolderExists = exists;
        FolderHint = !exists ? "No existe: Roost la crea con un index.php."
            : hasPublic ? "Existe y tiene public/: será la raíz web."
            : "Existe: se sirve tal cual.";

        // La raíz web sigue a la carpeta (public/ si la tiene) hasta que se toca.
        _suggestedRoot = hasPublic ? "public" : "";
        if (!_rootTouched && Root != _suggestedRoot) Root = _suggestedRoot;
        var root = CheckRoot(path, exists);

        StepFolder = !exists ? $"Crea {Folder} con un index.php" : $"Usa {Folder}{(root is null ? "" : $" (raíz web: {root}/)")}";

        BuildPhpOptions(exists ? Recommended(path!) : null);
        OnPropertyChanged(nameof(Host));
        OnPropertyChanged(nameof(DbName));
        CreateDatabase = CreateDatabase && HasDb;
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
    private (InstalledPhp, string)? Recommended(string folder) =>
        ComposerJson.PhpConstraint(folder) is { } constraint && ComposerJson.Recommended(constraint, _installed) is { } best
            ? (best, constraint)
            : null;

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
    private void InstallOther()
    {
        Close();
        _ = _main.NavigateToAsync(_main.Php);
    }

    [RelayCommand]
    private void Create()
    {
        if (!CanCreate) return;
        // Con la raíz vacía se pasa «.» y no null: null dejaría a Add detectar public/, y si el
        // usuario la ha borrado a propósito es porque quiere la carpeta.
        var root = Root.Trim();
        Close(new NewSiteRequest(Name.Trim().ToLowerInvariant(), Folder, root.Length == 0 ? "." : root, Php?.Spec, CreateDatabase && HasDb ? DbName : null));
    }
}
