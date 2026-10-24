using System.ComponentModel;
using System.Text.Json;
using Roost.Cli.Rendering;
using Roost.Core;
using Roost.Core.Install;
using Roost.Core.Platform;
using Roost.Core.Scanning;
using Roost.Core.Stack;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Roost.Cli.Commands.Php;

/// <summary>
/// <c>roost php ext list [--php &lt;version&gt;]</c>: las extensiones de una instalacion (las DLL de su
/// <c>ext/</c> y Xdebug), si se cargan y si van de serie. Es la misma eleccion que la pantalla PHP
/// de la app (<c>etc/php/&lt;instalacion&gt;/extensiones.json</c>).
/// </summary>
public sealed class PhpExtListCommand : Command<PhpExtListCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("--php <VERSION>")]
        [Description("Rama (8.4), version (8.4.27) o instalacion (8.4.27-nts-x64). Sin nada, el PHP global.")]
        public string? Php { get; init; }

        [CommandOption("--json")]
        [Description("Salida en JSON.")]
        public bool Json { get; init; }
    }

    public override int Execute(CommandContext context, Settings settings, CancellationToken cancellation)
    {
        if (!WindowsOnly.Check("php ext list")) return 1;
        var paths = RoostPaths.Default();
        try
        {
            var php = PhpTarget.Resolve(paths, settings.Php);
            var configurator = new PhpConfigurator(paths, CommandRunner.Default);
            var extensions = configurator.Extensions(php);

            if (settings.Json)
            {
                Console.WriteLine(JsonSerializer.Serialize(new
                {
                    php = php.Name,
                    ini = configurator.Planner.PhpIniPath(php),
                    extensions = extensions.Select(e => new { name = e.Name, on = e.On, byDefault = e.ByDefault, zend = e.Zend }),
                }, JsonOutput.Options));
                return 0;
            }

            var table = new Table().Border(TableBorder.Rounded).AddColumn("Extension").AddColumn("Estado").AddColumn("De serie").AddColumn("");
            foreach (var e in extensions)
            {
                var note = e.Name == PhpIni.Xdebug ? "[grey]roost php xdebug on|off[/]" : e.Zend ? "[grey]zend_extension[/]" : "";
                table.AddRow(Markup.Escape(e.Name), e.On ? "[green]activa[/]" : "[grey]-[/]", e.ByDefault ? "si" : "", note);
            }
            AnsiConsole.MarkupLineInterpolated($"PHP {php.Name} [grey]({configurator.Planner.PhpIniPath(php)})[/]");
            AnsiConsole.Write(table);
            AnsiConsole.MarkupLineInterpolated($"[grey]{extensions.Count(e => e.On)} de {extensions.Count} activas. Para cambiar una: roost php ext enable|disable <extension> --php {PhpTarget.Branch(php)}[/]");
            if (XdebugCatalog.WhyNot(php) is null && extensions.All(e => e.Name != PhpIni.Xdebug))
                AnsiConsole.MarkupLine("[grey]Xdebug no sale porque aun no esta instalado: roost php xdebug on lo baja y lo activa.[/]");
            return 0;
        }
        catch (RoostException ex)
        {
            AnsiConsole.MarkupLineInterpolated($"[red]{ex.Message}[/]");
            return 1;
        }
    }
}

public sealed class PhpExtToggleSettings : CommandSettings
{
    [CommandArgument(0, "<extension>")]
    [Description("El nombre de la extension (curl, soap, pdo_pgsql...), como sale en roost php ext list. Tambien php_soap.dll.")]
    public string Extension { get; init; } = "";

    [CommandOption("--php <VERSION>")]
    [Description("Rama (8.4), version (8.4.27) o instalacion (8.4.27-nts-x64). Sin nada, el PHP global.")]
    public string? Php { get; init; }
}

/// <summary>
/// <c>roost php ext enable|disable &lt;extension&gt;</c>: la activa o la quita del php.ini de una
/// instalacion, comprueba con <c>php -v</c> que PHP la carga (si no, la deja como estaba) y, si el
/// stack esta en marcha, reinicia solo su pool. <c>xdebug</c> va a <c>roost php xdebug on|off</c>.
/// </summary>
public abstract class PhpExtToggleCommand(bool enable) : AsyncCommand<PhpExtToggleSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, PhpExtToggleSettings settings, CancellationToken cancellation)
    {
        if (!WindowsOnly.Check(enable ? "php ext enable" : "php ext disable")) return 1;
        var paths = RoostPaths.Default();
        try
        {
            var php = PhpTarget.Resolve(paths, settings.Php);
            var name = PhpConfigurator.NormalizeName(settings.Extension);
            if (name == PhpIni.Xdebug)
                return enable ? await XdebugActions.OnAsync(paths, php, cancellation) : await XdebugActions.OffAsync(paths, php, cancellation);

            var configurator = new PhpConfigurator(paths, CommandRunner.Default);
            if (configurator.Extensions(php).FirstOrDefault(e => e.Name == name) is { } current && current.On == enable)
            {
                AnsiConsole.MarkupLineInterpolated($"{name} ya esta {(enable ? "activa" : "quitada")} en PHP {php.Name}.");
                return 0;
            }

            var check = await configurator.SetExtensionAsync(php, name, enable, cancellation);
            AnsiConsole.MarkupLineInterpolated($"[green]{(enable ? "Activada" : "Quitada")} {name}[/] en PHP {php.Name} [grey]({configurator.Planner.PhpIniPath(php)})[/]");
            if (enable && !check.Ran)
                AnsiConsole.MarkupLine("[yellow]php -v no ha contestado: no se ha podido comprobar que carga.[/]");
            PhpTarget.PrintWarnings(check);
            return await PhpTarget.ApplyAsync(paths, php, cancellation);
        }
        catch (RoostException ex)
        {
            AnsiConsole.MarkupLineInterpolated($"[red]{ex.Message}[/]");
            return 1;
        }
    }
}

public sealed class PhpExtEnableCommand() : PhpExtToggleCommand(enable: true);

public sealed class PhpExtDisableCommand() : PhpExtToggleCommand(enable: false);
