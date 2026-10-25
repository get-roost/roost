using System.Globalization;
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

/// <summary>Una tarjeta de «Instaladas por Roost».</summary>
public sealed partial class PhpCard(InstalledPhp php) : ObservableObject
{
    public InstalledPhp Php { get; } = php;
    public string Version => Php.Version?.ToString() ?? Php.Name;
    public required string Variant { get; init; }
    public required string Verdict { get; init; }
    public required Tone VerdictTone { get; init; }
    public required bool IsGlobal { get; init; }
    public required Tone DotTone { get; init; }
    public required string Usage { get; init; }
    public required IReadOnlyList<string> Sites { get; init; }
    public required string Branch { get; init; }

    [ObservableProperty]
    public partial bool IsSelected { get; set; }
}

/// <summary>Una extensión de la instalación elegida: se activa o se quita con un clic (salvo las integradas, que van fijas).</summary>
public sealed partial class ExtensionToggle(string name, bool on) : ObservableObject
{
    public string Name { get; } = name;

    [ObservableProperty]
    public partial bool IsOn { get; set; } = on;

    /// <summary>Compilada dentro del binario (los PHP de roost-builds, en Linux): siempre activa y sin clic.</summary>
    public bool BuiltIn { get; init; }

    public bool CanToggle => !BuiltIn;

    public string Tip => BuiltIn ? $"{Name} va integrada en este PHP (compilado estático): siempre activa, no se puede quitar" : IsOn ? $"Quitar {Name}" : $"Activar {Name}";
}

/// <summary>Una rama del catálogo de windows.php.net en la variante elegida (NTS/TS, x64/x86).</summary>
public sealed record PhpCatalogRow(PhpBuild Build, string Support, Tone Tone, bool Installed)
{
    public string Version => Build.Version.ToString();
    public bool Installable => !Installed;
}

/// <summary>Un PHP de fuera (Laragon, XAMPP…) que ha encontrado la auditoría.</summary>
public sealed record ExternalPhpRow(string Version, string Where, string Chip, Tone Tone, PhpBuild? Replacement)
{
    public bool CanInstall => Replacement is not null;
    public string InstallLabel => Replacement is null ? "" : $"Instalar {Replacement.Version}";
}

/// <summary>
/// PHP: las versiones instaladas por Roost (con su veredicto, quién las usa y sus extensiones),
/// el catálogo de windows.php.net y lo que hay instalado fuera de Roost.
/// </summary>
public sealed partial class PhpViewModel : PageViewModel
{
    private IReadOnlyList<PhpBuild>? _catalog;
    private string? _selectedName;

    public PhpViewModel(MainViewModel main) : base(main)
    {
        Subtitle = OsTexts.PhpSubtitle;
        main.Audit.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(AuditState.Findings)) BuildExternal();
        };
    }

    public override string Title => "PHP";

    [ObservableProperty] public partial IReadOnlyList<PhpCard> Installed { get; private set; } = [];
    [ObservableProperty] public partial PhpCard? Selected { get; private set; }
    [ObservableProperty] public partial IReadOnlyList<ExtensionToggle> Extensions { get; private set; } = [];
    [ObservableProperty] public partial string ExtensionSummary { get; private set; } = "";
    [ObservableProperty] public partial string Folder { get; private set; } = "";
    [ObservableProperty] public partial string IniPath { get; private set; } = "";
    [ObservableProperty] public partial string PoolText { get; private set; } = "";
    [ObservableProperty] public partial IReadOnlyList<PhpCatalogRow> Catalog { get; private set; } = [];
    [ObservableProperty] public partial IReadOnlyList<ExternalPhpRow> External { get; private set; } = [];
    [ObservableProperty] public partial string CatalogHint { get; private set; } = OsTexts.PhpCatalogSource;
    [ObservableProperty] public partial string? CatalogError { get; private set; }
    [ObservableProperty] public partial bool LoadingCatalog { get; private set; }
    [ObservableProperty] public partial string? Installing { get; private set; }

    // PHP (oleada 3): la conf.d de la rama, Xdebug y los ajustes rápidos de la instalación elegida.

    /// <summary>Lo que ejecuta php.exe (phpinfo, módulos, la comprobación tras un cambio); los tests ponen uno guionizado.</summary>
    internal ICommandRunner Runner { get; set; } = CommandRunner.Default;

    private PhpConfigurator Configurator => new(Main.App.Paths, Runner);

    [ObservableProperty] public partial string BranchIniText { get; private set; } = "";
    [ObservableProperty] public partial string BranchHint { get; private set; } = "";

    [ObservableProperty] public partial bool XdebugOn { get; private set; }
    [ObservableProperty] public partial bool XdebugAvailable { get; private set; }
    [ObservableProperty] public partial bool XdebugBusy { get; private set; }
    [ObservableProperty] public partial string XdebugText { get; private set; } = "";

    [ObservableProperty] public partial string MemoryLimit { get; set; } = "";
    [ObservableProperty] public partial string UploadMaxFilesize { get; set; } = "";
    [ObservableProperty] public partial string PostMaxSize { get; set; } = "";
    [ObservableProperty] public partial string MaxExecutionTime { get; set; } = "";
    [ObservableProperty] public partial string? SettingsError { get; private set; }
    [ObservableProperty] public partial bool SettingsDirty { get; private set; }

    public bool HasSettingsError => SettingsError is not null;
    public bool CanSaveSettings => SettingsDirty && SettingsError is null;

    private PhpSettings _savedSettings = PhpSettings.Default;
    private string? _settingsFor;

    partial void OnMemoryLimitChanged(string value) => CheckSettings();
    partial void OnUploadMaxFilesizeChanged(string value) => CheckSettings();
    partial void OnPostMaxSizeChanged(string value) => CheckSettings();
    partial void OnMaxExecutionTimeChanged(string value) => CheckSettings();

    partial void OnSettingsErrorChanged(string? value)
    {
        OnPropertyChanged(nameof(HasSettingsError));
        OnPropertyChanged(nameof(CanSaveSettings));
    }

    partial void OnSettingsDirtyChanged(bool value) => OnPropertyChanged(nameof(CanSaveSettings));

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(VariantHint))]
    public partial bool ThreadSafe { get; set; }

    [ObservableProperty]
    public partial bool X86 { get; set; }

    public bool Nts { get => !ThreadSafe; set => ThreadSafe = !value; }
    public bool X64 { get => !X86; set => X86 = !value; }

    public string VariantHint => ThreadSafe
        ? "TS (thread-safe): solo para un mod_php tuyo. Roost sirve siempre con la NTS, con nginx y con Apache."
        : OsTexts.PhpNtsHint;

    public bool HasInstalled => Installed.Count > 0;
    public bool HasSelection => Selected is not null;
    public bool HasExternal => External.Count > 0;
    public bool HasCatalogError => CatalogError is not null;
    public bool IsInstalling => Installing is not null;

    partial void OnThreadSafeChanged(bool value)
    {
        OnPropertyChanged(nameof(Nts));
        BuildCatalog();
    }

    partial void OnX86Changed(bool value)
    {
        OnPropertyChanged(nameof(X64));
        BuildCatalog();
    }

    partial void OnInstalledChanged(IReadOnlyList<PhpCard> value) => OnPropertyChanged(nameof(HasInstalled));
    partial void OnSelectedChanged(PhpCard? value) => OnPropertyChanged(nameof(HasSelection));
    partial void OnExternalChanged(IReadOnlyList<ExternalPhpRow> value) => OnPropertyChanged(nameof(HasExternal));
    partial void OnCatalogErrorChanged(string? value) => OnPropertyChanged(nameof(HasCatalogError));
    partial void OnInstallingChanged(string? value) => OnPropertyChanged(nameof(IsInstalling));

    public override async Task ShownAsync()
    {
        Build();
        if (_catalog is null) await LoadCatalogAsync();
    }

    public override void StackChanged() => Build();

    [RelayCommand]
    private Task RefreshCatalogAsync() => LoadCatalogAsync();

    private async Task LoadCatalogAsync()
    {
        if (LoadingCatalog) return;
        LoadingCatalog = true;
        CatalogError = null;
        try
        {
            _catalog = await PhpCatalog.FetchAsync(Main.App.Http);
            CatalogHint = $"{OsTexts.PhpCatalogSource} · hoy {DateTime.Now:HH:mm}";
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or RoostException or System.Text.Json.JsonException)
        {
            CatalogError = $"No se pudo leer el catálogo de {OsTexts.PhpCatalogSource}: {ex.Message}";
        }
        finally
        {
            LoadingCatalog = false;
        }
        Build();
    }

    // Construcción de la pantalla

    private void Build()
    {
        var paths = Main.App.Paths;
        var status = Main.Stack.Status;
        var installed = new PhpInstaller(paths).ListInstalled();
        var planner = new StackPlanner(paths, ProcessRunner.Default);
        RoostConfig config;
        try { config = RoostConfig.Load(paths.ConfigFile); } catch (RoostException) { config = new RoostConfig(); }
        InstalledPhp? global = null;
        try { global = planner.ResolvePhp(config, installed); } catch (RoostException) { }

        IReadOnlyList<Site> sites = [];
        try { sites = new SiteRegistry(paths).Load(); } catch (Exception ex) when (ex is IOException or RoostException) { }

        var cards = new List<PhpCard>();
        foreach (var php in installed)
        {
            var users = new List<string>();
            if (global is not null && php.Name == global.Name) users.Add("localhost");
            if (global is not null)
            {
                foreach (var site in sites)
                {
                    try
                    {
                        if (planner.ResolveSitePhp(site, global, installed).Name == php.Name) users.Add(site.Name);
                    }
                    catch (RoostException) { }
                }
            }

            var finding = php.Version is { } v ? Main.Audit.Evaluate(ComponentKind.Php, v, php.Executable) : null;
            var branch = HomeViewModel.Branch(php.Name);
            var poolRunning = status?.Running == true && status.Services.Any(s => s.Name.StartsWith($"php-{branch}-", StringComparison.Ordinal) && s.State == "Running")
                              && (status.Php == php.Name || status.Sites.Any(s => s.Php == php.Name));
            cards.Add(new PhpCard(php)
            {
                Variant = Variant(php),
                Verdict = finding is null ? "desconocida" : finding.VerdictLabel,
                VerdictTone = finding is null ? Tone.Warn : AuditState.ToneOf(finding.Color),
                IsGlobal = global?.Name == php.Name,
                DotTone = poolRunning ? Tone.Ok : Tone.Off,
                Usage = users.Count == 0 ? "Ningún sitio la usa" : string.Join(", ", users.Select(u => u == "localhost" || u.Contains('.') ? u : u + ".test")),
                Sites = users.Where(u => u != "localhost").ToList(),
                Branch = branch,
            });
        }

        Installed = cards;
        var selected = cards.FirstOrDefault(c => c.Php.Name == _selectedName) ?? cards.FirstOrDefault(c => c.IsGlobal) ?? cards.FirstOrDefault();
        Select(selected, planner, status);
        BuildCatalog();
        BuildExternal();
    }

    private void Select(PhpCard? card, StackPlanner planner, Roost.Daemon.StatusResponse? status)
    {
        foreach (var c in Installed) c.IsSelected = c == card;
        Selected = card;
        _selectedName = card?.Php.Name;
        if (card is null)
        {
            Extensions = [];
            return;
        }

        Folder = card.Php.Directory;
        IniPath = planner.PhpIniPath(card.Php);
        var ports = status?.Services.Where(s => s.Name.StartsWith($"php-{card.Branch}-", StringComparison.Ordinal)).ToList() ?? [];
        var inUse = card.IsGlobal || card.Sites.Count > 0;
        PoolText = !inUse ? "Sin pool: ningún sitio la usa"
            : ports.Count > 0 && card.DotTone == Tone.Ok
                ? $"{ports.Count} procesos · {ports.Min(p => p.Port)}–{ports.Max(p => p.Port)} · en marcha"
                : "Se arranca con el stack";

        // Xdebug tiene su interruptor: no va entre las extensiones aunque haya un php_xdebug.dll en ext/.
        // Las integradas en el binario (los PHP de roost-builds, en Linux) salen fijas: siempre activas y sin clic.
        Extensions = Configurator.Extensions(card.Php)
            .Where(e => e.Name != PhpIni.Xdebug)
            .Select(e => new ExtensionToggle(e.Name, e.On) { BuiltIn = e.BuiltIn }).ToList();
        var builtIn = Extensions.Count(e => e.BuiltIn);
        ExtensionSummary = $"{Extensions.Count(e => e.IsOn)} de {Extensions.Count} activas"
            + (builtIn > 0 ? $" ({builtIn} integradas en el binario, que no se pueden quitar)" : "");

        BranchIniText = Path.Combine("conf.d", card.Branch, PhpIni.UserFragmentName);
        BranchHint = $"solo la {card.Branch}: va después y manda";
        BuildXdebug(card);
        // Los campos solo se recargan al cambiar de versión: un aviso del stack no borra lo tecleado.
        if (_settingsFor != card.Php.Name) LoadSettings(card);
    }

    private void BuildXdebug(PhpCard card)
    {
        var php = card.Php;
        var configurator = Configurator;
        XdebugOn = configurator.Choice(php).On.Contains(PhpIni.Xdebug);
        XdebugAvailable = XdebugCatalog.WhyNot(php) is null;
        var build = XdebugCatalog.For(php);   // null en Linux: el xdebug.so viene dentro de cada PHP
        var dll = new XdebugInstaller(Main.App.Paths).Locate(php);
        var bundled = XdebugCatalog.IsBundled(php);
        var version = bundled ? XdebugInstaller.BundledVersion(php) ?? "" : dll is null ? build?.Version : Path.GetFileName(Path.GetDirectoryName(dll));
        var label = version is { Length: > 0 } ? $"Xdebug {version}" : "Xdebug";
        XdebugText = !XdebugAvailable
            ? bundled ? $"Este PHP no trae ext/{XdebugInstaller.SharedObjectName}: vuelve a instalarlo desde el catálogo (los PHP de roost-builds lo llevan dentro)."
            : php.ThreadSafe ? "Solo en las NTS, que son las que sirve Roost."
            : !php.Name.EndsWith("-x64", StringComparison.OrdinalIgnoreCase) ? "Solo en las de 64 bits."
            : $"Roost no tiene Xdebug para PHP {card.Branch}."
            : XdebugOn && dll is null ? "Activado, pero falta su DLL: apágalo y vuelve a encenderlo para bajarla."
            : XdebugOn ? $"{label} · depura la petición que trae XDEBUG_TRIGGER · el editor escucha en 127.0.0.1:9003"
            : bundled ? $"Apagado ({label} viene dentro de este PHP, en ext/). Encenderlo reinicia solo el pool de la {card.Branch}."
            : dll is not null ? $"Apagado ({label} ya está en {Path.Combine("bin", "xdebug")}). Encenderlo reinicia solo el pool de la {card.Branch}."
            : $"Apagado. La primera vez se baja Xdebug {build!.Version} de xdebug.org ({build.Size / 1024} KB, con su SHA-256 comprobado).";
    }

    private void LoadSettings(PhpCard card)
    {
        _settingsFor = card.Php.Name;
        _savedSettings = Configurator.Settings(card.Php);
        ShowSettings(_savedSettings);
    }

    private void ShowSettings(PhpSettings settings)
    {
        MemoryLimit = settings.MemoryLimit;
        UploadMaxFilesize = settings.UploadMaxFilesize;
        PostMaxSize = settings.PostMaxSize;
        MaxExecutionTime = settings.MaxExecutionTime.ToString(CultureInfo.InvariantCulture);
        CheckSettings();
    }

    /// <summary>Lo tecleado, o null si max_execution_time no es un número.</summary>
    private PhpSettings? TypedSettings() =>
        int.TryParse(MaxExecutionTime.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var seconds)
            ? new PhpSettings(MemoryLimit.Trim(), UploadMaxFilesize.Trim(), PostMaxSize.Trim(), seconds)
            : null;

    private void CheckSettings()
    {
        var typed = TypedSettings();
        SettingsError = typed is null ? "max_execution_time: pon segundos (0 es sin límite)." : typed.Validate();
        SettingsDirty = typed is null || typed.Normalized() != _savedSettings.Normalized();
    }

    private void BuildCatalog()
    {
        if (_catalog is null)
        {
            Catalog = [];
            return;
        }

        var installedNames = Installed.Select(c => c.Php.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var arch = X86 ? "x86" : "x64";
        Catalog = _catalog
            .Where(b => b.ThreadSafe == ThreadSafe && b.Architecture.Equals(arch, StringComparison.OrdinalIgnoreCase))
            .GroupBy(b => b.Branch)
            .Select(g => g.First())
            .OrderByDescending(b => b.Version)
            .Select(b =>
            {
                var (support, tone) = Support(b.Branch);
                return new PhpCatalogRow(b, support, tone, installedNames.Contains(b.InstallName));
            })
            .ToList();
    }

    private void BuildExternal()
    {
        var latest = _catalog?
            .Where(b => !b.ThreadSafe && b.Architecture == "x64")
            .GroupBy(b => b.Branch)
            .ToDictionary(g => g.Key, g => g.First());
        var installedBranches = Installed.Select(c => c.Branch).ToHashSet();

        External = Main.Audit.Findings
            .Where(f => f.Component.Kind == ComponentKind.Php && f.Component.Source != "Roost")
            .Select(f =>
            {
                var c = f.Component;
                var branch = $"{c.Version.Major}.{c.Version.Minor}";
                var ts = c.Extra.TryGetValue("threadSafety", out var t) ? t.ToUpperInvariant() : null;
                var version = string.Join(' ', new[] { c.Version.ToString(), ts, c.Architecture }.Where(x => !string.IsNullOrEmpty(x)));
                PhpBuild? replacement = null;
                if (latest is not null && latest.TryGetValue(branch, out var build) && !installedBranches.Contains(branch) && build.Version > c.Version)
                    replacement = build;
                return new ExternalPhpRow(version, Path.GetDirectoryName(c.ExecutablePath) ?? c.ExecutablePath,
                    AuditState.ChipText(f), AuditState.ToneOf(f.Color), replacement);
            })
            .ToList();
    }

    private (string Text, Tone Tone) Support(string branch)
    {
        var es = Roost.Desktop.Services.Es.Culture;
        if (!Main.Audit.Dataset.Products.TryGetValue("php", out var product) || !product.Branches.TryGetValue(branch, out var info))
            return ("Sin datos de soporte", Tone.Warn);
        var today = DateOnly.FromDateTime(DateTime.Today);
        if (info.Status == "eol" || info.Eol is { } eol && eol <= today)
            return (info.Eol is { } e ? $"EOL desde {e.ToString("d-MMM-yyyy", es)}" : "EOL", Tone.Bad);
        if (info.Status == "security")
            return ($"Solo seguridad, hasta {(info.SecurityUntil ?? info.Eol)?.ToString("d-MMM-yyyy", es)}", Tone.Warn);
        return (info.Eol is { } end ? $"Soporte activo hasta {end.Year}" : "Soporte activo", Tone.Ok);
    }

    private string Variant(InstalledPhp php)
    {
        var parts = php.Name.Split('-');
        var label = parts.Length == 3 ? $"{parts[1].ToUpperInvariant()} · {parts[2]}" : php.Name;
        var compiler = _catalog?.FirstOrDefault(b => b.InstallName.Equals(php.Name, StringComparison.OrdinalIgnoreCase))?.Compiler;
        return compiler is null ? label : $"{label} · {compiler}";
    }

    // Acciones

    [RelayCommand]
    private void Pick(PhpCard card) => Select(card, new StackPlanner(Main.App.Paths, ProcessRunner.Default), Main.Stack.Status);

    [RelayCommand]
    private Task UseAsGlobalAsync()
    {
        if (Selected is not { } card) return Task.CompletedTask;
        // La rama si es la mejor de su rama (así entran solas las versiones nuevas); si no, esa instalación.
        var installed = Installed.Select(c => c.Php).ToList();
        var best = new StackPlanner(Main.App.Paths, ProcessRunner.Default).ResolvePhp(new RoostConfig { PhpVersion = card.Branch }, installed);
        return Main.UsePhpAsync(best.Name == card.Php.Name ? card.Branch : card.Php.Name);
    }

    [RelayCommand]
    private Task OpenTerminalAsync() => Main.RunAsync("No se pudo abrir la terminal", () =>
    {
        if (Selected is { } card) Shell.OpenTerminal(Main.App.Paths, card.Php.Directory);
        return Task.CompletedTask;
    });

    [RelayCommand]
    private Task EditUserIniAsync() => Main.RunAsync("No se pudo abrir el fichero", () =>
    {
        Shell.OpenFile(new StackPlanner(Main.App.Paths, ProcessRunner.Default).EnsureUserIni());
        return Task.CompletedTask;
    });

    [RelayCommand]
    private Task OpenFolderAsync() => Main.RunAsync("No se pudo abrir la carpeta", () =>
    {
        if (Selected is { } card) Shell.OpenFolder(card.Php.Directory);
        return Task.CompletedTask;
    });

    /// <summary>
    /// Activa o quita una extensión: rehace el php.ini de esa versión, comprueba con php -v que la
    /// carga (si no, la deja como estaba y lo dice) y reinicia solo su pool.
    /// </summary>
    [RelayCommand]
    private Task ToggleExtensionAsync(ExtensionToggle extension) => Main.RunAsync($"No se pudo cambiar {extension.Name}", async () =>
    {
        if (Selected is not { } card) return;
        var check = await Configurator.SetExtensionAsync(card.Php, extension.Name, !extension.IsOn);
        extension.IsOn = !extension.IsOn;
        ExtensionSummary = $"{Extensions.Count(e => e.IsOn)} de {Extensions.Count} activas";
        await RestartPoolAsync(card);
        WarnAboutStartup(check);
    });

    // PHP (oleada 3)

    /// <summary>Si el pool de esa versión está en marcha, lo reinicia (solo ese: el resto del stack sigue).</summary>
    private async Task RestartPoolAsync(PhpCard card)
    {
        if (card.DotTone != Tone.Ok) return;
        var response = await Main.App.Daemon.RestartAsync($"php-{card.Branch}");
        if (!response.Ok) throw new RoostException(response.Error ?? "El daemon no ha dicho por qué.");
        await Main.Stack.RefreshAsync();
    }

    private void WarnAboutStartup(PhpCheck check)
    {
        if (check.Warnings.Count > 0) Main.Flash($"Aviso de PHP al arrancar: {check.Warnings[0]}");
    }

    [RelayCommand]
    private Task EditBranchIniAsync() => Main.RunAsync("No se pudo abrir el fichero", () =>
    {
        if (Selected is { } card) Shell.OpenFile(new StackPlanner(Main.App.Paths, ProcessRunner.Default).EnsureBranchUserIni(card.Php));
        return Task.CompletedTask;
    });

    /// <summary>«Ver»: el php.ini generado, tal cual, de solo lectura (se rehace antes para que esté al día).</summary>
    [RelayCommand]
    private async Task ShowIniAsync()
    {
        if (Selected is not { } card) return;
        var planner = new StackPlanner(Main.App.Paths, ProcessRunner.Default);
        var ini = planner.PhpIniPath(card.Php);
        var dialog = new PhpTextDialogViewModel(
            $"php.ini de PHP {card.Version}", "Lo genera Roost en cada roost up: no se edita. Tus ajustes van en conf.d.", ini,
            _ => Task.Run(() =>
            {
                planner.WritePhpIni(card.Php);
                return File.ReadAllText(ini);
            }),
            text => Main.CopyAsync(text, "php.ini copiado"),
            Shell.OpenFolder);
        var loading = dialog.LoadAsync();
        await Main.ShowDialogAsync(dialog);
        await loading;
    }

    [RelayCommand]
    private Task PhpInfoAsync() => ShowPhpOutputAsync(modules: false);

    [RelayCommand]
    private Task ModulesAsync() => ShowPhpOutputAsync(modules: true);

    /// <summary>phpinfo (<c>php -i</c>) o los módulos (<c>php -m</c>) en un diálogo con búsqueda, fuera del hilo de la UI.</summary>
    private async Task ShowPhpOutputAsync(bool modules)
    {
        if (Selected is not { } card) return;
        var dialog = PhpTextDialogViewModel.ForPhp(card.Php, Configurator, Runner, modules,
            text => Main.CopyAsync(text, modules ? "Módulos copiados" : "phpinfo copiado"));
        var loading = dialog.LoadAsync();
        await Main.ShowDialogAsync(dialog);
        await loading;
    }

    /// <summary>
    /// El interruptor de Xdebug. La primera vez baja la DLL de su rama con el diálogo Instalando
    /// (verificada); luego la activa en el php.ini, comprueba con php -v que carga y reinicia solo su pool.
    /// </summary>
    [RelayCommand]
    private async Task ToggleXdebugAsync()
    {
        if (Selected is not { } card || XdebugBusy) return;
        var wanted = !XdebugOn;
        XdebugBusy = true;
        try
        {
            if (wanted && !await EnsureXdebugAsync(card)) return;
            await Main.RunAsync(wanted ? "No se pudo activar Xdebug" : "No se pudo apagar Xdebug", async () =>
            {
                var check = await Configurator.SetXdebugAsync(card.Php, wanted);
                await RestartPoolAsync(card);
                if (wanted)
                    Main.Flash(check.XdebugVersion is { } v ? $"Xdebug {v} activado en PHP {card.Version}" : $"Xdebug activado en PHP {card.Version} (no se ha podido comprobar con php -v)");
                else
                    Main.Flash($"Xdebug apagado en PHP {card.Version}");
                WarnAboutStartup(check);
            });
        }
        finally
        {
            XdebugBusy = false;
            if (Selected is { } now) BuildXdebug(now);
            OnPropertyChanged(nameof(XdebugOn));   // el interruptor vuelve a lo que de verdad hay si se canceló o falló
        }
    }

    /// <summary>La DLL de Xdebug de la rama: si no está, la instala con el diálogo Instalando. false si no se ha instalado.</summary>
    private async Task<bool> EnsureXdebugAsync(PhpCard card)
    {
        if (XdebugCatalog.WhyNot(card.Php) is { } why)
        {
            await Main.InfoAsync("Xdebug no está disponible para esta versión", why, Tone.Warn);
            return false;
        }
        // En Linux no hay nada que bajar (For es null): el xdebug.so va dentro de cada PHP y WhyNot ya ha dicho que está.
        if (XdebugCatalog.For(card.Php) is not { } build) return true;
        var installer = new XdebugInstaller(Main.App.Paths, Main.App.Http);
        if (installer.Find(build) is not null) return true;
        if (Installing is not null)
        {
            await Main.InfoAsync("Ya hay una instalación en curso", $"Espera a que termine {Installing}.");
            return false;
        }

        var steps = new List<InstallStep>
        {
            new($"{build.Label} para PHP {build.PhpBranch}", $"· {build.FileName}"),
            new("Descargando de xdebug.org", null),
            new("Verificar el SHA-256", "· fijado en Roost, antes de usarla"),
            new($"Dejarla en {Path.GetDirectoryName(installer.DllPath(build))}", null),
        };
        var dialog = new InstallDialogViewModel(
            $"Instalando {build.Label}",
            $"Para PHP {build.PhpBranch} · NTS · x64 · {build.Compiler} · desde xdebug.org",
            build.Size,
            steps,
            async (d, cancel) =>
            {
                d.Step(1, "Descargando");
                var bytes = new Progress<long>(b =>
                {
                    d.Downloaded(b);
                    if (b >= build.Size) d.Step(2, "Verificando");
                });
                await installer.InstallAsync(build, bytes, cancel);
                d.Step(3, "Instalando");
            });
        await RunInstallAsync(dialog, build.Label, Path.GetDirectoryName(installer.DllPath(build)));
        return !dialog.Failed && await dialog.Finished && installer.Find(build) is not null;
    }

    /// <summary>Copia un <c>.vscode/launch.json</c> para PHP Debug con esta versión (ver README).</summary>
    [RelayCommand]
    private Task CopyLaunchJsonAsync() => Main.RunAsync("No se pudo copiar el launch.json", async () =>
    {
        if (Selected is not { } card) return;
        var json = await Task.Run(() => Configurator.VsCodeLaunchJson(card.Php));
        await Main.CopyAsync(json, "launch.json copiado: pégalo en .vscode/launch.json del proyecto");
    });

    /// <summary>Guarda los ajustes rápidos (validados), rehace el php.ini y reinicia solo el pool.</summary>
    [RelayCommand]
    private Task SaveSettingsAsync() => Main.RunAsync("No se pudieron guardar los ajustes", async () =>
    {
        if (Selected is not { } card || TypedSettings() is not { } typed) return;
        if (typed.Validate() is { } error)
        {
            SettingsError = error;
            return;
        }
        await Task.Run(() => Configurator.SetSettings(card.Php, typed));
        LoadSettings(card);
        await RestartPoolAsync(card);
        Main.Flash($"Ajustes guardados en el php.ini de PHP {card.Version}");
    });

    /// <summary>«De serie»: pone en los campos los valores de Roost (se guardan con «Guardar»).</summary>
    [RelayCommand]
    private void DefaultSettings() => ShowSettings(PhpSettings.Default);

    [RelayCommand]
    private Task InstallAsync(PhpCatalogRow row) => InstallBuildAsync(row.Build);

    /// <summary>Instala la última NTS x64 de una rama (desde la Auditoría: «Instalar 8.3.36»).</summary>
    public async Task InstallLatestAsync(string branch)
    {
        await Main.NavigateToAsync(this);
        if (_catalog is null) await LoadCatalogAsync();
        var build = _catalog?.FirstOrDefault(b => b.Branch == branch && !b.ThreadSafe && b.Architecture == "x64");
        if (build is null)
        {
            await Main.InfoAsync($"La rama {branch} no está en el catálogo", $"{OsTexts.PhpCatalogSource} ya no la publica: elige otra versión de la lista.", Tone.Warn);
            return;
        }
        if (Installed.Any(c => c.Php.Name == build.InstallName))
        {
            await Main.InfoAsync($"PHP {build.Version} ya está instalado", "Lo de fuera de Roost no se toca: úsalo en tus sitios desde aquí o desde Sitios.");
            return;
        }
        await InstallBuildAsync(build);
    }

    [RelayCommand]
    private Task InstallReplacementAsync(ExternalPhpRow row) => row.Replacement is null ? Task.CompletedTask : InstallBuildAsync(row.Replacement);

    /// <summary>Instala un build del catálogo con el diálogo Instalando (también desde la bienvenida).</summary>
    public async Task InstallBuildAsync(PhpBuild build)
    {
        if (Installing is not null)
        {
            await Main.InfoAsync("Ya hay una instalación en curso", $"Espera a que termine {Installing}.");
            return;
        }
        // Sin el VC++ Redistributable, php.exe no arranca: avisar antes, con el enlace.
        if (!await VcRedistGuard.ConfirmAsync(Main, $"PHP {build.Version}", forApache: false)) return;

        var paths = Main.App.Paths;
        var installer = new PhpInstaller(paths, Main.App.Http);
        var target = installer.InstallDirectory(build);
        var host = build.Url.Host;
        var steps = new List<InstallStep>
        {
            new("Catálogo leído", $"· {build.Version} es la última de la rama {build.Branch}"),
            new("Descargando el zip oficial", null),
            new("Verificar el SHA-256", "· antes de extraer nada"),
            new($"Extraer en {target}", null),
            new("Comprobar que arranca", "· php -v"),
        };

        // Sitios que fijan una versión exacta más vieja de esta rama: se les ofrece pasar a la nueva.
        var pinned = SafeSites().Where(s => s.Php is { } p && ComponentVersion.TryParse(p, out var pv)
                                            && $"{pv.Major}.{pv.Minor}" == build.Branch && pv < build.Version).ToList();
        var dialog = new InstallDialogViewModel(
            $"Instalando PHP {build.Version}",
            $"{(build.ThreadSafe ? "TS" : "NTS")} · {build.Architecture} · {build.Compiler} · desde {host}",
            InstallDialogViewModel.ParseSize(build.SizeText),
            steps,
            async (d, cancel) =>
            {
                d.Step(1, "Descargando");
                var bytes = new Progress<long>(b =>
                {
                    d.Downloaded(b);
                    if (d.TotalBytes is { } total && b >= total) d.Step(2, "Verificando");
                });
                var php = await installer.InstallAsync(build, bytes, cancel);
                d.Step(4, "Comprobando");
                var output = await Task.Run(() => ProcessRunner.Default.Run(php.Executable, "-v", TimeSpan.FromSeconds(15)), cancel);
                if (output is null || !output.Contains("PHP " + build.Version, StringComparison.Ordinal))
                    throw new RoostException($"PHP {build.Version} se ha instalado, pero php -v no responde como se esperaba: {output?.Trim() ?? "sin salida"}");
                if (d.OptionChecked)
                {
                    var registry = new SiteRegistry(paths);
                    foreach (var site in pinned) registry.Upsert(site with { Php = build.Branch });
                }
            })
        {
            OptionText = pinned.Count == 0 ? null
                : $"Al terminar, pasar a {build.Version} {(pinned.Count == 1 ? "el sitio que usa" : "los sitios que usan")} PHP {build.Branch} ({string.Join(", ", pinned.Select(s => s.Host))})",
            OptionChecked = pinned.Count > 0,
        };

        await RunInstallAsync(dialog, $"PHP {build.Version}");
        if (dialog.OptionChecked && pinned.Count > 0 && await dialog.Finished) await Main.ApplyAsync();
    }

    /// <summary>Enseña el diálogo de instalación y espera al resultado, aunque se haya pasado a segundo plano.</summary>
    /// <param name="where">Dónde queda, para el aviso si se pasó a segundo plano; null es la carpeta de los PHP.</param>
    private async Task RunInstallAsync(InstallDialogViewModel dialog, string what, string? where = null)
    {
        Installing = what;
        dialog.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(InstallDialogViewModel.Percent) or nameof(InstallDialogViewModel.Phase) && !dialog.Done)
                Installing = dialog.ShortStatus;
        };
        var run = dialog.RunAsync();
        var shown = await Main.ShowDialogAsync(dialog);
        await run;
        Installing = null;
        Main.NotifyConfigChanged();
        Build();
        // Si se pasó a segundo plano y falló, el error no se ha visto aún.
        if (shown is null && dialog.Failed)
            await Main.InfoAsync($"No se instaló {what}", dialog.Error!, Tone.Bad);
        else if (shown is null && !dialog.Failed)
            await Main.InfoAsync($"{what} instalado", $"Ya está en {where ?? Main.App.Paths.PhpBin}.", Tone.Ok);
    }

    [RelayCommand]
    private async Task RemoveAsync()
    {
        if (Selected is not { } card) return;
        if (card.IsGlobal)
        {
            await Main.InfoAsync($"PHP {card.Version} es el PHP global",
                "Sirve localhost y los sitios sin PHP propio. Elige otra versión como global y luego quítala.", Tone.Warn);
            return;
        }

        var others = Installed.Where(c => c != card).ToList();
        if (card.Sites.Count > 0)
        {
            var dialog = new RemovePhpDialogViewModel(card, others.First(c => c.IsGlobal), others, SafeSites().Where(s => card.Sites.Contains(s.Name)).ToList());
            if (await Main.ShowDialogAsync(dialog) is not true) return;
            await Main.RunAsync($"No se pudo quitar PHP {card.Version}", async () =>
            {
                var registry = new SiteRegistry(Main.App.Paths);
                foreach (var site in dialog.Sites) registry.Upsert(site with { Php = dialog.TargetSpec });
                // Primero se aplica (para que ningún php-cgi tenga abierta la carpeta) y luego se quita.
                // Si no se pudo aplicar, no se borra: los sitios ya están movidos y basta con reintentar.
                if (!await Main.ApplyAsync()) return;
                new PhpInstaller(Main.App.Paths).Remove(card.Php);
            });
        }
        else
        {
            if (!await Main.ConfirmAsync($"Quitar PHP {card.Version}",
                    $"Se borra solo su carpeta ({card.Php.Directory}). Tus ajustes de conf.d se quedan.", "Quitar", danger: true))
                return;
            await Main.RunAsync($"No se pudo quitar PHP {card.Version}", () =>
            {
                new PhpInstaller(Main.App.Paths).Remove(card.Php);
                return Task.CompletedTask;
            });
        }

        _selectedName = null;
        Main.NotifyConfigChanged();
        Build();
    }

    private IReadOnlyList<Site> SafeSites()
    {
        try { return new SiteRegistry(Main.App.Paths).Load(); }
        catch (Exception ex) when (ex is IOException or RoostException) { return []; }
    }
}
