using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using Roost.Cli.Rendering;
using Roost.Core;
using Roost.Core.Platform;
using Roost.Core.Scanning;
using Roost.Core.Stack;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Roost.Cli.Commands.Php;

/// <summary>
/// <c>roost php ini [&lt;php&gt;] [--set clave=valor]... [--reset]</c>: donde esta el php.ini que genera
/// Roost para una instalacion, sus conf.d (la comun y la de su rama) y los ajustes rapidos
/// (<c>memory_limit</c>, <c>upload_max_filesize</c>, <c>post_max_size</c>, <c>max_execution_time</c>),
/// que con <c>--set</c> se cambian como en la app: validados, en el php.ini y reiniciando solo su pool.
/// </summary>
public sealed class PhpIniCommand : AsyncCommand<PhpIniCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "[php]")]
        [Description("Rama (8.4), version (8.4.27) o instalacion (8.4.27-nts-x64). Sin nada, el PHP global.")]
        public string? Php { get; init; }

        [CommandOption("--set <CLAVE=VALOR>")]
        [Description("Cambia un ajuste rapido: memory_limit, upload_max_filesize, post_max_size o max_execution_time. Se puede repetir.")]
        public string[] Set { get; init; } = [];

        [CommandOption("--reset")]
        [Description("Vuelve a los ajustes rapidos de serie.")]
        public bool Reset { get; init; }

        [CommandOption("--json")]
        [Description("Salida en JSON.")]
        public bool Json { get; init; }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellation)
    {
        if (!PlatformGate.Require("php ini")) return 1;
        var paths = RoostPaths.Default();
        try
        {
            var php = PhpTarget.Resolve(paths, settings.Php);
            var configurator = new PhpConfigurator(paths, CommandRunner.Default);
            var planner = configurator.Planner;
            var current = configurator.Settings(php);

            if (settings.Reset || settings.Set.Length > 0)
            {
                var wanted = settings.Reset ? PhpSettings.Default : current;
                foreach (var pair in settings.Set) wanted = With(wanted, pair);
                configurator.SetSettings(php, wanted);
                current = configurator.Settings(php);
                AnsiConsole.MarkupLineInterpolated($"[green]Ajustes de PHP {php.Name} guardados[/] [grey]({planner.PhpIniPath(php)})[/]");
            }
            else
            {
                planner.WritePhpIni(php);   // que exista y este al dia, para poder abrirlo
            }

            if (settings.Json)
            {
                Console.WriteLine(JsonSerializer.Serialize(new
                {
                    php = php.Name,
                    ini = planner.PhpIniPath(php),
                    confD = planner.PhpConfD,
                    branchConfD = planner.PhpBranchConfD(php),
                    settings = new
                    {
                        memory_limit = current.MemoryLimit, upload_max_filesize = current.UploadMaxFilesize,
                        post_max_size = current.PostMaxSize, max_execution_time = current.MaxExecutionTime,
                    },
                }, JsonOutput.Options));
            }
            else
            {
                var table = new Table().Border(TableBorder.Rounded).AddColumn("").AddColumn("");
                table.AddRow("php.ini", Markup.Escape(planner.PhpIniPath(php)) + " [grey](generado: no se edita)[/]");
                table.AddRow("conf.d", Markup.Escape(Path.Combine(planner.PhpConfD, PhpIni.UserFragmentName)) + " [grey](todas las versiones)[/]");
                table.AddRow("", Markup.Escape(Path.Combine(planner.PhpBranchConfD(php), PhpIni.UserFragmentName)) + $" [grey](solo la {PhpTarget.Branch(php)})[/]");
                foreach (var (key, value, byDefault) in Rows(current))
                    table.AddRow(key, Markup.Escape(value) + (byDefault ? " [grey](de serie)[/]" : ""));
                AnsiConsole.MarkupLineInterpolated($"PHP {php.Name}");
                AnsiConsole.Write(table);
                AnsiConsole.MarkupLine("[grey]Lo de conf.d se lee despues del php.ini y manda sobre estos ajustes.[/]");
            }

            return settings.Reset || settings.Set.Length > 0 ? await PhpTarget.ApplyAsync(paths, php, cancellation) : 0;
        }
        catch (RoostException ex)
        {
            AnsiConsole.MarkupLineInterpolated($"[red]{ex.Message}[/]");
            return 1;
        }
    }

    private static IEnumerable<(string Key, string Value, bool ByDefault)> Rows(PhpSettings s)
    {
        var d = PhpSettings.Default;
        yield return ("memory_limit", s.MemoryLimit, s.MemoryLimit == d.MemoryLimit);
        yield return ("upload_max_filesize", s.UploadMaxFilesize, s.UploadMaxFilesize == d.UploadMaxFilesize);
        yield return ("post_max_size", s.PostMaxSize, s.PostMaxSize == d.PostMaxSize);
        yield return ("max_execution_time", s.MaxExecutionTime.ToString(CultureInfo.InvariantCulture), s.MaxExecutionTime == d.MaxExecutionTime);
    }

    private static PhpSettings With(PhpSettings s, string pair)
    {
        var eq = pair.IndexOf('=');
        if (eq <= 0) throw new RoostException($"'{pair}' no vale: usa --set clave=valor (por ejemplo --set memory_limit=1G).");
        var key = pair[..eq].Trim().ToLowerInvariant();
        var value = pair[(eq + 1)..].Trim();
        return key switch
        {
            "memory_limit" => s with { MemoryLimit = value },
            "upload_max_filesize" => s with { UploadMaxFilesize = value },
            "post_max_size" => s with { PostMaxSize = value },
            "max_execution_time" => int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds)
                ? s with { MaxExecutionTime = seconds }
                : throw new RoostException($"max_execution_time '{value}' no vale: pon segundos (0 = sin limite)."),
            _ => throw new RoostException($"'{key}' no es un ajuste rapido: memory_limit, upload_max_filesize, post_max_size o max_execution_time. Lo demas va en conf.d."),
        };
    }
}
