using System.ComponentModel;
using System.Text.Json;
using Roost.Cli.Net;
using Roost.Cli.Rendering;
using Roost.Core;
using Roost.Core.Config;
using Roost.Core.Git;
using Roost.Core.Platform;
using Roost.Core.Scanning;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Roost.Cli.Commands.Git;

/// <summary>Lo comun de los comandos que necesitan git: localizarlo e instalar MinGit la primera vez.</summary>
internal static class GitSetup
{
    /// <summary>El git que usa Roost; si no hay ninguno, instala el MinGit verificado (con su progreso en pantalla).</summary>
    public static async Task<GitLocation> EnsureAsync(RoostPaths paths, CancellationToken cancellation)
    {
        var config = RoostConfig.Load(paths.ConfigFile);
        if (GitTool.Locate(paths, config) is { } found) return found;

        var build = GitCatalog.Current;
        AnsiConsole.MarkupLineInterpolated($"Instalando git (la primera vez): {build.Label} de {build.Url.Host}...");
        GitLocation? location = null;
        await AnsiConsole.Status().StartAsync($"Instalando {build.Label}...", async ctx =>
        {
            var progress = new Progress<long>(bytes => ctx.Status($"Instalando {build.Label}... {bytes / 1048576.0:0.0} MB descargados"));
            location = await GitTool.EnsureAsync(paths, config, RoostHttp.Client, progress, cancellation);
        });
        AnsiConsole.MarkupLineInterpolated($"[green]Instalado[/] {build.Label} en {Path.GetDirectoryName(Path.GetDirectoryName(location!.Executable))} [grey](SHA-256 verificado)[/]");
        return location;
    }

    /// <summary>La primera linea de <c>git --version</c>, o null si no contesta.</summary>
    public static async Task<string?> VersionAsync(GitLocation git, CancellationToken cancellation)
    {
        try
        {
            var result = await CommandRunner.Default.RunAsync(
                new CommandSpec(git.Executable, ["--version"], Timeout: TimeSpan.FromSeconds(15)), null, cancellation);
            var line = result.Stdout.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.StartsWith("git version", StringComparison.Ordinal));
            return result.Succeeded ? line : null;
        }
        catch (RoostException)
        {
            return null;
        }
    }

    public static string SourceLabel(GitLocation git) =>
        git.Source == GitSource.Configured ? "el de roost.toml ([[git]] exe)" : $"el de Roost ({GitCatalog.Current.Label})";
}

/// <summary><c>roost git install</c>: el MinGit de Roost, verificado, en bin/git.</summary>
public sealed class GitInstallCommand : AsyncCommand<EmptyCommandSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, EmptyCommandSettings settings, CancellationToken cancellation)
    {
        if (!PlatformGate.Require("git install")) return 1;

        var paths = RoostPaths.Default();
        try
        {
            var config = RoostConfig.Load(paths.ConfigFile);
            if (GitTool.Locate(paths, config) is { } already)
            {
                if (already.Source == GitSource.Configured)
                    AnsiConsole.MarkupLineInterpolated($"roost.toml fija otro git ([[git]] exe = \"{config.GitExe}\"): Roost usa ese y no instala el suyo. Quita la clave si prefieres el MinGit de Roost.");
                else
                    AnsiConsole.MarkupLineInterpolated($"[green]git ya esta instalado:[/] {already.Executable}");
                AnsiConsole.MarkupLineInterpolated($"[grey]{await GitSetup.VersionAsync(already, cancellation) ?? "git --version no contesta"}[/]");
                return 0;
            }

            var git = await GitSetup.EnsureAsync(paths, cancellation);
            var version = await GitSetup.VersionAsync(git, cancellation);
            if (version is null)
            {
                AnsiConsole.MarkupLineInterpolated($"[yellow]Aviso:[/] {git.Executable} no responde a --version.");
                return 1;
            }
            AnsiConsole.MarkupLineInterpolated($"[grey]{version}[/]");
            return 0;
        }
        catch (RoostException ex)
        {
            AnsiConsole.MarkupLineInterpolated($"[red]{ex.Message}[/]");
            return 1;
        }
    }
}

/// <summary><c>roost git status</c>: que git usa Roost, su version y donde esta.</summary>
public sealed class GitStatusCommand : AsyncCommand<GitStatusCommand.Settings>
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
        try
        {
            var config = RoostConfig.Load(paths.ConfigFile);
            var git = GitTool.Locate(paths, config);
            var version = git is null ? null : await GitSetup.VersionAsync(git, cancellation);
            var roostGit = new GitInstaller(paths).Find(GitCatalog.Current);
            var env = new GitEnvironment(paths);
            var access = GitAccessSupport.Factory(paths);
            var ssh = OpenSsh.TryLocate();
            var keys = access.Keys.List();
            var tokens = access.Credentials.List();
            var trusted = access.KnownHosts.Load().SelectMany(e => e.Hosts).Distinct().Count(h => KnownHosts.PublishedFor(h) is null);

            if (settings.Json)
            {
                Console.WriteLine(JsonSerializer.Serialize(new
                {
                    installed = git is not null,
                    executable = git?.Executable,
                    source = git?.Source switch { GitSource.Roost => "roost", GitSource.Configured => "configured", _ => null },
                    version,
                    configuredExe = config.GitExe,
                    roost = new { build = GitCatalog.Current.Version, installed = roostGit is not null, directory = roostGit?.Directory, sha256 = GitCatalog.Current.Sha256 },
                    gitconfig = env.ConfigFile,
                    ssh = new { found = ssh is not null, directory = OpenSsh.DefaultDirectory(), knownHosts = env.KnownHostsFile, trustedHosts = trusted },
                    keys = keys.Select(k => k.Name),
                    tokens = tokens.Count,
                }, JsonOutput.Options));
                return 0;
            }

            if (git is null)
            {
                AnsiConsole.MarkupLine("[yellow]git no esta instalado.[/]" + PlatformGate.WindowsHint(" Roost lo instala solo la primera vez que clonas un sitio, o ahora con: roost git install"));
                AnsiConsole.MarkupLineInterpolated($"[grey]Sera {GitCatalog.Current.Label} (SHA-256 {GitCatalog.Current.Sha256[..12]}...), en {paths.GitBin}. Para usar otro: [[git]] exe en {paths.ConfigFile}[/]");
                return 0;
            }

            AnsiConsole.MarkupLineInterpolated($"[bold]git:[/] {version ?? "[red]no contesta a --version[/]"}");
            AnsiConsole.MarkupLineInterpolated($"[bold]Ejecutable:[/] {git.Executable} [grey]({GitSetup.SourceLabel(git)})[/]");
            if (git.Source == GitSource.Configured && roostGit is not null)
                AnsiConsole.MarkupLineInterpolated($"[grey]El MinGit de Roost sigue en {roostGit.Directory}; quita [[git]] exe de roost.toml para volver a el.[/]");
            AnsiConsole.MarkupLineInterpolated($"[bold]Configuracion aislada:[/] {env.ConfigFile} [grey](GIT_CONFIG_NOSYSTEM, sin prompts, Schannel; tu ~/.gitconfig no se toca ni se lee)[/]");
            if (ssh is null)
                AnsiConsole.MarkupLineInterpolated($"[yellow]SSH:[/] falta el cliente OpenSSH de Windows en {OpenSsh.DefaultDirectory()} [grey](caracteristica opcional de Windows: sin el, entra con un token HTTPS)[/]");
            else
                AnsiConsole.MarkupLineInterpolated($"[bold]SSH:[/] OpenSSH de Windows ({ssh.Directory}) [grey]- servidores conocidos: GitHub, GitLab y Bitbucket de serie{(trusted > 0 ? $" y {trusted} aceptado(s)" : "")} (roost git hosts list)[/]");
            AnsiConsole.MarkupLineInterpolated($"[bold]Claves SSH de Roost:[/] {(keys.Count == 0 ? "ninguna" : string.Join(", ", keys.Select(k => k.Name)))} [grey](roost git key show <sitio>)[/]");
            AnsiConsole.MarkupLineInterpolated($"[bold]Tokens HTTPS:[/] {tokens.Count} [grey](en el Administrador de credenciales de Windows: roost git token list)[/]");
            return version is null ? 1 : 0;
        }
        catch (RoostException ex)
        {
            AnsiConsole.MarkupLineInterpolated($"[red]{ex.Message}[/]");
            return 1;
        }
    }
}
