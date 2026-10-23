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

/// <summary>Lo que pide el diálogo Nuevo sitio. <c>Php</c> null = el global; <c>Database</c> null = no crearla.</summary>
public sealed record NewSiteRequest(string Name, string Folder, string? Php, string? Database);

/// <summary>Una plantilla de proyecto (de momento solo «Vacía»; el resto llega con <c>roost new</c>).</summary>
public sealed record SiteTemplate(string Label, bool Available, string Hint);

/// <summary>
/// «Nuevo sitio»: nombre, carpeta, PHP (recomendado por el composer.json si lo hay), base de
/// datos y plantilla, con lo que hará Roost al crearlo. Devuelve un <see cref="NewSiteRequest"/>.
/// </summary>
public sealed partial class NewSiteDialogViewModel : DialogViewModel
{
    private readonly MainViewModel _main;
    private readonly IReadOnlyList<Site> _sites;
    private readonly IReadOnlyList<InstalledPhp> _installed;
    private readonly InstalledPhp? _global;
    private readonly InstalledDb? _db;
    private bool _folderTouched;

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
    [ObservableProperty] public partial IReadOnlyList<PhpOption> PhpOptions { get; private set; } = [];
    [ObservableProperty] public partial PhpOption? Php { get; set; }
    [ObservableProperty] public partial bool CreateDatabase { get; set; }
    [ObservableProperty] public partial SiteTemplate Template { get; set; }
    [ObservableProperty] public partial string StepFolder { get; private set; } = "";

    public string Host => NameValid ? $"{Name.Trim().ToLowerInvariant()}.test" : "<nombre>.test";

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
        Update();
    }

    private string DefaultFolder => Path.Combine(_main.App.Paths.Www, Name.Trim().ToLowerInvariant());

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

        var exists = Directory.Exists(Folder);
        var hasPublic = exists && Directory.Exists(Path.Combine(Folder, "public"));
        FolderHint = !exists ? "No existe: Roost la crea con un index.php."
            : hasPublic ? "Existe y tiene public/: será la raíz web."
            : "Existe: se sirve tal cual.";
        StepFolder = !exists ? $"Crea {Folder} con un index.php" : $"Usa {Folder}{(hasPublic ? " (raíz web: public/)" : "")}";

        BuildPhpOptions(exists ? Recommended(Folder) : null);
        OnPropertyChanged(nameof(Host));
        OnPropertyChanged(nameof(DbName));
        CreateDatabase = CreateDatabase && HasDb;
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

    [RelayCommand]
    private void InstallOther()
    {
        Close();
        _ = _main.NavigateToAsync(_main.Php);
    }

    [RelayCommand]
    private void Create()
    {
        if (!NameValid) return;
        Close(new NewSiteRequest(Name.Trim().ToLowerInvariant(), Folder, Php?.Spec, CreateDatabase && HasDb ? DbName : null));
    }
}
