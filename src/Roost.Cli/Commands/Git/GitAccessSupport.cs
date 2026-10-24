using Roost.Core;
using Roost.Core.Git;
using Roost.Core.Platform;
using Roost.Core.Scanning;
using Spectre.Console;

namespace Roost.Cli.Commands.Git;

/// <summary>
/// Lo comun de los comandos de acceso a git (claves, servidores conocidos, tokens y el acceso de
/// site clone): el GitAccessFactory de la CLI, pedir un token sin eco, entender un servidor y la
/// comprobacion de huellas (TOFU explicito: nunca se acepta un servidor sin ensenar su huella).
/// </summary>
internal static class GitAccessSupport
{
    /// <summary>El de la CLI: el Administrador de credenciales, el roost.exe de al lado como helper y el OpenSSH de Windows.</summary>
    public static GitAccessFactory Factory(RoostPaths paths) => GitAccessFactory.CreateDefault(paths, CommandRunner.Default);

    /// <summary>true si se puede preguntar (hay terminal y la entrada no viene de una tuberia).</summary>
    public static bool Interactive => AnsiConsole.Profile.Capabilities.Interactive && !Console.IsInputRedirected;

    /// <summary>
    /// Pide el token sin eco o, si la entrada no es un terminal (un script, una tuberia), lee una
    /// linea de stdin: asi nunca va en la linea de ordenes. null si no hay de donde leerlo.
    /// </summary>
    public static string? ReadToken(string what)
    {
        if (Console.IsInputRedirected)
        {
            // PowerShell 5.1 con $OutputEncoding en UTF-8 manda la tuberia con BOM: no es parte del token.
            var line = Console.ReadLine()?.TrimStart('﻿').Trim();
            return string.IsNullOrEmpty(line) ? null : line;
        }
        if (!AnsiConsole.Profile.Capabilities.Interactive) return null;
        var token = AnsiConsole.Prompt(new TextPrompt<string>($"{what}:").Secret().AllowEmpty()).Trim();
        return token.Length == 0 ? null : token;
    }

    /// <summary>
    /// Un servidor como lo escribe el usuario: <c>git.acme.es</c>, <c>git.acme.es:2222</c>,
    /// <c>[git.acme.es]:2222</c> o la direccion SSH de un repositorio. <paramref name="port"/> gana al de dentro.
    /// </summary>
    public static (string Host, int? Port) ParseHost(string text, int? port)
    {
        var value = text.Trim();
        if (value.Contains('@') || value.Contains("://", StringComparison.Ordinal))
        {
            var url = GitUrl.Parse(value);
            if (url.Host is null) throw new RoostException($"'{text}' es una carpeta local, no un servidor.");
            return (url.Host, port ?? url.Port);
        }
        if (value.StartsWith('[') && value.IndexOf("]:", StringComparison.Ordinal) is var close and > 0
            && int.TryParse(value[(close + 2)..], out var bracketed))
            return (value[1..close].ToLowerInvariant(), port ?? bracketed);
        var colon = value.LastIndexOf(':');
        if (colon > 0 && value.IndexOf(':') == colon && int.TryParse(value[(colon + 1)..], out var after))
            return (value[..colon].ToLowerInvariant(), port ?? after);
        if (!KnownHosts.IsValidHost(value)) throw new RoostException($"'{text}' no vale como servidor: pon su nombre (git.acme.es), con :puerto si no es el 22.");
        return (value.ToLowerInvariant(), port);
    }

    /// <summary>La clave de serie que se ensena y se acepta de un servidor desconocido: la mas fuerte (ed25519, ecdsa, rsa).</summary>
    public static SshHostKey Preferred(IReadOnlyList<SshHostKey> keys) => keys[0];

    /// <summary>
    /// Que Roost pueda conectar con el servidor sin fiarse a ciegas. Los de serie (GitHub, GitLab,
    /// Bitbucket) valen sin preguntar; uno ya aceptado, tambien. Uno desconocido: se le pregunta su
    /// clave (ssh-keyscan), se calcula la huella y se ensena; se acepta si el usuario dice que si o si
    /// coincide con <paramref name="fingerprint"/>. Con <paramref name="yes"/> solo se aceptan las de
    /// serie. Sin terminal, nunca se pregunta: falla con la huella en el texto.
    /// </summary>
    /// <returns>null si se puede seguir; si no, el texto de por que no.</returns>
    public static async Task<string?> EnsureTrustedAsync(KnownHosts hosts, string host, int? port, bool yes, string? fingerprint, bool quiet, CancellationToken cancellation)
    {
        var status = hosts.Status(host, port);
        switch (status.Trust)
        {
            case HostTrust.Published:
                hosts.EnsurePublished();
                if (!quiet)
                    AnsiConsole.MarkupLineInterpolated($"[grey]Huella de {status.Token}: la que publica {GitUrl.NameOf(status.Published!.Provider)} ({status.Published.Keys[0].Algorithm} {status.Published.Keys[0].Fingerprint}), de serie en Roost.[/]");
                return null;
            case HostTrust.Trusted when fingerprint is null:
                if (!quiet)
                    AnsiConsole.MarkupLineInterpolated($"[grey]Huella de {status.Token}: {status.Stored[0].Key.Algorithm} {status.Stored[0].Key.Fingerprint}, aceptada {(KnownHosts.AcceptedOn(status.Stored[0]) is { } on ? "el " + on : "antes")}.[/]");
                return null;
            case HostTrust.Mismatch:
                return $"Lo que Roost tiene guardado para {status.Token} no coincide con la huella que publica {GitUrl.NameOf(status.Published!.Provider)}. No se conecta. "
                    + $"Si no lo cambiaste tu a proposito, olvida lo guardado (roost git hosts remove {status.Token}) y vuelve la publicada.";
        }

        if (!quiet && status.Trust == HostTrust.Unknown)
            AnsiConsole.MarkupLineInterpolated($"Roost no conoce {status.Token}. Preguntandole por su clave (ssh-keyscan)...");
        else if (!quiet)
            AnsiConsole.MarkupLineInterpolated($"Preguntando a {status.Token} por su clave (ssh-keyscan)...");
        var keys = await hosts.ScanAsync(host, port, cancellation);
        var key = Preferred(keys);
        var shown = $"{key.Algorithm} {key.Fingerprint}";

        if (fingerprint is not null)
        {
            var wanted = fingerprint.Trim();
            if (!wanted.StartsWith("SHA256:", StringComparison.Ordinal)) wanted = "SHA256:" + wanted;
            var match = keys.FirstOrDefault(k => k.Fingerprint == wanted.TrimEnd('='));
            if (match is null)
                return $"La huella de {status.Token} ({string.Join(", ", keys.Select(k => $"{k.Algorithm} {k.Fingerprint}"))}) no es la que has dado ({wanted}). No se ha guardado nada.";
            hosts.Trust(host, port, [match]);
            if (!quiet) AnsiConsole.MarkupLineInterpolated($"[green]Coincide.[/] Roost recordara {status.Token} ({match.Algorithm} {match.Fingerprint}).");
            return null;
        }

        var notice = $"Huella de {status.Token} ({key.Algorithm}): {key.Fingerprint}";
        if (yes || !Interactive)
            return $"{notice}. Roost no puede comprobar esta huella: comparala con la que te de quien administra {host} y, si coincide, "
                + $"aceptala con roost git hosts add {status.Token} --fingerprint {key.Fingerprint}" + (yes ? " (--yes solo acepta las de GitHub, GitLab y Bitbucket)." : ".");

        AnsiConsole.MarkupLineInterpolated($"[yellow]{notice}[/]");
        AnsiConsole.MarkupLineInterpolated($"[grey]Roost no puede comprobar esta huella. Comparala con la que te de quien administra {host}.[/]");
        if (!AnsiConsole.Confirm("Confiar en este servidor?", defaultValue: false))
            return $"No se ha aceptado {status.Token}: no se ha guardado nada.";
        hosts.Trust(host, port, [key]);
        AnsiConsole.MarkupLineInterpolated($"[green]Roost recordara {status.Token}[/] [grey]({shown}, en {hosts.FilePath})[/]");
        return null;
    }

    /// <summary>La clave publica para copiarla, con donde pegarla segun el proveedor.</summary>
    public static void ShowPublicKey(SshKeyInfo key, GitUrl? url)
    {
        AnsiConsole.MarkupLineInterpolated($"[bold]Clave publica de {key.Name}[/] [grey]({key.Algorithm} {key.Fingerprint})[/]");
        // Sin markup ni ajuste: que se pueda copiar tal cual.
        Console.WriteLine(key.PublicKey);
        if (url?.DeployKeysUrl is { } page)
        {
            AnsiConsole.MarkupLineInterpolated($"Pegala en las deploy keys de {url.Label}: [link]{page}[/]");
            AnsiConsole.MarkupLine(url.Provider switch
            {
                GitProvider.GitHub => "[grey]En GitHub: Add deploy key, pega la clave y deja «Allow write access» sin marcar.[/]",
                GitProvider.GitLab => "[grey]En GitLab: Add new key, pega la clave y deja «Grant write permissions» sin marcar.[/]",
                GitProvider.Bitbucket => "[grey]En Bitbucket: Add key (las claves de acceso son de solo lectura).[/]",
                _ => "[grey]Solo con permiso de lectura.[/]",
            });
        }
        else
        {
            AnsiConsole.MarkupLine("[grey]Pegala como deploy key (de solo lectura) del repositorio: en GitHub, Settings > Deploy keys; en GitLab, Settings > Repository > Deploy keys; en Bitbucket, Repository settings > Access keys.[/]");
        }
        AnsiConsole.MarkupLineInterpolated($"[grey]La privada esta en {key.PrivateFile}, sin contrasena y solo legible por tu usuario.[/]");
    }
}
