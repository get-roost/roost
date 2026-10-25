using Roost.Core.Scanning;

namespace Roost.Core.Share;

/// <summary>
/// Copiar un texto (la URL de <c>roost share</c>) al portapapeles con lo que tenga el sistema: en
/// Windows, <c>clip.exe</c>; en Linux, <c>wl-copy</c> (Wayland, paquete wl-clipboard) o <c>xclip</c> (X11).
/// Sin escritorio (una terminal por SSH, WSL sin WSLg) o sin esas herramientas no se copia nada y se
/// dice por qué: no es un error, la URL sale igual en pantalla. Lo puro es <see cref="Resolve"/>.
/// </summary>
public static class Clipboard
{
    /// <summary>Qué se puede hacer: la orden que copia (le llega el texto por stdin), o por qué no hay ninguna.</summary>
    public sealed record Choice(CommandSpec? Command, string? WhyNot)
    {
        public bool Available => Command is not null;
    }

    /// <summary>
    /// PURO: la orden de este sistema según el entorno (<c>WAYLAND_DISPLAY</c>, <c>DISPLAY</c>) y qué herramienta
    /// hay en el PATH, o por qué no se puede copiar (sin tildes: lo enseña la CLI).
    /// </summary>
    public static Choice Resolve(bool windows, Func<string, string?> env, Func<string, string?> findTool, string? systemDirectory = null)
    {
        if (windows)
        {
            var clip = Path.Combine(systemDirectory ?? Environment.SystemDirectory, "clip.exe");
            return new Choice(new CommandSpec(clip, [], Timeout: TimeSpan.FromSeconds(5)), null);
        }

        var wayland = !string.IsNullOrEmpty(env("WAYLAND_DISPLAY"));
        var x11 = !string.IsNullOrEmpty(env("DISPLAY"));
        if (!wayland && !x11)
            return new Choice(null, "sin escritorio (ni WAYLAND_DISPLAY ni DISPLAY): copiala a mano");

        // Con Wayland, wl-copy; xclip vale en X11 y, por XWayland, también en Wayland.
        if (wayland && findTool("wl-copy") is { } wlCopy)
            return new Choice(new CommandSpec(wlCopy, [], Timeout: TimeSpan.FromSeconds(5)), null);
        if (findTool("xclip") is { } xclip)
            return new Choice(new CommandSpec(xclip, ["-selection", "clipboard"], Timeout: TimeSpan.FromSeconds(5)), null);
        if (findTool("xsel") is { } xsel)
            return new Choice(new CommandSpec(xsel, ["--clipboard", "--input"], Timeout: TimeSpan.FromSeconds(5)), null);
        return new Choice(null, wayland
            ? "no hay wl-copy ni xclip: instala wl-clipboard (sudo apt install wl-clipboard) para que se copie sola"
            : "no hay xclip: instala xclip (sudo apt install xclip) para que se copie sola");
    }

    /// <summary>Lo mismo con el entorno y el PATH de este proceso.</summary>
    public static Choice Resolve() =>
        Resolve(OperatingSystem.IsWindows(), Environment.GetEnvironmentVariable, FindInPath);

    /// <summary>
    /// Copia <paramref name="text"/>: true si se ha copiado; si no, false y por qué (la herramienta que
    /// falta, o que ha fallado). Nunca lanza salvo por cancelación.
    /// </summary>
    public static async Task<(bool Copied, string? WhyNot)> CopyAsync(string text, ICommandRunner runner, CancellationToken cancellation = default) =>
        await CopyAsync(text, Resolve(), runner, cancellation);

    /// <summary>Lo mismo con una elección ya hecha (los tests).</summary>
    public static async Task<(bool Copied, string? WhyNot)> CopyAsync(string text, Choice choice, ICommandRunner runner, CancellationToken cancellation = default)
    {
        if (choice.Command is not { } command) return (false, choice.WhyNot);
        try
        {
            var result = await runner.RunAsync(command with { Stdin = text }, null, cancellation);
            return result.Succeeded ? (true, null) : (false, $"{Path.GetFileName(command.Exe)} ha fallado (codigo {result.ExitCode})");
        }
        catch (RoostException ex)
        {
            return (false, ex.Message);
        }
    }

    /// <summary>Una herramienta por el PATH; su ruta o null.</summary>
    public static string? FindInPath(string name)
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(dir, name);
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }
}
