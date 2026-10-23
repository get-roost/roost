using System.ComponentModel;
using Roost.Core;
using Roost.Core.Config;
using Roost.Core.Install;
using Roost.Core.Platform;
using Roost.Core.Scanning;
using Roost.Core.Stack;
using Roost.Daemon;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Roost.Cli.Commands.Db;

/// <summary>Lo que comparten los comandos que hablan con la base de datos en marcha: encontrarla y pedir contrasenas.</summary>
internal static class DbSession
{
    /// <summary>La base de datos global en marcha, con su my.ini y su puerto; null (ya avisado por pantalla) si no lo esta.</summary>
    public static async Task<(InstalledDb Db, string Ini, int Port)?> RunningAsync(RoostPaths paths, CancellationToken cancellation)
    {
        using var client = new DaemonClient(paths);
        var status = await client.TryStatusAsync(cancellation);
        if (status is not { Running: true, Db: not null })
        {
            AnsiConsole.MarkupLine("[red]La base de datos no esta en marcha.[/] Arranca el stack con: roost up");
            return null;
        }

        var planner = new StackPlanner(paths, ProcessRunner.Default);
        var config = RoostConfig.Load(paths.ConfigFile);
        var db = planner.ResolveDb(config, new DbInstaller(paths).ListInstalled())
            ?? throw new RoostException("No hay base de datos global: elige una con roost db use <motor@version>.");
        return (db, planner.DbIniPath(db), status.DbPort ?? config.DbPort);
    }

    /// <summary>
    /// Pide una contrasena sin eco, dos veces. Si la entrada no es un terminal (un script, una
    /// tuberia), lee una linea de stdin y no la repite: asi nunca va en la linea de ordenes.
    /// </summary>
    public static string? ReadPassword(string what, bool allowEmpty)
    {
        if (Console.IsInputRedirected)
        {
            // PowerShell 5.1 con $OutputEncoding en UTF-8 manda la tuberia con BOM: no es parte de la contrasena.
            var line = Console.ReadLine()?.TrimStart('﻿').TrimEnd('\r');
            if (line is null || (line.Length == 0 && !allowEmpty))
            {
                AnsiConsole.MarkupLine("[red]Sin entrada:[/] pasa la contrasena por stdin (una linea) o ejecutalo en un terminal.");
                return null;
            }
            return line;
        }

        var hint = allowEmpty ? " [grey](vacia = sin contrasena)[/]" : "";
        var first = AnsiConsole.Prompt(new TextPrompt<string>($"{what}{hint}:").Secret().AllowEmpty());
        var second = AnsiConsole.Prompt(new TextPrompt<string>("Repitela:").Secret().AllowEmpty());
        if (first != second)
        {
            AnsiConsole.MarkupLine("[red]No coinciden.[/] No se ha cambiado nada.");
            return null;
        }
        if (first.Length == 0 && !allowEmpty)
        {
            AnsiConsole.MarkupLine("[red]La contrasena no puede estar vacia.[/]");
            return null;
        }
        return first;
    }

    /// <summary>Confirma una accion destructiva; con --yes o sin terminal no pregunta (sin --yes y sin terminal, se niega).</summary>
    public static bool Confirm(string question, bool yes)
    {
        if (yes) return true;
        if (Console.IsInputRedirected || !AnsiConsole.Profile.Capabilities.Interactive)
        {
            AnsiConsole.MarkupLine("[red]Hace falta confirmar:[/] repitelo con --yes.");
            return false;
        }
        return AnsiConsole.Confirm(question, defaultValue: false);
    }

    public static int Fail(RoostException ex)
    {
        AnsiConsole.MarkupLineInterpolated($"[red]{ex.Message}[/]");
        return 1;
    }
}

/// <summary><c>roost db drop</c>: borra una base de datos (nunca las del sistema) tras confirmar.</summary>
public sealed class DbDropCommand : AsyncCommand<DbDropCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<NOMBRE>")]
        [Description("La base de datos que se borra, con todas sus tablas. No se puede deshacer.")]
        public string Name { get; init; } = "";

        [CommandOption("-y|--yes")]
        [Description("No preguntar (para scripts).")]
        public bool Yes { get; init; }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellation)
    {
        if (!DbAdmin.IsValidName(settings.Name))
        {
            AnsiConsole.MarkupLineInterpolated($"[red]'{settings.Name}' no vale como nombre:[/] usa solo letras, numeros y _ (hasta 64).");
            return 1;
        }
        if (DbAdmin.SystemSchemas.Contains(settings.Name))
        {
            AnsiConsole.MarkupLineInterpolated($"[red]{settings.Name} es del propio servidor:[/] no se borra.");
            return 1;
        }

        try
        {
            if (await DbSession.RunningAsync(RoostPaths.Default(), cancellation) is not { } session) return 1;
            if (!DbSession.Confirm($"Se borra [bold]{Markup.Escape(settings.Name)}[/] y todas sus tablas. No se puede deshacer. ¿Seguro?", settings.Yes))
                return 1;

            DbAdmin.Drop(session.Db, session.Ini, settings.Name, ProcessRunner.Default);
            AnsiConsole.MarkupLineInterpolated($"[green]Borrada[/] {settings.Name}.");
            return 0;
        }
        catch (RoostException ex)
        {
            return DbSession.Fail(ex);
        }
    }
}

/// <summary><c>roost db password</c>: la contrasena de root, en el servidor y en lo que guarda Roost.</summary>
public sealed class DbPasswordCommand : AsyncCommand<DbPasswordCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("--save-only")]
        [Description("Solo decirle a Roost cual es (ya la cambiaste por fuera): no toca el servidor.")]
        public bool SaveOnly { get; init; }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellation)
    {
        var paths = RoostPaths.Default();
        try
        {
            if (await DbSession.RunningAsync(paths, cancellation) is not { } session) return 1;

            AnsiConsole.MarkupLineInterpolated($"Contrasena de root de [bold]{session.Db.Label}[/] [grey]({DbAdmin.PasswordRule})[/]");
            var password = DbSession.ReadPassword(settings.SaveOnly ? "La contrasena que ya tiene root" : "Nueva contrasena", allowEmpty: true);
            if (password is null) return 1;
            if (!DbAdmin.IsValidPassword(password))
            {
                AnsiConsole.MarkupLineInterpolated($"[red]La contrasena no vale:[/] {DbAdmin.PasswordRule}.");
                return 1;
            }

            DbAdmin.ChangeRootPassword(paths, session.Db, session.Port, password, settings.SaveOnly, ProcessRunner.Default);
            if (password.Length == 0)
                AnsiConsole.MarkupLine("[green]root vuelve a ir sin contrasena.[/]");
            else if (settings.SaveOnly)
                AnsiConsole.MarkupLine("[green]Guardada.[/] Roost ya entra con ella (y la parada limpia, la consola y crear bases de datos siguen funcionando).");
            else
                AnsiConsole.MarkupLine("[green]Cambiada en el servidor y guardada en Roost.[/] Acuerdate del DB_PASSWORD de tus proyectos.");
            if (password.Length > 0)
                AnsiConsole.MarkupLineInterpolated($"[grey]Roost la guarda en tu equipo, en {new DbPasswords(paths).FilePath}, para sus propios comandos.[/]");
            return 0;
        }
        catch (RoostException ex)
        {
            return DbSession.Fail(ex);
        }
    }
}

/// <summary><c>roost db user list</c>: los usuarios (sin root ni los del sistema) y a que llegan.</summary>
public sealed class DbUserListCommand : AsyncCommand<EmptyCommandSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, EmptyCommandSettings settings, CancellationToken cancellation)
    {
        try
        {
            if (await DbSession.RunningAsync(RoostPaths.Default(), cancellation) is not { } session) return 1;
            var users = DbAdmin.ListUsers(session.Db, session.Ini, ProcessRunner.Default);
            if (users.Count == 0)
            {
                AnsiConsole.MarkupLine("[grey]Ningun usuario aparte de root. Crea uno: roost db user add <usuario> --db <base_de_datos>[/]");
                return 0;
            }

            var table = new Table().Border(TableBorder.Rounded);
            table.AddColumn("Usuario"); table.AddColumn("Host"); table.AddColumn("Acceso a");
            foreach (var user in users)
                table.AddRow(Markup.Escape(user.Name), Markup.Escape(user.Host), Markup.Escape(user.AccessText));
            AnsiConsole.Write(table);
            AnsiConsole.MarkupLineInterpolated($"[grey]root no sale aqui: su contrasena se cambia con roost db password. Conexion: 127.0.0.1:{session.Port}.[/]");
            return 0;
        }
        catch (RoostException ex)
        {
            return DbSession.Fail(ex);
        }
    }
}

/// <summary><c>roost db user add</c>: crea 'usuario'@'%' con acceso a una base de datos o a todas.</summary>
public sealed class DbUserAddCommand : AsyncCommand<DbUserAddCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<USUARIO>")]
        [Description("Letras, numeros y _ (hasta 32).")]
        public string User { get; init; } = "";

        [CommandOption("--db <BASE_DE_DATOS>")]
        [Description("A la que tiene acceso (todos los privilegios sobre ella). Sin --db, a todas.")]
        public string? Database { get; init; }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellation)
    {
        if (!DbAdmin.IsValidUserName(settings.User))
        {
            AnsiConsole.MarkupLineInterpolated($"[red]'{settings.User}' no vale como usuario:[/] usa solo letras, numeros y _ (hasta 32).");
            return 1;
        }

        try
        {
            if (await DbSession.RunningAsync(RoostPaths.Default(), cancellation) is not { } session) return 1;
            var password = DbSession.ReadPassword($"Contrasena de {settings.User}", allowEmpty: false);
            if (password is null) return 1;

            DbAdmin.CreateUser(session.Db, session.Ini, settings.User, password, settings.Database, ProcessRunner.Default);
            AnsiConsole.MarkupLineInterpolated($"[green]Usuario {settings.User} creado[/] con acceso a {settings.Database ?? "todas las bases de datos"}.");
            AnsiConsole.MarkupLine("[grey]Para tu .env:[/]");
            AnsiConsole.WriteLine($"DB_HOST=127.0.0.1\nDB_PORT={session.Port}\n{(settings.Database is null ? "" : $"DB_DATABASE={settings.Database}\n")}DB_USERNAME={settings.User}\nDB_PASSWORD={password}");
            return 0;
        }
        catch (RoostException ex)
        {
            return DbSession.Fail(ex);
        }
    }
}

/// <summary>Ajustes de los comandos que actuan sobre un usuario que ya existe.</summary>
public class DbUserSettings : CommandSettings
{
    [CommandArgument(0, "<USUARIO>")]
    public string User { get; init; } = "";

    [CommandOption("--host <HOST>")]
    [Description("El host de la cuenta si tiene varias (lo normal es % y no hace falta).")]
    public string? Host { get; init; }
}

/// <summary><c>roost db user remove</c>: borra la cuenta (sus bases de datos se quedan).</summary>
public sealed class DbUserRemoveCommand : AsyncCommand<DbUserRemoveCommand.Settings>
{
    public sealed class Settings : DbUserSettings
    {
        [CommandOption("-y|--yes")]
        [Description("No preguntar (para scripts).")]
        public bool Yes { get; init; }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellation)
    {
        try
        {
            if (await DbSession.RunningAsync(RoostPaths.Default(), cancellation) is not { } session) return 1;
            var (user, host) = Resolve(session.Db, session.Ini, settings);
            if (!DbSession.Confirm($"Se borra la cuenta [bold]{Markup.Escape(user)}@{Markup.Escape(host)}[/] (sus bases de datos se quedan). ¿Seguro?", settings.Yes))
                return 1;

            DbAdmin.DropUser(session.Db, session.Ini, user, host, ProcessRunner.Default);
            AnsiConsole.MarkupLineInterpolated($"[green]Borrado[/] {user}@{host}.");
            return 0;
        }
        catch (RoostException ex)
        {
            return DbSession.Fail(ex);
        }
    }

    /// <summary>El usuario y su host: el que se pida, o el unico que tenga esa cuenta.</summary>
    internal static (string User, string Host) Resolve(InstalledDb db, string ini, DbUserSettings settings)
    {
        if (settings.Host is not null) return (settings.User, settings.Host);
        var hosts = DbAdmin.ListUsers(db, ini, ProcessRunner.Default).Where(u => u.Name == settings.User).Select(u => u.Host).ToList();
        return hosts.Count switch
        {
            0 => throw new RoostException($"No hay ningun usuario {settings.User}. Mira los que hay con: roost db user list"),
            1 => (settings.User, hosts[0]),
            _ => throw new RoostException($"{settings.User} tiene varias cuentas ({string.Join(", ", hosts)}): indica cual con --host."),
        };
    }
}

/// <summary><c>roost db user password</c>: cambia la contrasena de un usuario.</summary>
public sealed class DbUserPasswordCommand : AsyncCommand<DbUserSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, DbUserSettings settings, CancellationToken cancellation)
    {
        try
        {
            if (await DbSession.RunningAsync(RoostPaths.Default(), cancellation) is not { } session) return 1;
            var (user, host) = DbUserRemoveCommand.Resolve(session.Db, session.Ini, settings);
            var password = DbSession.ReadPassword($"Nueva contrasena de {user}", allowEmpty: false);
            if (password is null) return 1;

            DbAdmin.SetUserPassword(session.Db, session.Ini, user, host, password, ProcessRunner.Default);
            AnsiConsole.MarkupLineInterpolated($"[green]Cambiada[/] la contrasena de {user}@{host}. Acuerdate del DB_PASSWORD de ese proyecto.");
            return 0;
        }
        catch (RoostException ex)
        {
            return DbSession.Fail(ex);
        }
    }
}
