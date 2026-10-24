using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using Roost.Cli.Net;
using Roost.Cli.Rendering;
using Roost.Core;
using Roost.Core.Config;
using Roost.Core.Install;
using Roost.Core.Platform;
using Roost.Core.Stack;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Roost.Cli.Commands.Services;

/// <summary>
/// <c>roost mail install</c>: Mailpit verificado en bin/mailpit y en el stack ([mail] enabled), con el
/// mail() de PHP apuntando a el (20-roost-mail.ini en el conf.d comun). Se aplica si esta en marcha.
/// </summary>
public sealed class MailInstallCommand : AsyncCommand<EmptyCommandSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, EmptyCommandSettings settings, CancellationToken cancellation)
    {
        if (!WindowsOnly.Check("mail install")) return 1;
        var paths = RoostPaths.Default();
        try
        {
            var build = MailpitCatalog.Windows;
            var installer = new MailpitInstaller(paths, RoostHttp.Client);
            var mailpit = installer.Find(build);
            if (mailpit is null)
                mailpit = await ServiceSetup.InstallAsync(build.Label, build.Url, (p, ct) => installer.InstallAsync(build, p, ct), cancellation);
            else
                AnsiConsole.MarkupLineInterpolated($"[green]{build.Label} ya esta instalado:[/] {mailpit.Directory}");

            return await MailHints.EnableAsync(paths, cancellation);
        }
        catch (RoostException ex)
        {
            AnsiConsole.MarkupLineInterpolated($"[red]{ex.Message}[/]");
            return 1;
        }
    }
}

/// <summary><c>roost mail remove</c>: lo saca del stack, quita el mail() de PHP y sus binarios. La bandeja de data/mailpit se queda.</summary>
public sealed class MailRemoveCommand : AsyncCommand<EmptyCommandSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, EmptyCommandSettings settings, CancellationToken cancellation)
    {
        if (!WindowsOnly.Check("mail remove")) return 1;
        var paths = RoostPaths.Default();
        try
        {
            if (RoostConfig.Load(paths.ConfigFile).MailEnabled)
            {
                var applied = await MailHints.DisableAsync(paths, cancellation);
                if (applied != 0) return applied;
            }

            var installer = new MailpitInstaller(paths);
            var installed = installer.ListInstalled();
            if (installed.Count == 0)
            {
                AnsiConsole.MarkupLine("[grey]No habia ningun Mailpit de Roost instalado.[/]");
                return 0;
            }
            foreach (var mailpit in installed)
            {
                installer.Remove(mailpit);
                AnsiConsole.MarkupLineInterpolated($"[green]Quitado[/] {mailpit.Label}.");
            }
            if (Directory.Exists(paths.MailpitData))
                AnsiConsole.MarkupLineInterpolated($"[grey]La bandeja sigue en {paths.MailpitData}: Roost nunca borra datos. Borrala a mano si ya no la quieres.[/]");
            return 0;
        }
        catch (RoostException ex)
        {
            AnsiConsole.MarkupLineInterpolated($"[red]{ex.Message}[/]");
            return 1;
        }
    }
}

/// <summary><c>roost mail enable</c> / <c>disable</c>: Mailpit (y el mail() de PHP hacia el) entra o sale del stack.</summary>
public sealed class MailEnableCommand : AsyncCommand<EmptyCommandSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, EmptyCommandSettings settings, CancellationToken cancellation)
    {
        if (!WindowsOnly.Check("mail " + context.Name)) return 1;
        var paths = RoostPaths.Default();
        try
        {
            if (context.Name != "enable") return await MailHints.DisableAsync(paths, cancellation);
            await ServiceSetup.EnsureMailpitAsync(paths, cancellation);
            return await MailHints.EnableAsync(paths, cancellation);
        }
        catch (RoostException ex)
        {
            AnsiConsole.MarkupLineInterpolated($"[red]{ex.Message}[/]");
            return 1;
        }
    }
}

/// <summary><c>roost mail port --smtp &lt;p&gt; --web &lt;p&gt;</c>: cambia los puertos de Mailpit y lo aplica si esta en marcha.</summary>
public sealed class MailPortCommand : AsyncCommand<MailPortCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("--smtp <PUERTO>")]
        [Description("Donde recibe el correo (1025 por defecto). Es el MAIL_PORT de tus proyectos.")]
        public int? Smtp { get; init; }

        [CommandOption("--web <PUERTO>")]
        [Description("La bandeja web y su API (8025 por defecto).")]
        public int? Web { get; init; }

        public override ValidationResult Validate() =>
            Smtp is null && Web is null ? ValidationResult.Error("Indica --smtp, --web o los dos.") : ValidationResult.Success();
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellation)
    {
        if (!WindowsOnly.Check("mail port")) return 1;
        var paths = RoostPaths.Default();
        try
        {
            RoostConfig.SetMail(paths.ConfigFile, smtpPort: settings.Smtp, webPort: settings.Web);
            var config = RoostConfig.Load(paths.ConfigFile);
            AnsiConsole.MarkupLineInterpolated($"[green]Mailpit:[/] SMTP en {config.MailSmtpPort}, bandeja en {config.MailWebUrl} [grey](acuerdate del MAIL_PORT de tus proyectos)[/]");
            if (!config.MailEnabled)
            {
                AnsiConsole.MarkupLine("[grey]Mailpit no va en el stack: se usaran cuando lo actives (roost mail enable).[/]");
                return 0;
            }
            // El 20-roost-mail.ini con el puerto nuevo lo escribe el up, antes de arrancar PHP.
            return await StackApply.IfRunningAsync(paths, _ => $"Mailpit en el {config.MailSmtpPort}.", cancellation);
        }
        catch (RoostException ex)
        {
            AnsiConsole.MarkupLineInterpolated($"[red]{ex.Message}[/]");
            return 1;
        }
    }
}

/// <summary><c>roost mail open</c>: abre la bandeja de Mailpit en el navegador.</summary>
public sealed class MailOpenCommand : AsyncCommand<EmptyCommandSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, EmptyCommandSettings settings, CancellationToken cancellation)
    {
        if (!WindowsOnly.Check("mail open")) return 1;
        var paths = RoostPaths.Default();
        try
        {
            var config = RoostConfig.Load(paths.ConfigFile);
            if (!config.MailEnabled)
            {
                AnsiConsole.MarkupLine("[yellow]Mailpit no va en el stack.[/] Ponlo con: roost mail install");
                return 1;
            }
            if (await ServiceSetup.ServiceAsync(paths, "mailpit", cancellation) is not { State: "Running" })
            {
                AnsiConsole.MarkupLine("[yellow]Mailpit no esta en marcha.[/] Arrancalo con: roost up");
                return 1;
            }
            Process.Start(new ProcessStartInfo(config.MailWebUrl) { UseShellExecute = true })?.Dispose();
            AnsiConsole.MarkupLineInterpolated($"Abierta la bandeja: [bold]{config.MailWebUrl}[/]");
            return 0;
        }
        catch (RoostException ex)
        {
            AnsiConsole.MarkupLineInterpolated($"[red]{ex.Message}[/]");
            return 1;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            AnsiConsole.MarkupLineInterpolated($"[red]No se pudo abrir el navegador:[/] {ex.Message}");
            return 1;
        }
    }
}

/// <summary><c>roost mail status</c>: que Mailpit hay, si va en el stack, sus puertos y si el mail() de PHP va a el.</summary>
public sealed class MailStatusCommand : AsyncCommand<MailStatusCommand.Settings>
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
        RoostConfig config;
        try { config = RoostConfig.Load(paths.ConfigFile); }
        catch (RoostException ex)
        {
            AnsiConsole.MarkupLineInterpolated($"[red]{ex.Message}[/]");
            return 1;
        }

        var build = MailpitCatalog.Windows;
        var installer = new MailpitInstaller(paths);
        var mailpit = installer.Locate();
        var service = OperatingSystem.IsWindows() ? await ServiceSetup.ServiceAsync(paths, "mailpit", cancellation) : null;
        var phpIni = Path.Combine(paths.Etc, "php", "conf.d", MailConf.PhpIniName);

        if (settings.Json)
        {
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                installed = mailpit is not null,
                version = mailpit?.Version,
                directory = mailpit?.Directory,
                roost = new { version = build.Version, installed = installer.Find(build) is not null, url = build.Url.ToString(), sha256 = build.Sha256 },
                enabled = config.MailEnabled,
                smtpPort = config.MailSmtpPort,
                webPort = config.MailWebPort,
                inbox = config.MailWebUrl,
                state = service?.State,
                phpIni = File.Exists(phpIni) ? phpIni : null,
                data = paths.MailpitData,
                log = Path.Combine(paths.Logs, "mailpit.log"),
            }, JsonOutput.Options));
            return 0;
        }

        if (mailpit is null)
        {
            AnsiConsole.MarkupLine("[yellow]Mailpit no esta instalado.[/] Instalalo (y ponlo en el stack) con: roost mail install");
            AnsiConsole.MarkupLineInterpolated($"[grey]Sera {build.Label} (SHA-256 {build.Sha256[..12]}...), en {paths.MailpitBin}. El correo de PHP se queda en su bandeja: no sale del equipo.[/]");
            return 0;
        }

        AnsiConsole.MarkupLineInterpolated($"[bold]Mailpit:[/] {mailpit.Label} [grey]({mailpit.Directory})[/]");
        if (installer.Find(build) is null)
            AnsiConsole.MarkupLineInterpolated($"[yellow]Esta version de Roost trae {build.Label}:[/] roost mail install [grey](la de ahora se queda en su carpeta)[/]");
        AnsiConsole.MarkupLine($"En el stack: {(config.MailEnabled ? "[green]si[/]" : "[grey]no (roost mail enable)[/]")} · SMTP 127.0.0.1:{config.MailSmtpPort} · bandeja {config.MailWebUrl} · {ServiceSetup.StateText(service, config.MailEnabled)}");
        AnsiConsole.MarkupLine(File.Exists(phpIni)
            ? $"mail() de PHP: [green]a Mailpit[/] [grey]({Markup.Escape(phpIni)})[/]"
            : "mail() de PHP: [grey]sin configurar (va con Mailpit en el stack)[/]");
        AnsiConsole.MarkupLineInterpolated($"[grey]Bandeja: {paths.MailpitData} · log: {Path.Combine(paths.Logs, "mailpit.log")}[/]");
        MailHints.Env(config.MailSmtpPort);
        return 0;
    }
}

/// <summary>Lo comun de activar y quitar Mailpit, y lo que se le cuenta al usuario.</summary>
internal static class MailHints
{
    public static async Task<int> EnableAsync(RoostPaths paths, CancellationToken cancellation)
    {
        RoostConfig.SetMail(paths.ConfigFile, enabled: true);
        var config = RoostConfig.Load(paths.ConfigFile);
        // El mail() de PHP ya, aunque el stack este parado: la terminal lo lee igual.
        MailConf.SyncPhpIni(Path.Combine(paths.Etc, "php", "conf.d"), config);
        AnsiConsole.MarkupLineInterpolated($"[green]Mailpit va en el stack:[/] SMTP en 127.0.0.1:{config.MailSmtpPort}, bandeja en {config.MailWebUrl}");
        AnsiConsole.MarkupLine("[grey]El mail() de PHP va a Mailpit (etc\\php\\conf.d\\20-roost-mail.ini): el correo se queda en la bandeja, no sale del equipo.[/]");
        Env(config.MailSmtpPort);
        return await StackApply.IfRunningAsync(paths, _ => $"Mailpit en {config.MailWebUrl}", cancellation);
    }

    public static async Task<int> DisableAsync(RoostPaths paths, CancellationToken cancellation)
    {
        RoostConfig.SetMail(paths.ConfigFile, enabled: false);
        MailConf.SyncPhpIni(Path.Combine(paths.Etc, "php", "conf.d"), RoostConfig.Load(paths.ConfigFile));
        AnsiConsole.MarkupLine("Mailpit ya no va en el stack, y el mail() de PHP vuelve a como venia [grey](la bandeja se queda)[/].");
        // Primero se para (si el stack esta en marcha): un mailpit.exe vivo no deja quitar su carpeta.
        return await StackApply.IfRunningAsync(paths, _ => "sin Mailpit.", cancellation);
    }

    /// <summary>Las variables de correo de Laravel para el .env.</summary>
    public static void Env(int smtpPort)
    {
        AnsiConsole.MarkupLine("[grey]Para tu .env (Laravel):[/]");
        foreach (var line in MailConf.EnvLines(smtpPort).Split('\n'))
            AnsiConsole.MarkupLineInterpolated($"  [grey]{line}[/]");
    }
}
