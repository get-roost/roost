using System.ComponentModel;
using System.Text.Json;
using Roost.Cli.Commands.Db;
using Roost.Cli.Rendering;
using Roost.Core;
using Roost.Core.Git;
using Roost.Core.Platform;
using Roost.Core.Sites;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Roost.Cli.Commands.Git;

/// <summary>Lo comun de roost git key: el nombre (el del sitio) y el repositorio para el enlace.</summary>
public class GitKeySettings : CommandSettings
{
    [CommandArgument(0, "<SITIO>")]
    [Description("El sitio de la clave (su nombre, el de <sitio>.test). Puede no existir aun: la clave se crea antes de clonar.")]
    public string Name { get; init; } = "";

    [CommandOption("--json")]
    [Description("Salida JSON para maquinas / CI.")]
    public bool Json { get; init; }

    /// <summary>El nombre normalizado (sin .test, en minusculas).</summary>
    public string Site
    {
        get
        {
            var name = Name.Trim().ToLowerInvariant();
            return name.EndsWith(".test", StringComparison.Ordinal) ? name[..^".test".Length] : name;
        }
    }
}

/// <summary>Lo comun de los comandos de claves: el repositorio del sitio (para el enlace a sus deploy keys).</summary>
internal static class GitKeySupport
{
    public static GitUrl? RepoOf(RoostPaths paths, string site, string? repo)
    {
        if (repo is not null) return GitUrl.Parse(repo);
        var remote = new SiteRegistry(paths).Find(site)?.Git?.Remote;
        return remote is not null && GitUrl.TryParse(remote, out var url, out _) ? url : null;
    }

    public static object Json(SshKeyInfo key, GitUrl? url) => new
    {
        name = key.Name,
        type = key.Type,
        fingerprint = key.Fingerprint,
        publicKey = key.PublicKey,
        privateFile = key.PrivateFile,
        publicFile = key.PublicFile,
        deployKeysUrl = url?.DeployKeysUrl,
    };

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

/// <summary><c>roost git key new</c>: crea la clave SSH de un sitio (ed25519, sin contrasena, solo para tu usuario) y ensena la publica.</summary>
public sealed class GitKeyNewCommand : AsyncCommand<GitKeyNewCommand.Settings>
{
    public sealed class Settings : GitKeySettings
    {
        [CommandOption("--repo <URL>")]
        [Description("El repositorio, para dar el enlace a sus deploy keys si el sitio aun no existe.")]
        public string? Repo { get; init; }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellation)
    {
        if (!PlatformGate.Require("git key")) return 1;
        var paths = RoostPaths.Default();
        try
        {
            var factory = GitAccessSupport.Factory(paths);
            var url = GitKeySupport.RepoOf(paths, settings.Site, settings.Repo);
            if (factory.Keys.Find(settings.Site) is { } existing)
            {
                if (settings.Json)
                {
                    Console.WriteLine(JsonSerializer.Serialize(new { ok = true, created = false, key = GitKeySupport.Json(existing, url) }, JsonOutput.Options));
                    return 0;
                }
                AnsiConsole.MarkupLineInterpolated($"[yellow]{settings.Site} ya tiene una clave SSH de Roost.[/] [grey]Para otra, quitala antes: roost git key remove {settings.Site}[/]");
                GitAccessSupport.ShowPublicKey(existing, url);
                return 0;
            }

            var key = await factory.Keys.GenerateAsync(settings.Site, cancellation);
            if (settings.Json)
            {
                Console.WriteLine(JsonSerializer.Serialize(new { ok = true, created = true, key = GitKeySupport.Json(key, url) }, JsonOutput.Options));
                return 0;
            }
            AnsiConsole.MarkupLineInterpolated($"[green]Clave creada[/] para {settings.Site} [grey](ed25519, sin contrasena: es una deploy key de solo lectura, una por repositorio)[/]");
            GitAccessSupport.ShowPublicKey(key, url);
            AnsiConsole.MarkupLineInterpolated($"[grey]Despues: roost site clone <url> --name {settings.Site} --ssh[/]");
            return 0;
        }
        catch (RoostException ex)
        {
            return GitKeySupport.Fail(ex, settings.Json);
        }
    }
}

/// <summary><c>roost git key show</c>: la clave publica de un sitio, para copiarla, y donde pegarla.</summary>
public sealed class GitKeyShowCommand : Command<GitKeyShowCommand.Settings>
{
    public sealed class Settings : GitKeySettings
    {
        [CommandOption("--repo <URL>")]
        [Description("El repositorio, para dar el enlace a sus deploy keys si el sitio aun no existe.")]
        public string? Repo { get; init; }
    }

    public override int Execute(CommandContext context, Settings settings, CancellationToken cancellation)
    {
        var paths = RoostPaths.Default();
        try
        {
            var factory = GitAccessSupport.Factory(paths);
            var key = factory.Keys.Find(settings.Site)
                ?? throw new RoostException($"{settings.Site} no tiene clave SSH de Roost. Creala con: roost git key new {settings.Site}");
            var url = GitKeySupport.RepoOf(paths, settings.Site, settings.Repo);
            if (settings.Json)
            {
                Console.WriteLine(JsonSerializer.Serialize(GitKeySupport.Json(key, url), JsonOutput.Options));
                return 0;
            }
            GitAccessSupport.ShowPublicKey(key, url);
            return 0;
        }
        catch (RoostException ex)
        {
            return GitKeySupport.Fail(ex, settings.Json);
        }
    }
}

/// <summary><c>roost git key remove</c>: borra la clave SSH de un sitio de este equipo (y recuerda quitar la publica en el proveedor).</summary>
public sealed class GitKeyRemoveCommand : Command<GitKeyRemoveCommand.Settings>
{
    public sealed class Settings : GitKeySettings
    {
        [CommandOption("-y|--yes")]
        [Description("No pide confirmacion aunque un sitio use la clave.")]
        public bool Yes { get; init; }
    }

    public override int Execute(CommandContext context, Settings settings, CancellationToken cancellation)
    {
        var paths = RoostPaths.Default();
        try
        {
            var factory = GitAccessSupport.Factory(paths);
            if (!factory.Keys.Exists(settings.Site))
                throw new RoostException($"{settings.Site} no tiene clave SSH de Roost: no hay nada que quitar.");
            var users = new SiteRegistry(paths).Load().Where(s => s.Git is { Access: SiteGit.AccessKey } g && g.KeyName == settings.Site).ToList();
            if (users.Count > 0)
            {
                if (!settings.Json)
                    AnsiConsole.MarkupLineInterpolated($"[yellow]{string.Join(", ", users.Select(u => u.Host))} entra con esta clave:[/] sin ella, Actualizar y cambiar de rama dejaran de funcionar.");
                if (!DbSession.Confirm("Quitarla igualmente?", settings.Yes)) return 1;
            }

            var url = GitKeySupport.RepoOf(paths, settings.Site, null);
            factory.Keys.Remove(settings.Site);
            if (settings.Json)
            {
                Console.WriteLine(JsonSerializer.Serialize(new { ok = true, removed = settings.Site, deployKeysUrl = url?.DeployKeysUrl }, JsonOutput.Options));
                return 0;
            }
            AnsiConsole.MarkupLineInterpolated($"[green]Quitada[/] la clave SSH de {settings.Site} de este equipo.");
            AnsiConsole.MarkupLineInterpolated($"[grey]Quita tambien la publica de las deploy keys del repositorio{(url?.DeployKeysUrl is { } page ? $": {page}" : ".")}[/]");
            return 0;
        }
        catch (RoostException ex)
        {
            return GitKeySupport.Fail(ex, settings.Json);
        }
    }
}
