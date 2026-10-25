using System.ComponentModel;
using System.Text.Json;
using Roost.Cli.Commands.Shims;
using Roost.Cli.Net;
using Roost.Cli.Rendering;
using Roost.Core;
using Roost.Core.Composer;
using Roost.Core.Install;
using Roost.Core.Platform;
using Roost.Core.Scanning;
using Roost.Core.Shims;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Roost.Cli.Commands.Composer;

/// <summary>Lo comun de los comandos que usan Composer: instalarlo la primera vez, su version, el shim y las lineas en vivo.</summary>
internal static class ComposerSetup
{
    /// <summary>El Composer de esta version de Roost; si falta, lo instala (con su progreso en pantalla).</summary>
    public static async Task<InstalledComposer> EnsureAsync(RoostPaths paths, CancellationToken cancellation)
    {
        var build = ComposerCatalog.Stable;
        if (new ComposerInstaller(paths).Find(build) is { } found) return found;

        AnsiConsole.MarkupLineInterpolated($"Instalando Composer (la primera vez): {build.Label} de {build.Url.Host}...");
        InstalledComposer? installed = null;
        await AnsiConsole.Status().StartAsync($"Instalando {build.Label}...", async ctx =>
        {
            var progress = new Progress<long>(bytes => ctx.Status($"Instalando {build.Label}... {bytes / 1048576.0:0.0} MB descargados"));
            installed = await ComposerTool.EnsureAsync(paths, RoostHttp.Client, progress, cancellation);
        });
        AnsiConsole.MarkupLineInterpolated($"[green]Instalado[/] {build.Label} en {installed!.Directory} [grey](SHA-256 verificado)[/]");
        return installed;
    }

    /// <summary>Lo que dice <c>composer --version</c> con el PHP de <paramref name="directory"/>, o null si no contesta.</summary>
    public static async Task<string?> VersionLineAsync(RoostPaths paths, InstalledComposer composer, InstalledPhp php, string directory, CancellationToken cancellation)
    {
        try
        {
            var spec = ComposerTool.Command(paths, php, composer, ["--version", "--no-ansi"], directory) with { Timeout = TimeSpan.FromSeconds(60) };
            var result = await CommandRunner.Default.RunAsync(spec, null, cancellation);
            var line = (result.Stdout + "\n" + result.Stderr).Split('\n').Select(l => l.Trim())
                .FirstOrDefault(l => l.StartsWith("Composer", StringComparison.Ordinal));
            return result.Succeeded ? line : null;
        }
        catch (RoostException)
        {
            return null;
        }
    }

    /// <summary>Con los shims puestos, añade el de composer si falta (los de antes solo traian php).</summary>
    public static void EnsureShim(RoostPaths paths)
    {
        var shims = new ShimInstaller(paths);
        if (!shims.IsInstalled)
        {
            AnsiConsole.MarkupLine(OperatingSystem.IsWindows()
                ? "[grey]Para usar composer en la terminal con el PHP de cada carpeta: roost shims install (pone php y composer delante en el PATH de tu usuario).[/]"
                : "[grey]Para usar composer en la terminal con el PHP de cada carpeta: roost shims install (enlaces de php y composer en ~/.local/bin).[/]");
            return;
        }
        try
        {
            if (shims.AddMissing(AppContext.BaseDirectory).Contains("composer"))
                AnsiConsole.MarkupLineInterpolated($"[green]Shim de composer puesto[/] en {shims.Directory}: [grey]en una terminal nueva, composer usa el PHP de cada carpeta.[/]");
        }
        catch (RoostException ex)
        {
            AnsiConsole.MarkupLineInterpolated($"[yellow]{ex.Message}[/]");
        }
        ShimSupport.WarnAboutShadowing(shims.Directory, "composer");
    }
}

/// <summary>
/// Las lineas de una orden segun salen, tal cual y en orden: <see cref="Progress{T}"/> las mandaria
/// al pool de hilos y podrian llegar desordenadas (stdout y stderr se leen a la vez).
/// </summary>
internal sealed class ConsoleLines : IProgress<string>
{
    private readonly Lock _lock = new();

    public void Report(string value)
    {
        lock (_lock) Console.WriteLine(value);
    }
}

/// <summary><c>roost composer install</c>: el Composer de Roost, verificado, en bin/composer.</summary>
public sealed class ComposerInstallCommand : AsyncCommand<EmptyCommandSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, EmptyCommandSettings settings, CancellationToken cancellation)
    {
        var paths = RoostPaths.Default();
        try
        {
            var build = ComposerCatalog.Stable;
            var already = new ComposerInstaller(paths).Find(build);
            var composer = already ?? await ComposerSetup.EnsureAsync(paths, cancellation);
            if (already is not null)
                AnsiConsole.MarkupLineInterpolated($"[green]Composer {build.Version} ya esta instalado:[/] {already.Phar}");

            // Que de verdad arranca: con el PHP global y su php.ini (openssl, curl, zip...).
            PhpChoice? php = null;
            try { php = ComposerTool.PhpFor(paths, Environment.CurrentDirectory); }
            catch (RoostException ex) { AnsiConsole.MarkupLineInterpolated($"[yellow]{ex.Message}[/] [grey](Composer necesita un PHP para funcionar.)[/]"); }
            var code = 0;
            if (php is not null)
            {
                var version = await ComposerSetup.VersionLineAsync(paths, composer, php.Php, Environment.CurrentDirectory, cancellation);
                if (version is null)
                {
                    AnsiConsole.MarkupLineInterpolated($"[yellow]Aviso:[/] {composer.Phar} no responde a --version con {php.Php.Name}.");
                    code = 1;
                }
                else
                {
                    AnsiConsole.MarkupLineInterpolated($"[grey]{version} (con PHP {php.Php.Version?.ToString() ?? php.Php.Name})[/]");
                }
            }

            ComposerSetup.EnsureShim(paths);
            return code;
        }
        catch (RoostException ex)
        {
            AnsiConsole.MarkupLineInterpolated($"[red]{ex.Message}[/]");
            return 1;
        }
    }
}

/// <summary><c>roost composer status</c>: que Composer usa Roost, con que PHP aqui, y si el shim esta puesto y le gana otro.</summary>
public sealed class ComposerStatusCommand : AsyncCommand<ComposerStatusCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("--json")]
        [Description("Salida JSON para maquinas / CI.")]
        public bool Json { get; init; }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellation)
    {
        var paths = RoostPaths.Default();
        var build = ComposerCatalog.Stable;
        var installer = new ComposerInstaller(paths);
        var composer = ComposerTool.Locate(paths);
        var current = installer.Find(build) is not null;
        var others = installer.ListInstalled().Where(c => c.Version != build.Version).ToList();

        PhpChoice? php = null;
        string? phpError = null;
        try { php = ComposerTool.PhpFor(paths, Environment.CurrentDirectory); }
        catch (RoostException ex) { phpError = ex.Message; }
        var version = composer is not null && php is not null
            ? await ComposerSetup.VersionLineAsync(paths, composer, php.Php, Environment.CurrentDirectory, cancellation)
            : null;

        var shims = new ShimInstaller(paths);
        var shadow = shims.Has("composer") ? ShimShadowing.Find("composer", shims.Directory) : null;

        if (settings.Json)
        {
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                installed = composer is not null,
                version = composer?.Version,
                phar = composer?.Phar,
                composerVersion = ComposerTool.ParseVersion(version),
                roost = new { version = build.Version, installed = current, url = build.Url.ToString(), sha256 = build.Sha256, minPhp = build.MinPhp.ToString() },
                others = others.Select(c => new { version = c.Version, phar = c.Phar }),
                php = php is null ? null : new { name = php.Php.Name, executable = php.Php.Executable, reason = php.Reason },
                phpError,
                shim = new { installed = shims.Has("composer"), path = shims.ToolPath("composer"), shadowedBy = shadow?.Path },
            }, JsonOutput.Options));
            return 0;
        }

        if (composer is null)
        {
            AnsiConsole.MarkupLine("[yellow]Composer no esta instalado.[/] Roost lo instala la primera vez que lo usas desde la app, o ahora con: roost composer install");
            AnsiConsole.MarkupLineInterpolated($"[grey]Sera {build.Label} (SHA-256 {build.Sha256[..12]}...), en {paths.ComposerBin}.[/]");
            return 0;
        }

        AnsiConsole.MarkupLineInterpolated($"[bold]Composer:[/] {composer.Label} [grey]({composer.Phar})[/]");
        if (!current)
            AnsiConsole.MarkupLineInterpolated($"[yellow]Esta version de Roost trae {build.Label}:[/] roost composer install [grey](la de ahora se queda en su carpeta)[/]");
        else
            AnsiConsole.MarkupLineInterpolated($"[grey]SHA-256 {build.Sha256[..12]}... (verificado al instalar, de {build.Url.Host})[/]");
        foreach (var other in others.Where(o => o.Version != composer.Version))
            AnsiConsole.MarkupLineInterpolated($"[grey]Tambien esta {other.Label} ({other.Directory}), de una version anterior de Roost.[/]");

        if (php is null)
            AnsiConsole.MarkupLineInterpolated($"[yellow]PHP aqui:[/] {phpError}");
        else
        {
            AnsiConsole.MarkupLineInterpolated($"[bold]PHP aqui:[/] {php.Php.Name} [grey](porque {php.Reason})[/]");
            AnsiConsole.MarkupLine($"[bold]composer --version:[/] {(version is null ? "[red]no contesta[/]" : Markup.Escape(version))}");
        }

        AnsiConsole.MarkupLine($"Shim de composer: {(shims.Has("composer") ? "[green]puesto[/]" : "[yellow]no puesto[/]")} [grey]({Markup.Escape(shims.ToolPath("composer"))})[/]");
        if (shims.Has("composer"))
            ShimSupport.WarnAboutShadowing(shims.Directory, "composer");
        else if (shims.IsInstalled)
            AnsiConsole.MarkupLine("[grey]Los shims son de una version anterior (sin composer): roost composer install lo anade.[/]");
        else
            AnsiConsole.MarkupLine("[grey]Para usarlo en la terminal: roost shims install[/]");
        return php is null || version is not null ? 0 : 1;
    }
}
