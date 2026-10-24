using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Roost.Core;
using Roost.Core.Composer;
using Roost.Core.Scanning;
using Roost.Core.Shims;
using Roost.Core.Sites;
using Roost.Desktop.Controls;

namespace Roost.Desktop.ViewModels;

/// <summary>
/// El bloque Composer del panel del sitio, si la carpeta tiene composer.json: «Instalar
/// dependencias», «Actualizar» y «Añadir paquete…», con el PHP del sitio y el php.ini de Roost
/// (<see cref="ComposerTool"/>), en el diálogo de salida en vivo. La primera vez instala Composer
/// con el diálogo Instalando, como nginx o PHP.
/// </summary>
public sealed partial class SitesViewModel
{
    /// <summary>Quien ejecuta Composer: el de verdad; los tests ponen uno guionizado.</summary>
    internal ICommandRunner Runner { get; set; } = CommandRunner.Default;

    /// <summary>Lo que se sabe del proyecto del sitio elegido (Laravel, si tiene composer.json, si le falta vendor/).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasComposer), nameof(ComposerNeedsInstall), nameof(ComposerChip), nameof(ComposerChipTone), nameof(HasFramework), nameof(FrameworkText))]
    public partial ProjectKind? Project { get; private set; }

    [ObservableProperty] public partial string ComposerText { get; private set; } = "";

    /// <summary>Composer está trabajando en este sitio (o instalándose): los botones esperan.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ComposerIdle))]
    public partial bool ComposerBusy { get; private set; }

    public bool ComposerIdle => !ComposerBusy;

    public bool HasComposer => Project?.HasComposerJson == true;

    /// <summary>composer.json sin vendor/: el aviso discreto y el botón de instalar en primario.</summary>
    public bool ComposerNeedsInstall => Project?.NeedsComposerInstall == true;

    public string ComposerChip => Project is not { HasComposerJson: true } p ? "" : p.HasVendor ? "vendor/ instalado" : "falta vendor/";

    public Tone ComposerChipTone => ComposerNeedsInstall ? Tone.Warn : Tone.Ok;

    public bool HasFramework => Project?.Framework is not null;

    public string FrameworkText => Project?.Framework is { } framework ? $"Proyecto {framework}" : "";

    private void PickComposer(Site site)
    {
        Project = Directory.Exists(site.Folder) ? ProjectKind.Detect(site.Folder) : null;
        if (Project is not { HasComposerJson: true })
        {
            ComposerText = "";
            return;
        }

        var paths = Main.App.Paths;
        string php;
        try { php = $"PHP {ComposerTool.PhpFor(paths, site.Folder).Php.Version?.ToString() ?? "?"}"; }
        catch (RoostException) { php = "sin PHP instalado"; }
        var composer = ComposerTool.Locate(paths);
        ComposerText = composer is not null && ComposerTool.IsCurrentInstalled(paths)
            ? $"{php} · {composer.Label}"
            : $"{php} · {ComposerCatalog.Stable.Label} se instala la primera vez (verificado, de getcomposer.org)";
    }

    [RelayCommand]
    private Task ComposerInstallAsync() => RunComposerAsync(ComposerAction.Install, null, false);

    [RelayCommand]
    private Task ComposerUpdateAsync() => RunComposerAsync(ComposerAction.Update, null, false);

    [RelayCommand]
    private async Task ComposerRequireAsync()
    {
        if (Selected is not { } row || ComposerBusy) return;
        string php;
        try { php = ComposerTool.PhpFor(Main.App.Paths, row.Site.Folder).Php.Version?.ToString() ?? "?"; }
        catch (RoostException ex)
        {
            await Main.InfoAsync("No se puede ejecutar Composer", ex.Message, Tone.Bad);
            return;
        }
        if (await Main.ShowDialogAsync(new ComposerRequireDialogViewModel(row.Host, php)) is not ComposerRequireRequest request) return;
        await RunComposerAsync(ComposerAction.Require, request.Package, request.Dev);
    }

    /// <summary>Composer en la carpeta del sitio, en el diálogo de salida en vivo. Al terminar, el panel vuelve a mirar vendor/.</summary>
    private async Task RunComposerAsync(ComposerAction action, string? package, bool dev)
    {
        if (Selected is not { } row || ComposerBusy) return;
        ComposerBusy = true;
        try
        {
            var paths = Main.App.Paths;
            IReadOnlyList<string> arguments;
            Roost.Core.Shims.PhpChoice php;
            try
            {
                if (!Directory.Exists(row.Site.Folder))
                    throw new RoostException($"La carpeta del proyecto ya no existe ({row.Site.Folder}).");
                arguments = ComposerTool.Arguments(action, package, dev);
                php = ComposerTool.PhpFor(paths, row.Site.Folder);
                ComposerTool.CheckPhp(php.Php);
            }
            catch (RoostException ex)
            {
                await Main.InfoAsync("No se puede ejecutar Composer", ex.Message, Tone.Bad);
                return;
            }

            if (await EnsureComposerAsync() is not { } composer) return;

            CommandSpec spec;
            try { spec = ComposerTool.Command(paths, php.Php, composer, arguments, row.Site.Folder); }
            catch (Exception ex) when (ex is RoostException or IOException or UnauthorizedAccessException)
            {
                await Main.InfoAsync("No se puede ejecutar Composer", ex.Message, Tone.Bad);
                return;
            }

            var display = ComposerTool.Display(arguments);
            var dialog = new CommandOutputDialogViewModel(
                $"{display} · {row.Host}",
                $"PHP {php.Php.Version?.ToString() ?? php.Php.Name} · {composer.Label} · {row.Site.Folder}",
                spec, Runner, text => Main.CopyAsync(text, "Salida de Composer copiada"))
            {
                CommandText = display,
                Note = "Composer ejecuta los scripts del composer.json del proyecto (post-install-cmd, post-autoload-dump…) y los plugins que permita: úsalo con proyectos en los que confíes.",
            };
            var run = dialog.RunAsync();
            await Main.ShowDialogAsync(dialog);
            await run;   // mientras corre, el diálogo no se puede cerrar: aquí ya ha terminado

            if (Selected?.Site.Name == row.Site.Name) PickComposer(row.Site);
            if (dialog.Succeeded) Main.Flash($"{display} terminado en {row.Host}");
        }
        finally
        {
            ComposerBusy = false;
        }
    }

    /// <summary>
    /// El Composer de esta versión de Roost; la primera vez lo instala con el diálogo Instalando
    /// (descarga verificada). Si los shims están puestos, añade el de composer. null si no se instaló.
    /// </summary>
    private async Task<InstalledComposer?> EnsureComposerAsync()
    {
        var paths = Main.App.Paths;
        var build = ComposerCatalog.Stable;
        var installer = new ComposerInstaller(paths, Main.App.Http);
        if (installer.Find(build) is { } found) return found;

        InstalledComposer? installed = null;
        var steps = new List<InstallStep>
        {
            new("Composer fijado en Roost", $"· {build.Version}, para PHP {build.MinPhp} o más nuevo"),
            new("Descargando composer.phar", $"· de {build.Url.Host}"),
            new("Verificar el SHA-256", "· antes de instalar nada"),
            new($"Instalar en {installer.InstallDirectory(build)}", null),
        };
        var dialog = new InstallDialogViewModel($"Instalando {build.Label}", $"La primera vez que se usa · desde {build.Url.Host}", build.Size, steps,
            async (d, cancel) =>
            {
                d.Step(1, "Descargando");
                var bytes = new Progress<long>(b =>
                {
                    d.Downloaded(b);
                    if (d.TotalBytes is { } total && b >= total) d.Step(2, "Verificando");
                });
                installed = await installer.InstallAsync(build, bytes, cancel);
                d.Step(3, "Instalando");
            });
        var run = dialog.RunAsync();
        var shown = await Main.ShowDialogAsync(dialog);
        await run;
        if (!await dialog.Finished)
        {
            if (shown is null && dialog.Failed) await Main.InfoAsync($"No se instaló {build.Label}", dialog.Error!, Tone.Bad);
            return null;
        }

        // Con los shims puestos, composer en la terminal también (los de antes solo traían php).
        try { new ShimInstaller(paths).AddMissing(AppContext.BaseDirectory); }
        catch (RoostException) { /* la terminal sigue sin composer; la app no lo necesita */ }
        return installed;
    }
}
