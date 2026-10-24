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

        var build = GitCatalog.Windows;
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
        git.Source == GitSource.Configured ? "el de roost.toml ([[git]] exe)" : $"el de Roost ({GitCatalog.Windows.Label})";
}

/// <summary><c>roost git install</c>: el MinGit de Roost, verificado, en bin/git.</summary>
public sealed class GitInstallCommand : AsyncCommand<EmptyCommandSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, EmptyCommandSettings settings, CancellationToken cancellation)
    {
        if (!WindowsOnly.Check("git install")) return 1;

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
            var roostGit = new GitInstaller(paths).Find(GitCatalog.Windows);
            var env = new GitEnvironment(paths);

            if (settings.Json)
            {
                Console.WriteLine(JsonSerializer.Serialize(new
                {
                    installed = git is not null,
                    executable = git?.Executable,
                    source = git?.Source switch { GitSource.Roost => "roost", GitSource.Configured => "configured", _ => null },
                    version,
                    configuredExe = config.GitExe,
                    roost = new { build = GitCatalog.Windows.Version, installed = roostGit is not null, directory = roostGit?.Directory, sha256 = GitCatalog.Windows.Sha256 },
                    gitconfig = env.ConfigFile,
                }, JsonOutput.Options));
                return 0;
            }

            if (git is null)
            {
                AnsiConsole.MarkupLine("[yellow]git no esta instalado.[/] Roost lo instala solo la primera vez que clonas un sitio, o ahora con: roost git install");
                AnsiConsole.MarkupLineInterpolated($"[grey]Sera {GitCatalog.Windows.Label} (SHA-256 {GitCatalog.Windows.Sha256[..12]}...), en {paths.GitBin}. Para usar otro: [[git]] exe en {paths.ConfigFile}[/]");
                return 0;
            }

            AnsiConsole.MarkupLineInterpolated($"[bold]git:[/] {version ?? "[red]no contesta a --version[/]"}");
            AnsiConsole.MarkupLineInterpolated($"[bold]Ejecutable:[/] {git.Executable} [grey]({GitSetup.SourceLabel(git)})[/]");
            if (git.Source == GitSource.Configured && roostGit is not null)
                AnsiConsole.MarkupLineInterpolated($"[grey]El MinGit de Roost sigue en {roostGit.Directory}; quita [[git]] exe de roost.toml para volver a el.[/]");
            AnsiConsole.MarkupLineInterpolated($"[bold]Configuracion aislada:[/] {env.ConfigFile} [grey](GIT_CONFIG_NOSYSTEM, sin prompts, Schannel; tu ~/.gitconfig no se toca ni se lee)[/]");
            return version is null ? 1 : 0;
        }
        catch (RoostException ex)
        {
            AnsiConsole.MarkupLineInterpolated($"[red]{ex.Message}[/]");
            return 1;
        }
    }
}
