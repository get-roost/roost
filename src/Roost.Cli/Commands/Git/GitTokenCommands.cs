using System.ComponentModel;
using System.Text.Json;
using Roost.Cli.Rendering;
using Roost.Core;
using Roost.Core.Git;
using Roost.Core.Platform;
using Roost.Core.Sites;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Roost.Cli.Commands.Git;

/// <summary>Lo comun de los comandos de tokens: donde vale y que sitios lo usan.</summary>
public class GitTokenSettings : CommandSettings
{
    [CommandArgument(0, "<SERVIDOR/RUTA>")]
    [Description("Para que repositorio vale: github.com/acme/tienda (o https://github.com/acme/tienda.git). github.com/acme vale para todos los de acme y github.com, para todo el servidor.")]
    public string Location { get; init; } = "";

    [CommandOption("--json")]
    [Description("Salida JSON para maquinas / CI.")]
    public bool Json { get; init; }
}

internal static class GitTokenSupport
{
    public static CredentialLocation Parse(string text) =>
        CredentialLocation.TryParse(text, out var location, out var error) ? location : throw new RoostException(error);

    /// <summary>Los sitios a los que git les daria ese token (es el suyo, o vale para su remoto).</summary>
    public static IReadOnlyList<Site> SitesUsing(RoostPaths paths, GitCredentials credentials, string target) =>
        new SiteRegistry(paths).Load().Where(s => s.Git is { } g && (g.CredentialTarget == target
            || (GitUrl.TryParse(g.Remote, out var url, out _) && CredentialLocation.For(url) is { } l && credentials.Candidates(l).Contains(target, StringComparer.OrdinalIgnoreCase))))
            .ToList();

    public static int Fail(RoostException ex, bool json)
    {
        if (json)
        {
            Console.WriteLine(JsonSerializer.Serialize(new { ok = false, error = ex.Message }, JsonOutput.Options));
            return 1;
        }
        AnsiConsole.MarkupLineInterpolated($"[red]{ex.Message}[/]");
        return 1;
    }
}

/// <summary>
/// <c>roost git token set</c>: guarda un token HTTPS en el Administrador de credenciales de Windows
/// (cifrado para tu usuario). Lo pide sin eco o lo lee de stdin: nunca va en la linea de ordenes.
/// </summary>
public sealed class GitTokenSetCommand : Command<GitTokenSetCommand.Settings>
{
    public sealed class Settings : GitTokenSettings
    {
        [CommandOption("--user <USUARIO>")]
        [Description("El usuario que va con el token. GitHub, GitLab y Bitbucket no lo usan con un token; un deploy token de GitLab o un servidor propio pueden pedirlo.")]
        public string? User { get; init; }
    }

    public override int Execute(CommandContext context, Settings settings, CancellationToken cancellation)
    {
        if (!PlatformGate.Require("git token")) return 1;
        var paths = RoostPaths.Default();
        try
        {
            var location = GitTokenSupport.Parse(settings.Location);
            var credentials = GitAccessSupport.Factory(paths).Credentials;
            if (!settings.Json && GitAccessSupport.Interactive)
                AnsiConsole.MarkupLineInterpolated($"Token para [bold]{location.Key}[/] [grey](se pega sin que se vea)[/]");
            var token = GitAccessSupport.ReadToken("Token")
                ?? throw new RoostException("Sin token: pegalo cuando lo pida o pasalo por stdin (una linea).");

            var warning = GitCredentials.LooksLikePassword(location.Host.Split(':')[0], token);
            var target = credentials.Save(location, token, settings.User);
            var sites = GitTokenSupport.SitesUsing(paths, credentials, target);
            if (settings.Json)
            {
                Console.WriteLine(JsonSerializer.Serialize(new { ok = true, target = credentials.Describe(target), warning, sites = sites.Select(s => s.Name) }, JsonOutput.Options));
                return 0;
            }
            if (warning is not null) AnsiConsole.MarkupLineInterpolated($"[yellow]Aviso:[/] {warning}");
            AnsiConsole.MarkupLineInterpolated($"[green]Guardado[/] el token de {location.Key} [grey]en el Administrador de credenciales de Windows, cifrado para tu usuario. Nunca va en la direccion del repositorio ni en los registros.[/]");
            AnsiConsole.MarkupLineInterpolated($"[grey]Vale para {(location.Path is null ? "todo " + location.Host : location.Key + " y lo que cuelgue de esa ruta")}.{(sites.Count > 0 ? $" Lo usaran: {string.Join(", ", sites.Select(s => s.Host))}." : "")}[/]");
            return 0;
        }
        catch (RoostException ex)
        {
            return GitTokenSupport.Fail(ex, settings.Json);
        }
    }
}

/// <summary><c>roost git token remove</c>: borra un token guardado.</summary>
public sealed class GitTokenRemoveCommand : Command<GitTokenSettings>
{
    public override int Execute(CommandContext context, GitTokenSettings settings, CancellationToken cancellation)
    {
        if (!PlatformGate.Require("git token")) return 1;
        var paths = RoostPaths.Default();
        try
        {
            var location = GitTokenSupport.Parse(settings.Location);
            var credentials = GitAccessSupport.Factory(paths).Credentials;
            var target = credentials.TargetOf(location);
            var sites = GitTokenSupport.SitesUsing(paths, credentials, target);
            var removed = credentials.Remove(target);
            if (settings.Json)
            {
                Console.WriteLine(JsonSerializer.Serialize(new { ok = true, removed, target = credentials.Describe(target), sites = sites.Select(s => s.Name) }, JsonOutput.Options));
                return 0;
            }
            if (!removed)
            {
                AnsiConsole.MarkupLineInterpolated($"No habia ningun token para {location.Key}. [grey]Los guardados: roost git token list[/]");
                return 0;
            }
            AnsiConsole.MarkupLineInterpolated($"[green]Borrado[/] el token de {location.Key}.");
            if (sites.Count > 0)
                AnsiConsole.MarkupLineInterpolated($"[yellow]{string.Join(", ", sites.Select(s => s.Host))}[/] se quedan sin el: si su repositorio es privado, Actualizar fallara hasta que guardes otro.");
            return 0;
        }
        catch (RoostException ex)
        {
            return GitTokenSupport.Fail(ex, settings.Json);
        }
    }
}

/// <summary><c>roost git token list</c>: los tokens guardados (donde valen y con que usuario; nunca el token).</summary>
public sealed class GitTokenListCommand : Command<GitTokenListCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("--json")]
        [Description("Salida JSON para maquinas / CI.")]
        public bool Json { get; init; }
    }

    public override int Execute(CommandContext context, Settings settings, CancellationToken cancellation)
    {
        var paths = RoostPaths.Default();
        try
        {
            var credentials = GitAccessSupport.Factory(paths).Credentials;
            var rows = credentials.List().Select(c => new
            {
                location = credentials.Describe(c.Target),
                user = c.UserName,
                saved = c.LastWritten,
                sites = GitTokenSupport.SitesUsing(paths, credentials, c.Target).Select(s => s.Name).ToList(),
            }).ToList();
            if (settings.Json)
            {
                Console.WriteLine(JsonSerializer.Serialize(new { tokens = rows }, JsonOutput.Options));
                return 0;
            }
            if (rows.Count == 0)
            {
                AnsiConsole.MarkupLine("No hay ningun token guardado. [grey]Para guardar uno: roost git token set github.com/acme/tienda[/]");
                return 0;
            }
            var table = new Table().Border(TableBorder.Rounded);
            table.AddColumn("Vale para"); table.AddColumn("Usuario"); table.AddColumn("Guardado"); table.AddColumn("Sitios");
            foreach (var r in rows)
                table.AddRow(Markup.Escape(r.location), Markup.Escape(r.user ?? ""), r.saved?.ToLocalTime().ToString("yyyy-MM-dd HH:mm") ?? "", Markup.Escape(string.Join(", ", r.sites)));
            AnsiConsole.Write(table);
            AnsiConsole.MarkupLine("[grey]Los tokens estan en el Administrador de credenciales de Windows; Roost nunca los ensena.[/]");
            return 0;
        }
        catch (RoostException ex)
        {
            return GitTokenSupport.Fail(ex, settings.Json);
        }
    }
}
