using System.ComponentModel;
using System.Text.Json;
using Roost.Cli.Net;
using Roost.Cli.Rendering;
using Roost.Core;
using Roost.Core.Install;
using Roost.Core.Platform;
using Roost.Core.Scanning;
using Roost.Core.Stack;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Roost.Cli.Commands.Php;

public sealed class PhpXdebugSettings : CommandSettings
{
    [CommandArgument(0, "[php]")]
    [Description("Rama (8.4), version (8.4.27) o instalacion (8.4.27-nts-x64). Sin nada, el PHP global.")]
    public string? Php { get; init; }
}

/// <summary>Encender y apagar Xdebug, compartido con <c>roost php ext enable|disable xdebug</c>.</summary>
internal static class XdebugActions
{
    public static async Task<int> OnAsync(RoostPaths paths, InstalledPhp php, CancellationToken cancellation)
    {
        if (XdebugCatalog.WhyNot(php) is { } why)
        {
            AnsiConsole.MarkupLineInterpolated($"[red]{why}[/]");
            return 1;
        }

        var build = XdebugCatalog.For(php)!;
        var installer = new XdebugInstaller(paths, RoostHttp.Client);
        if (installer.Find(build) is null)
        {
            AnsiConsole.MarkupLineInterpolated($"Bajando {build.FileName} de {build.Url.Host} ({build.Size / 1024} KB, la primera vez para la {build.PhpBranch})...");
            string? dll = null;
            await AnsiConsole.Status().StartAsync($"Bajando {build.Label}...", async ctx =>
            {
                var progress = new Progress<long>(bytes => ctx.Status($"Bajando {build.Label}... {bytes / 1024} KB de {build.Size / 1024} KB"));
                dll = await installer.InstallAsync(build, progress, cancellation);
            });
            AnsiConsole.MarkupLineInterpolated($"[green]Instalado[/] {build.Label} en {dll} [grey](SHA-256 verificado: {build.Sha256})[/]");
        }

        var check = await new PhpConfigurator(paths, CommandRunner.Default).SetXdebugAsync(php, enabled: true, cancellation);
        if (check.Ran)
            AnsiConsole.MarkupLineInterpolated($"[green]Xdebug {check.XdebugVersion} activado en PHP {php.Name}[/] [grey](php -v: with Xdebug v{check.XdebugVersion})[/]");
        else
            AnsiConsole.MarkupLineInterpolated($"[yellow]Xdebug activado en el php.ini de {php.Name}, pero php -v no ha contestado: no se ha podido comprobar que carga.[/]");
        AnsiConsole.MarkupLine("[grey]Modo debug,develop. Se depura la peticion que trae XDEBUG_TRIGGER (cookie, GET/POST o variable de entorno) y conecta con el editor en 127.0.0.1:9003.[/]");
        PhpTarget.PrintWarnings(check);
        return await PhpTarget.ApplyAsync(paths, php, cancellation);
    }

    public static async Task<int> OffAsync(RoostPaths paths, InstalledPhp php, CancellationToken cancellation)
    {
        var configurator = new PhpConfigurator(paths, CommandRunner.Default);
        if (!configurator.Choice(php).On.Contains(PhpIni.Xdebug))
        {
            AnsiConsole.MarkupLineInterpolated($"Xdebug ya esta apagado en PHP {php.Name}.");
            return 0;
        }
        var check = await configurator.SetXdebugAsync(php, enabled: false, cancellation);
        AnsiConsole.MarkupLineInterpolated($"[green]Xdebug apagado en PHP {php.Name}[/] [grey](la DLL se queda en bin\\xdebug para cuando lo vuelvas a encender)[/]");
        PhpTarget.PrintWarnings(check);
        return await PhpTarget.ApplyAsync(paths, php, cancellation);
    }
}

/// <summary>
/// <c>roost php xdebug on [&lt;php&gt;]</c>: baja la DLL de Xdebug que toca a esa rama (verificada, la
/// primera vez), la activa en su php.ini con <c>zend_extension</c>, comprueba con <c>php -v</c> que
/// carga y, si el stack esta en marcha, reinicia solo su pool.
/// </summary>
public sealed class PhpXdebugOnCommand : AsyncCommand<PhpXdebugSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, PhpXdebugSettings settings, CancellationToken cancellation)
    {
        if (!WindowsOnly.Check("php xdebug on")) return 1;
        var paths = RoostPaths.Default();
        try
        {
            return await XdebugActions.OnAsync(paths, PhpTarget.Resolve(paths, settings.Php), cancellation);
        }
        catch (RoostException ex)
        {
            AnsiConsole.MarkupLineInterpolated($"[red]{ex.Message}[/]");
            return 1;
        }
    }
}

/// <summary><c>roost php xdebug off [&lt;php&gt;]</c>: lo quita del php.ini de esa instalacion y reinicia su pool si esta en marcha.</summary>
public sealed class PhpXdebugOffCommand : AsyncCommand<PhpXdebugSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, PhpXdebugSettings settings, CancellationToken cancellation)
    {
        if (!WindowsOnly.Check("php xdebug off")) return 1;
        var paths = RoostPaths.Default();
        try
        {
            return await XdebugActions.OffAsync(paths, PhpTarget.Resolve(paths, settings.Php), cancellation);
        }
        catch (RoostException ex)
        {
            AnsiConsole.MarkupLineInterpolated($"[red]{ex.Message}[/]");
            return 1;
        }
    }
}

/// <summary><c>roost php xdebug status [&lt;php&gt;]</c>: en que instalaciones esta encendido, que DLL usa y, si esta encendido, si carga.</summary>
public sealed class PhpXdebugStatusCommand : AsyncCommand<PhpXdebugStatusCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "[php]")]
        [Description("Rama, version o instalacion. Sin nada, todas las instaladas.")]
        public string? Php { get; init; }

        [CommandOption("--json")]
        [Description("Salida en JSON.")]
        public bool Json { get; init; }
    }

    private sealed record Row(string Php, string Branch, bool On, bool? Loads, string? Version, string? Dll, string? Available, string? Unsupported);

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellation)
    {
        if (!WindowsOnly.Check("php xdebug status")) return 1;
        var paths = RoostPaths.Default();
        try
        {
            var targets = settings.Php is null ? new PhpInstaller(paths).ListInstalled() : [PhpTarget.Resolve(paths, settings.Php)];
            var configurator = new PhpConfigurator(paths, CommandRunner.Default);
            var installer = new XdebugInstaller(paths);
            var rows = new List<Row>();
            foreach (var php in targets)
            {
                var on = configurator.Choice(php).On.Contains(PhpIni.Xdebug);
                var dll = installer.Locate(php);
                bool? loads = null;
                string? version = dll is null ? null : Path.GetFileName(Path.GetDirectoryName(dll));
                if (on)
                {
                    var check = await configurator.CheckAsync(php, cancellation);
                    if (check.Ran)
                    {
                        loads = check.XdebugVersion is not null;
                        version = check.XdebugVersion ?? version;
                    }
                }
                rows.Add(new Row(php.Name, PhpTarget.Branch(php), on, loads, version, dll, XdebugCatalog.For(php)?.Version, XdebugCatalog.WhyNot(php)));
            }

            if (settings.Json)
            {
                Console.WriteLine(JsonSerializer.Serialize(rows.Select(r => new
                {
                    php = r.Php, branch = r.Branch, on = r.On, loads = r.Loads, version = r.Version, dll = r.Dll,
                    available = r.Available, unsupported = r.Unsupported,
                    mode = r.On ? "debug,develop" : null, startWithRequest = r.On ? "trigger" : null, client = r.On ? "127.0.0.1:9003" : null,
                }), JsonOutput.Options));
                return 0;
            }

            if (rows.Count == 0)
            {
                AnsiConsole.MarkupLine("No hay ningun PHP instalado por Roost. Instala uno: roost php install 8.4");
                return 0;
            }
            var table = new Table().Border(TableBorder.Rounded)
                .AddColumn("PHP").AddColumn("Xdebug").AddColumn("Version").AddColumn("DLL");
            foreach (var r in rows)
            {
                var state = r.Unsupported is not null ? "[grey]no disponible[/]"
                    : !r.On ? "[grey]apagado[/]"
                    : r.Loads == false ? "[red]encendido, pero no carga[/]"
                    : "[green]encendido[/]";
                var dll = r.Dll is null ? (r.Unsupported is null ? "[grey](se baja al encenderlo)[/]" : $"[grey]{Markup.Escape(r.Unsupported)}[/]") : Markup.Escape(r.Dll);
                table.AddRow(Markup.Escape(r.Php), state, Markup.Escape(r.Version ?? r.Available ?? "-"), dll);
            }
            AnsiConsole.Write(table);
            if (rows.Any(r => r.On))
                AnsiConsole.MarkupLine("[grey]Modo debug,develop · start_with_request=trigger (XDEBUG_TRIGGER) · el editor escucha en 127.0.0.1:9003.[/]");
            else
                AnsiConsole.MarkupLine("[grey]Para encenderlo: roost php xdebug on [[<php>]][/]");
            return rows.Any(r => r.Loads == false) ? 1 : 0;
        }
        catch (RoostException ex)
        {
            AnsiConsole.MarkupLineInterpolated($"[red]{ex.Message}[/]");
            return 1;
        }
    }
}
