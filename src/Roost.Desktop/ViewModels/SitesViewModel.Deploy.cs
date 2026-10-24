using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Roost.Core;
using Roost.Core.Composer;
using Roost.Core.Deploy;
using Roost.Core.Scanning;
using Roost.Core.Sites;
using Roost.Desktop.Controls;
using Roost.Desktop.Services;

namespace Roost.Desktop.ViewModels;

/// <summary>
/// Pasos y plantillas (oleada 4): los pasos de instalación de un sitio (tras clonarlo, o desde
/// «Pasos de instalación…» en su panel) y Nuevo sitio con una plantilla (Laravel, WordPress,
/// Symfony). Todo en el diálogo <see cref="DeployStepsDialogViewModel"/>, con los pasos de
/// <see cref="DeployPlan"/> ejecutados por <see cref="DeployRunner"/> sobre <see cref="Runner"/>.
/// </summary>
public sealed partial class SitesViewModel
{
    /// <summary>Quien mira y crea la base de datos de los pasos (el cliente de verdad); los tests ponen uno falso.</summary>
    internal IProcessRunner DbRunner { get; set; } = ProcessRunner.Default;

    /// <summary>Los pasos (o una plantilla) están en marcha: el botón espera.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DeployIdle))]
    public partial bool DeployBusy { get; private set; }

    public bool DeployIdle => !DeployBusy;

    /// <summary>El panel ofrece «Pasos de instalación…»: hay un proyecto reconocido (Laravel, Symfony, WordPress o con composer.json).</summary>
    public bool HasDeploySteps => Project is { } p && (p.Type != ProjectType.Plain || p.HasComposerJson);

    /// <summary>«Laravel 13», «WordPress», «Composer»: lo que se reconoce del proyecto.</summary>
    public string DeployProjectText => Project?.FrameworkLabel ?? (Project?.HasComposerJson == true ? "Composer" : "");

    /// <summary>Lo que hacen los pasos de este tipo de proyecto, para el panel.</summary>
    public string DeployStepsText => Project?.Type switch
    {
        ProjectType.Laravel => "composer install, el .env con la base de datos de Roost, la clave, las migraciones y storage:link. Tú eliges cuáles.",
        ProjectType.Symfony => "composer install, el .env.local con la base de datos de Roost y las migraciones de Doctrine. Tú eliges cuáles.",
        ProjectType.WordPress => "El wp-config.php con la base de datos de Roost y claves nuevas, y que se fíe de los certificados .test.",
        _ => "composer install con el PHP del sitio. Tú eliges si lo haces.",
    };

    partial void OnProjectChanged(ProjectKind? value)
    {
        OnPropertyChanged(nameof(HasDeploySteps));
        OnPropertyChanged(nameof(DeployProjectText));
        OnPropertyChanged(nameof(DeployStepsText));
    }

    /// <summary>
    /// Los «Pasos tras clonar» de un sitio ya registrado: el diálogo con los pasos que tocan a su
    /// proyecto, marcados los que hacen falta; el usuario elige y los ejecuta (o los omite). No
    /// abre el navegador ni ofrece la CA: eso lo hace quien llama, después.
    /// </summary>
    public Task OfferDeployStepsAsync(Site site) => ShowDeployStepsAsync(site, DeployDialogMode.AfterClone);

    /// <summary>«Pasos de instalación…» del panel: lo mismo, para el sitio elegido.</summary>
    [RelayCommand]
    private Task DeployStepsAsync() => Selected is { } row ? ShowDeployStepsAsync(row.Site, DeployDialogMode.Panel) : Task.CompletedTask;

    private async Task ShowDeployStepsAsync(Site site, DeployDialogMode mode)
    {
        if (DeployBusy) return;
        DeployBusy = true;
        try
        {
            var paths = Main.App.Paths;
            if (!Directory.Exists(site.Folder))
            {
                await Main.InfoAsync($"No hay pasos para {site.Host}", $"La carpeta del proyecto ya no existe ({site.Folder}).", Tone.Bad);
                return;
            }

            var kind = ProjectKind.Detect(site.Folder);
            DeployTarget target;
            try
            {
                var (running, port) = DbStatus();
                target = await ProbeAsync(DeployTarget.ForSite(paths, site, running, port));
            }
            catch (RoostException ex)
            {
                await Main.InfoAsync($"No se pueden preparar los pasos de {site.Host}", ex.Message, Tone.Bad);
                return;
            }

            var steps = DeployPlan.For(kind, target);
            var runner = new DeployRunner(paths, Runner, DbRunner)
            {
                Composer = ComposerTool.IsCurrentInstalled(paths) ? ComposerTool.Locate(paths) : null,
            };
            var dialog = new DeployStepsDialogViewModel(mode, kind, target, steps,
                (step, lines, cancel) => RunDeployStepAsync(runner, target, step, lines, cancel),
                text => Main.CopyAsync(text, "Salida de los pasos copiada"));
            var result = await Main.ShowDialogAsync(dialog) as DeployDialogResult;

            if (result?.Ran != true) return;
            Main.NotifyConfigChanged();
            if (Selected?.Site.Name == site.Name) PickComposer(site);
            if (result.Succeeded) Main.Flash($"Pasos de {site.Host} hechos");
        }
        finally
        {
            DeployBusy = false;
        }
    }

    /// <summary>
    /// Un paso, fuera del hilo de la UI (los de la base de datos esperan al cliente). Si necesita
    /// Composer y aún no está, primero lo instala con el diálogo Instalando, encima de este.
    /// </summary>
    private async Task<DeployOutcome> RunDeployStepAsync(DeployRunner runner, DeployTarget target, DeployStep step, IProgress<string> lines, CancellationToken cancellation)
    {
        if (step.Kind == DeployStepKind.ComposerInstall || step.Kind == DeployStepKind.CreateProject && step.RunsProjectCode)
        {
            runner.Composer ??= await EnsureComposerAsync();
            if (runner.Composer is null)
                return new DeployOutcome(DeployOutcomeKind.Failed, "No se ha instalado Composer: sin él no se puede hacer este paso.");
        }
        return await Task.Run(() => runner.RunStepAsync(target, step, lines, cancellation), CancellationToken.None);
    }

    /// <summary>La base de datos global, si el stack la tiene en marcha, y su puerto.</summary>
    private (bool Running, int? Port) DbStatus()
    {
        var status = Main.Stack.Status;
        var running = status is { Running: true, Db: not null }
                      && status.Services.Any(s => s.State == "Running" && (s.Name.StartsWith("mariadb-", StringComparison.Ordinal) || s.Name.StartsWith("mysql-", StringComparison.Ordinal)));
        return (running, status?.DbPort);
    }

    /// <summary>Mira la base de datos (si existe y si tiene tablas) fuera del hilo de la UI.</summary>
    private async Task<DeployTarget> ProbeAsync(DeployTarget target) =>
        target.Database is { State: not DbState.Stopped } db
            ? target with { Database = await DeployProbe.ProbeAsync(db, DbRunner) }
            : target;

    // Nuevo sitio con una plantilla

    /// <summary>
    /// «Crear y abrir» con una plantilla: Composer (la primera vez), la base de datos en marcha si
    /// hace falta, y el diálogo de los pasos, que empieza solo: crear el proyecto, registrarlo, su
    /// base de datos, el .env o el wp-config.php… Si se deja a medias, Roost quita lo que creó en ese
    /// intento (la carpeta nueva, el sitio y su base de datos). Si acaba, lo de siempre: el hosts, el
    /// stack, la CA y el navegador.
    /// </summary>
    private Task CreateFromTemplateAsync(NewSiteRequest request) => Main.RunAsync($"No se pudo crear {request.Name}.test", async () =>
    {
        if (DeployBusy) return;
        DeployBusy = true;
        try
        {
            var paths = Main.App.Paths;
            var template = ProjectTemplates.Get(request.Template!.Value);
            var plan = ProjectTemplates.Plan(paths, new NewProjectRequest(template.Template, request.Name, request.Folder, request.Php, request.Database, request.Webapp));

            InstalledComposer? composer = null;
            if (template.UsesComposer && (composer = await EnsureComposerAsync()) is null) return;

            // Para crear la base de datos y migrar, tiene que estar en marcha.
            if (plan.Database is not null && !DbStatus().Running)
            {
                await Main.StartStackAsync();
                if (!DbStatus().Running) return;
            }

            var (running, port) = DbStatus();
            var target = await ProbeAsync(ProjectTemplates.Target(paths, plan, running, port));
            var steps = ProjectTemplates.Steps(plan, target);
            var runner = new DeployRunner(paths, Runner, DbRunner) { Composer = composer, Http = Main.App.Http, Template = plan };
            var dialog = new DeployStepsDialogViewModel(DeployDialogMode.Template, template.Expected(plan.Webapp), target, steps,
                (step, lines, cancel) => RunDeployStepAsync(runner, target, step, lines, cancel),
                text => Main.CopyAsync(text, "Salida copiada"));
            var run = dialog.StartAsync();
            await Main.ShowDialogAsync(dialog);
            await run;   // mientras corre no se puede cerrar: aquí ya ha terminado

            if (!dialog.Succeeded)
            {
                // No queda un sitio a medias: fuera lo que Roost ha creado en este intento, y nada más.
                var undone = await Task.Run(() => runner.Undo(target));
                Main.NotifyConfigChanged();
                Main.Flash(undone.Count == 0 ? $"No se ha creado {target.Host}" : $"No se ha creado {target.Host}: quitado lo de este intento");
                return;
            }

            await FinishCreatedAsync(plan.Name, target.Url + "/");
        }
        finally
        {
            DeployBusy = false;
        }
    });

    /// <summary>Lo de después de crear un sitio: el hosts, el stack, la CA y el navegador (como «Crear y abrir»).</summary>
    private async Task FinishCreatedAsync(string name, string url)
    {
        Main.NotifyConfigChanged();
        var host = $"{name}.test";
        var hostsOk = await Main.SyncHostsAsync(showError: false);
        if (!hostsOk)
            await Main.InfoAsync($"{host} está creado, pero falta en el hosts",
                $"{Main.HostsError}\n\nEl sitio queda registrado y se sirve, pero {host} no resolverá en el navegador hasta que esté en el hosts. "
                + $"Para añadirlo: Sitios › {host} › «Añadir al hosts» (Windows volverá a pedir permiso), o pasa al DNS local en Ajustes › Nombres .test.",
                Tone.Warn);

        if (Main.Stack.IsRunning)
        {
            if (!await Main.ApplyAsync()) return;
        }
        else
        {
            await Main.StartStackAsync();
            if (!Main.Stack.IsRunning) return;
        }

        Main.NotifyConfigChanged();
        Pick(_all.FirstOrDefault(r => r.Site.Name == name));
        if (!hostsOk) return;
        await Main.OfferTrustAsync();
        Shell.OpenUrl(url);
    }
}
