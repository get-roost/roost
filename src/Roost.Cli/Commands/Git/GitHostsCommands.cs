using System.ComponentModel;
using System.Text.Json;
using Roost.Cli.Rendering;
using Roost.Core;
using Roost.Core.Git;
using Roost.Core.Platform;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Roost.Cli.Commands.Git;

/// <summary><c>roost git hosts list</c>: los servidores SSH en los que confia Roost, con su huella y de donde sale.</summary>
public sealed class GitHostsListCommand : Command<GitHostsListCommand.Settings>
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
            var hosts = GitAccessSupport.Factory(paths).KnownHosts;
            hosts.EnsurePublished();
            var rows = hosts.Load().SelectMany(e => e.Hosts.Select(h => (Host: h, Entry: e)))
                .Select(r =>
                {
                    var (name, port) = Split(r.Host);
                    var published = KnownHosts.PublishedFor(name, port);
                    var origin = published is null
                        ? (KnownHosts.AcceptedOn(r.Entry) is { } on ? $"aceptada el {on}" : "aceptada")
                        : published.Keys.Any(k => k.Base64 == r.Entry.Key.Base64) ? $"publicada por {GitUrl.NameOf(published.Provider)}" : $"NO es la que publica {GitUrl.NameOf(published.Provider)}";
                    return new { host = r.Host, algorithm = r.Entry.Key.Algorithm, fingerprint = r.Entry.Key.Fingerprint, origin, published = published is not null, matchesPublished = published?.Keys.Any(k => k.Base64 == r.Entry.Key.Base64) };
                })
                .ToList();

            if (settings.Json)
            {
                Console.WriteLine(JsonSerializer.Serialize(new { file = hosts.FilePath, hosts = rows }, JsonOutput.Options));
                return 0;
            }

            var table = new Table().Border(TableBorder.Rounded);
            table.AddColumn("Servidor"); table.AddColumn("Clave"); table.AddColumn("Huella"); table.AddColumn("De donde");
            foreach (var r in rows)
                table.AddRow(Markup.Escape(r.host), r.algorithm, Markup.Escape(r.fingerprint),
                    r.matchesPublished == false ? $"[red]{Markup.Escape(r.origin)}[/]" : Markup.Escape(r.origin));
            AnsiConsole.Write(table);
            AnsiConsole.MarkupLineInterpolated($"[grey]{hosts.FilePath} - otro servidor: roost git hosts add <servidor>; olvidar uno: roost git hosts remove <servidor>[/]");
            return 0;
        }
        catch (RoostException ex)
        {
            AnsiConsole.MarkupLineInterpolated($"[red]{ex.Message}[/]");
            return 1;
        }
    }

    private static (string Host, int? Port) Split(string token) =>
        token.StartsWith('[') && token.IndexOf("]:", StringComparison.Ordinal) is var close and > 0 && int.TryParse(token[(close + 2)..], out var port)
            ? (token[1..close], port)
            : (token, null);
}

/// <summary>Lo comun de add y remove: el servidor y su puerto.</summary>
public class GitHostSettings : CommandSettings
{
    [CommandArgument(0, "<SERVIDOR>")]
    [Description("El servidor: git.acme.es, git.acme.es:2222 o la direccion SSH de un repositorio (git@git.acme.es:grupo/repo.git).")]
    public string Host { get; init; } = "";

    [CommandOption("--port <PUERTO>")]
    [Description("El puerto SSH, si no es el 22.")]
    public int? Port { get; init; }
}

/// <summary>
/// <c>roost git hosts add</c>: le pregunta al servidor por su clave, ensena la huella (calculada por
/// Roost) y la recuerda si dices que si o si coincide con --fingerprint. GitHub, GitLab y Bitbucket
/// se comparan con la que publica cada uno.
/// </summary>
public sealed class GitHostsAddCommand : AsyncCommand<GitHostsAddCommand.Settings>
{
    public sealed class Settings : GitHostSettings
    {
        [CommandOption("--fingerprint <HUELLA>")]
        [Description("La huella que te ha dado quien administra el servidor (SHA256:...): se acepta solo si coincide, sin preguntar.")]
        public string? Fingerprint { get; init; }

        [CommandOption("-y|--yes")]
        [Description("No pregunta: solo acepta las huellas publicadas por GitHub, GitLab y Bitbucket.")]
        public bool Yes { get; init; }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellation)
    {
        var paths = RoostPaths.Default();
        try
        {
            var (host, port) = GitAccessSupport.ParseHost(settings.Host, settings.Port);
            var hosts = GitAccessSupport.Factory(paths).KnownHosts;

            if (KnownHosts.PublishedFor(host, port) is { } published)
            {
                // Uno de serie: lo que diga el servidor se compara con lo publicado, nunca se acepta a ciegas.
                AnsiConsole.MarkupLineInterpolated($"Comprobando {host} contra la huella que publica {GitUrl.NameOf(published.Provider)} (ssh-keyscan)...");
                var keys = await hosts.ScanAsync(host, port, cancellation);
                var odd = keys.Where(k => published.Keys.All(p => p.Base64 != k.Base64)).ToList();
                if (odd.Count > 0)
                {
                    AnsiConsole.MarkupLineInterpolated($"[red]{host} presenta una clave que no es la que publica {GitUrl.NameOf(published.Provider)}[/] ({string.Join(", ", odd.Select(k => $"{k.Algorithm} {k.Fingerprint}"))}). Puede que alguien se este haciendo pasar por el, o que {GitUrl.NameOf(published.Provider)} la haya cambiado (si lo ha anunciado, actualiza Roost). No se ha guardado nada.");
                    return 1;
                }
                hosts.Forget(host, port);
                hosts.EnsurePublished();
                AnsiConsole.MarkupLineInterpolated($"[green]Coincide con la que publica {GitUrl.NameOf(published.Provider)}[/] ({keys[0].Algorithm} {keys[0].Fingerprint}). [grey]Es la de serie de Roost.[/]");
                return 0;
            }

            if (hosts.Status(host, port) is { Trust: HostTrust.Trusted } known && settings.Fingerprint is null)
            {
                AnsiConsole.MarkupLineInterpolated($"Roost ya conoce {known.Token} ({known.Stored[0].Key.Algorithm} {known.Stored[0].Key.Fingerprint}). [grey]Para cambiarla: roost git hosts remove {known.Token} y vuelve a anadirlo.[/]");
                return 0;
            }
            // Con --fingerprint sobre uno ya aceptado, lo guardado solo se sustituye si la nueva coincide.
            var why = await GitAccessSupport.EnsureTrustedAsync(hosts, host, port, settings.Yes, settings.Fingerprint, quiet: false, cancellation);
            if (why is null) return 0;
            AnsiConsole.MarkupLineInterpolated($"[red]{why}[/]");
            return 1;
        }
        catch (RoostException ex)
        {
            AnsiConsole.MarkupLineInterpolated($"[red]{ex.Message}[/]");
            return 1;
        }
    }
}

/// <summary><c>roost git hosts remove</c>: olvida la huella de un servidor (si es de serie, la proxima vez vuelve la publicada).</summary>
public sealed class GitHostsRemoveCommand : Command<GitHostSettings>
{
    public override int Execute(CommandContext context, GitHostSettings settings, CancellationToken cancellation)
    {
        var paths = RoostPaths.Default();
        try
        {
            var (host, port) = GitAccessSupport.ParseHost(settings.Host, settings.Port);
            var hosts = GitAccessSupport.Factory(paths).KnownHosts;
            var token = KnownHosts.HostToken(host, port);
            var removed = hosts.Forget(host, port);
            if (removed == 0)
            {
                AnsiConsole.MarkupLineInterpolated($"Roost no tenia ninguna huella de {token}.");
                return 0;
            }
            AnsiConsole.MarkupLineInterpolated($"[green]Olvidada[/] la huella de {token} [grey]({removed} clave(s))[/].");
            if (KnownHosts.PublishedFor(host, port) is { } published)
                AnsiConsole.MarkupLineInterpolated($"[grey]Es un servidor de serie: la proxima vez Roost vuelve a poner la que publica {GitUrl.NameOf(published.Provider)}.[/]");
            else
                AnsiConsole.MarkupLineInterpolated($"[grey]La proxima conexion pedira comprobarla otra vez: roost git hosts add {token}[/]");
            return 0;
        }
        catch (RoostException ex)
        {
            AnsiConsole.MarkupLineInterpolated($"[red]{ex.Message}[/]");
            return 1;
        }
    }
}
