using System.ComponentModel;
using System.Text.Json;
using Roost.Cli.Net;
using Roost.Cli.Rendering;
using Roost.Core.Install;
using Roost.Core.Platform;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Roost.Cli.Commands.Php;

/// <summary>
/// <c>roost php list</c>: los PHP instalados por Roost y los que hay en el catalogo de
/// windows.php.net, cada uno con su estado de seguridad. Sin red sigue listando lo instalado.
/// </summary>
public sealed class PhpListCommand : AsyncCommand<PhpListCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("--json")]
        [Description("Salida JSON para maquinas / CI en lugar de las tablas.")]
        public bool Json { get; init; }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellation)
    {
        var paths = RoostPaths.Default();
        var installed = new PhpInstaller(paths, RoostHttp.Client).ListInstalled();

        IReadOnlyList<PhpBuild> catalog = [];
        string? catalogError = null;
        if (!OperatingSystem.IsWindows())
        {
            catalogError = "El catalogo de PHP solo existe para Windows por ahora: en Linux no hay binarios oficiales.";
        }
        else
        {
            try
            {
                catalog = await PhpCatalog.FetchAsync(RoostHttp.Client, cancellation);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException
                                       && !cancellation.IsCancellationRequested)
            {
                catalogError = $"No se pudo leer el catalogo de windows.php.net: {ex.Message}";
            }
        }

        if (settings.Json)
            RenderJson(paths, installed, catalog, catalogError);
        else
            RenderTables(paths, installed, catalog, catalogError);

        return 0;
    }

    private static void RenderTables(
        RoostPaths paths, IReadOnlyList<InstalledPhp> installed, IReadOnlyList<PhpBuild> catalog, string? catalogError)
    {
        AnsiConsole.MarkupLineInterpolated($"[bold]PHP instalados por Roost[/]  [grey]({paths.PhpBin})[/]");
        if (installed.Count == 0)
        {
            AnsiConsole.MarkupLine("[grey]Ninguno todavia." + PlatformGate.WindowsHint(" Prueba: roost php install 8.4") + "[/]");
        }
        else
        {
            var table = new Table().Border(TableBorder.Rounded);
            table.AddColumn("");
            table.AddColumn("Instalacion");
            table.AddColumn("Veredicto");
            table.AddColumn("Detalle");
            foreach (var php in installed)
            {
                if (php.Version is not { } version)
                {
                    table.AddRow("", Markup.Escape(php.Name), "-", "[grey]nombre de carpeta sin version[/]");
                    continue;
                }
                var f = CatalogAudit.Evaluate(version, php.Executable, "Roost");
                table.AddRow(CatalogAudit.Dot(f.Color), Markup.Escape(php.Name), f.VerdictLabel, Markup.Escape(f.Headline));
            }
            AnsiConsole.Write(table);
        }

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[bold]Disponibles en windows.php.net[/]  [grey](solo la ultima de cada rama)[/]");
        if (catalogError is not null)
        {
            AnsiConsole.MarkupLineInterpolated($"[yellow]{catalogError}[/]");
            return;
        }

        var available = new Table().Border(TableBorder.Rounded);
        available.AddColumn("");
        available.AddColumn("Rama");
        available.AddColumn("Version");
        available.AddColumn("Veredicto");
        available.AddColumn("Instalada");
        foreach (var branch in Branches(catalog))
        {
            var f = CatalogAudit.Evaluate(branch.Version, branch.FileName, "windows.php.net");
            var mine = installed.Where(p => p.Version is { } v && v.CompareTo(branch.Version) == 0).Select(p => p.Name);
            available.AddRow(
                CatalogAudit.Dot(f.Color),
                Markup.Escape(branch.Branch),
                Markup.Escape(branch.Version.ToString()),
                f.VerdictLabel,
                Markup.Escape(string.Join(", ", mine)));
        }
        AnsiConsole.Write(available);
        AnsiConsole.MarkupLine("[grey]Instalar: roost php install <rama|version> [[--ts]] [[--arch x86]][/]");
    }

    private static void RenderJson(
        RoostPaths paths, IReadOnlyList<InstalledPhp> installed, IReadOnlyList<PhpBuild> catalog, string? catalogError)
    {
        var payload = new
        {
            root = paths.PhpBin,
            installed = installed.Select(p =>
            {
                var f = p.Version is { } v ? CatalogAudit.Evaluate(v, p.Executable, "Roost") : null;
                return new
                {
                    name = p.Name,
                    version = p.Version?.ToString(),
                    path = p.Executable,
                    verdict = f?.VerdictLabel,
                    color = f?.Color.ToString().ToLowerInvariant(),
                };
            }),
            catalogError,
            catalog = Branches(catalog).Select(b =>
            {
                var f = CatalogAudit.Evaluate(b.Version, b.FileName, "windows.php.net");
                return new
                {
                    branch = b.Branch,
                    version = b.Version.ToString(),
                    verdict = f.VerdictLabel,
                    color = f.Color.ToString().ToLowerInvariant(),
                    builds = catalog.Where(x => x.Branch == b.Branch).Select(x => new
                    {
                        name = x.InstallName,
                        compiler = x.Compiler,
                        file = x.FileName,
                        url = x.Url,
                        sha256 = x.Sha256,
                        size = x.SizeText,
                    }),
                };
            }),
        };
        Console.WriteLine(JsonSerializer.Serialize(payload, JsonOutput.Options));
    }

    // Un representante por rama (todas las variantes de una rama comparten version), de la mas nueva a la mas vieja.
    private static IEnumerable<PhpBuild> Branches(IEnumerable<PhpBuild> catalog) =>
        catalog.GroupBy(b => b.Branch).Select(g => g.First()).OrderByDescending(b => b.Version);
}
