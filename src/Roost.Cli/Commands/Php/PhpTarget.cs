using Roost.Core;
using Roost.Core.Config;
using Roost.Core.Install;
using Roost.Core.Platform;
using Roost.Core.Scanning;
using Roost.Core.Stack;
using Roost.Daemon;
using Spectre.Console;

namespace Roost.Cli.Commands.Php;

/// <summary>
/// Lo comun de <c>roost php ext</c> y <c>roost php xdebug</c>: que instalacion se toca (la que se
/// pide o la global) y aplicar el cambio reiniciando solo el pool de esa rama, si esta en marcha.
/// </summary>
internal static class PhpTarget
{
    /// <summary>La instalacion que casa con <paramref name="spec"/> (rama, version o instalacion; preferida la NTS), o la global sin nada.</summary>
    public static InstalledPhp Resolve(RoostPaths paths, string? spec)
    {
        var installed = new PhpInstaller(paths).ListInstalled();
        var planner = new StackPlanner(paths, ProcessRunner.Default);
        if (string.IsNullOrWhiteSpace(spec)) return planner.ResolvePhp(RoostConfig.Load(paths.ConfigFile), installed);

        var candidates = PhpInstaller.Match(installed, spec);
        if (candidates.Count == 0)
            throw new RoostException($"No hay ningun PHP de Roost que case con '{spec.Trim()}'.{PlatformGate.WindowsHint($" Instalalo: roost php install {spec.Trim()}")}");
        return PhpInstaller.Best(candidates);
    }

    public static string Branch(InstalledPhp php) => php.Version is { } v ? $"{v.Major}.{v.Minor}" : php.Name;

    /// <summary>
    /// Si el stack esta en marcha y esta instalacion sirve algo (localhost o un sitio), reinicia solo
    /// su pool (<c>roost restart php-8.4</c>): el php.ini ya esta reescrito y el resto no se toca.
    /// </summary>
    public static async Task<int> ApplyAsync(RoostPaths paths, InstalledPhp php, CancellationToken cancellation)
    {
        using var client = new DaemonClient(paths);
        if (await client.TryStatusAsync(cancellation) is not { Running: true } status)
        {
            AnsiConsole.MarkupLine("[grey]Se aplica en el proximo roost up.[/]");
            return 0;
        }

        var pool = $"php-{Branch(php)}";
        var serves = status.Php == php.Name || status.Sites.Any(s => s.Php == php.Name);
        var running = status.Services.Any(s => s.Name.StartsWith(pool + "-", StringComparison.Ordinal) && s.State == "Running");
        if (!serves || !running)
        {
            AnsiConsole.MarkupLineInterpolated($"[grey]PHP {php.Name} no sirve ahora nada: se aplica cuando lo use un sitio o el global (roost up).[/]");
            return 0;
        }

        var response = await client.RestartAsync(pool, cancellation);
        if (!response.Ok)
        {
            AnsiConsole.MarkupLineInterpolated($"[red]No se pudo reiniciar {pool}:[/] {response.Error}");
            return 1;
        }
        AnsiConsole.MarkupLineInterpolated($"[green]Aplicado:[/] reiniciado solo el pool {pool} [grey](el resto del stack sigue igual)[/]");
        return 0;
    }

    /// <summary>Los avisos de arranque que no son de lo que se ha cambiado (un conf.d del usuario con algo roto).</summary>
    public static void PrintWarnings(PhpCheck check)
    {
        foreach (var warning in check.Warnings)
            AnsiConsole.MarkupLineInterpolated($"[yellow]Aviso de PHP al arrancar:[/] {warning}");
    }
}
