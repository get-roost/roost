using Roost.Core;
using Roost.Core.Config;
using Roost.Core.Dns;
using Roost.Core.Platform;
using Roost.Core.Sites;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Roost.Cli.Commands.Sites;

/// <summary>
/// <c>roost dns install</c>: los .test por el DNS local de Roost en vez del hosts (ROOST.md §5.7).
/// Pide permiso de administrador una sola vez, para la regla NRPT; despues, dar de alta o de baja
/// un sitio ya no lo pide.
/// </summary>
public sealed class DnsInstallCommand : AsyncCommand<EmptyCommandSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, EmptyCommandSettings settings, CancellationToken cancellation)
    {
        if (!PlatformGate.Require("dns install")) return 1;

        var paths = RoostPaths.Default();
        try
        {
            if (LocalDns.ForeignRule(LocalDns.ReadRules()) is { } foreign && !LocalDns.IsRuleInstalled())
            {
                AnsiConsole.MarkupLineInterpolated($"[red]Ya hay una regla de DNS para .test que no es de Roost[/] (apunta a {foreign.Servers}).");
                AnsiConsole.MarkupLine("Otra herramienta resuelve esos nombres. Quitala o sigue con el fichero hosts.");
                return 1;
            }

            if (!LocalDns.IsRuleInstalled())
            {
                AnsiConsole.MarkupLine("Windows va a pedir permiso de administrador una vez, para la regla de DNS de .test...");
                LocalDns.InstallRule(SiteSupport.HelperExe);
            }
            AnsiConsole.MarkupLineInterpolated($"[green]Regla de DNS:[/] .test -> {LocalDns.Address} [grey](la responde roostd con el stack en marcha)[/]");

            RoostConfig.SetResolver(paths.ConfigFile, RoostConfig.ResolverDns);
            AnsiConsole.MarkupLine("[green]Los sitios ya no tocan el hosts.[/] [grey]Las lineas .test que hubiera en el hosts se pueden quedar: dicen lo mismo.[/]");
            return await StackApply.IfRunningAsync(paths, _ => $"el DNS local responde en {LocalDns.EndPoint}.", cancellation);
        }
        catch (RoostException ex)
        {
            AnsiConsole.MarkupLineInterpolated($"[red]{ex.Message}[/]");
            return 1;
        }
    }
}

/// <summary><c>roost dns remove</c>: quita la regla y vuelve al fichero hosts (que se rellena con los sitios).</summary>
public sealed class DnsRemoveCommand : AsyncCommand<EmptyCommandSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, EmptyCommandSettings settings, CancellationToken cancellation)
    {
        if (!PlatformGate.Require("dns remove")) return 1;
        var paths = RoostPaths.Default();
        try
        {
            if (LocalDns.IsRuleInstalled())
            {
                AnsiConsole.MarkupLine("Windows va a pedir permiso de administrador para quitar la regla de DNS de .test...");
                LocalDns.RemoveRule(SiteSupport.HelperExe);
                AnsiConsole.MarkupLine("[green]Regla de DNS quitada.[/]");
            }

            RoostConfig.SetResolver(paths.ConfigFile, RoostConfig.ResolverHosts);
            AnsiConsole.MarkupLine("[green]Los .test vuelven a resolverse por el fichero hosts.[/]");
            SiteSupport.SyncHosts(paths, new SiteRegistry(paths).Load());
            return await StackApply.IfRunningAsync(paths, _ => "los .test vuelven a ir por el hosts.", cancellation);
        }
        catch (RoostException ex)
        {
            AnsiConsole.MarkupLineInterpolated($"[red]{ex.Message}[/]");
            return 1;
        }
    }
}

/// <summary><c>roost dns status</c>: el modo, la regla y si el DNS local contesta.</summary>
public sealed class DnsStatusCommand : AsyncCommand<EmptyCommandSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, EmptyCommandSettings settings, CancellationToken cancellation)
    {
        if (!PlatformGate.Require("dns status")) return 1;
        var paths = RoostPaths.Default();
        var config = RoostConfig.Load(paths.ConfigFile);
        var rules = LocalDns.ReadRules();
        AnsiConsole.MarkupLineInterpolated($"Los .test se resuelven por: [bold]{(config.UsesLocalDns ? "el DNS local de Roost" : "el fichero hosts")}[/] [grey]([[sites]] resolver = \"{config.SitesResolver}\")[/]");
        // Los colores van fuera de la interpolacion: MarkupLineInterpolated escaparia sus corchetes.
        AnsiConsole.MarkupLine($"Regla NRPT .test -> {LocalDns.Address}: {(LocalDns.HasRoostRule(rules) ? "[green]puesta[/]" : "[yellow]no esta[/]")}");
        if (LocalDns.ForeignRule(rules) is { } foreign && !LocalDns.HasRoostRule(rules))
            AnsiConsole.MarkupLineInterpolated($"[yellow]Hay otra regla para .test, que no es de Roost:[/] apunta a {foreign.Servers}.");

        var answers = await LocalDnsProbe.AsksAsync("roost-check.test", cancellation);
        AnsiConsole.MarkupLine($"DNS local en {LocalDns.EndPoint}: {(answers ? "[green]contesta[/]" : "[grey]no contesta (arranca con roost up si [[sites]] resolver = \"dns\")[/]")}");
        if (config.UsesLocalDns && !LocalDns.HasRoostRule(rules))
            AnsiConsole.MarkupLine("[yellow]Falta la regla: Windows no le preguntara. Ponla con roost dns install.[/]");
        return 0;
    }
}
