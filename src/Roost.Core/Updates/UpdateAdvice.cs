namespace Roost.Core.Updates;

/// <summary>Cómo se instaló este Roost, que decide cómo se actualiza.</summary>
public enum InstallChannel
{
    /// <summary>Un zip (o tar.gz) descomprimido a mano: se actualiza bajando el nuevo.</summary>
    Manual,

    /// <summary>Scoop (bucket de este repo): <c>…\scoop\apps\roost\current\</c>.</summary>
    Scoop,

    /// <summary>winget, como portable: <c>…\WinGet\Packages\Roost.Roost_…\</c>.</summary>
    Winget,

    /// <summary>Compilado desde el código (<c>bin\Debug</c> o <c>bin\Release</c>).</summary>
    Source,

    /// <summary>Con el instalador de Windows (Program Files): junto a los exe está su <c>unins000.exe</c>.</summary>
    Installer,
}

/// <summary>
/// Qué hacer para actualizar. <see cref="Command"/> es lo que se escribe en la terminal (null
/// en una instalación manual) y <see cref="Url"/>, lo que hay que abrir o bajar (null si no se
/// sabe aún cuál es la release). La app compone sus propios textos con esto; <see cref="Text"/>
/// es el de la CLI (sin tildes, como el resto de su salida).
/// </summary>
public sealed record UpdateAdvice(InstallChannel Channel, string? Command, Uri? Url, string? Package = null)
{
    public string Text => Channel switch
    {
        InstallChannel.Scoop => $"Instalado con Scoop: {Command}",
        InstallChannel.Winget => $"Instalado con winget: {Command}",
        InstallChannel.Source => $"Compilado desde el codigo: {Command}",
        InstallChannel.Installer when Package is not null && Url is not null => $"Instalado con el instalador: descarga {Package} ({Url}) y ejecutalo; actualiza sin tocar tus datos",
        InstallChannel.Installer => "Instalado con el instalador: descarga el nuevo de la pagina de releases de Roost en GitHub y ejecutalo",
        _ when Package is not null && Url is not null => $"Descarga {Package} ({Url}) y sustituye con su contenido la carpeta de Roost",
        _ when Url is not null => $"Descarga la nueva desde {Url} y sustituye con ella la carpeta de Roost",
        _ => "Descarga la nueva desde la pagina de releases de Roost en GitHub",
    };
}

/// <summary>Cómo actualizar, según desde dónde se ejecuta Roost. PURO salvo <see cref="DetectCurrent"/>.</summary>
public static class UpdateInstructions
{
    public const string ScoopCommand = "scoop update roost";
    public const string WingetCommand = "winget upgrade Roost.Roost";
    public const string SourceCommand = "git pull && dotnet build Roost.slnx";

    /// <summary>
    /// El canal de una ruta (la carpeta del ejecutable o el propio exe). PURO. Mira trozos de
    /// la ruta, no prefijos, porque Scoop y winget pueden vivir en otra unidad o carpeta
    /// (<c>SCOOP</c>, instalación para toda la máquina).
    /// </summary>
    public static InstallChannel Detect(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return InstallChannel.Manual;
        var parts = path.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries);

        for (var i = 0; i + 1 < parts.Length; i++)
        {
            // …\scoop\apps\roost\<versión|current>\  (o un SCOOP propio: …\apps\roost\current\)
            if (Is(parts[i], "apps") && Is(parts[i + 1], "roost")
                && (parts.Take(i).Any(p => Is(p, "scoop")) || (i + 2 < parts.Length && Is(parts[i + 2], "current"))))
                return InstallChannel.Scoop;

            // …\WinGet\Packages\Roost.Roost_Microsoft.Winget.Source_8wekyb3d8bbwe\  y el enlace de …\WinGet\Links\
            if (Is(parts[i], "winget")
                && ((Is(parts[i + 1], "packages") && i + 2 < parts.Length && parts[i + 2].StartsWith("Roost.Roost_", StringComparison.OrdinalIgnoreCase))
                    || Is(parts[i + 1], "links")))
                return InstallChannel.Winget;
        }

        // …\src\Roost.Cli\bin\Debug\net10.0\
        for (var i = 0; i + 1 < parts.Length; i++)
            if (Is(parts[i], "bin") && (Is(parts[i + 1], "debug") || Is(parts[i + 1], "release")))
                return InstallChannel.Source;

        return InstallChannel.Manual;
    }

    /// <summary>
    /// El de este proceso: si junto a los exe está el desinstalador del instalador
    /// (<c>unins000.exe</c>, que deja Inno Setup), el instalador; si no, la carpeta que resolvió
    /// .NET (la real, aunque se lance por un enlace) y, si no dice nada, la ruta con la que se
    /// lanzó (el enlace de winget en <c>WinGet\Links</c>).
    /// </summary>
    public static InstallChannel DetectCurrent()
    {
        try
        {
            if (File.Exists(Path.Combine(AppContext.BaseDirectory, InstallerUninstaller))) return InstallChannel.Installer;
        }
        catch
        {
            // Sin permiso para mirar: se decide por la ruta.
        }
        var channel = Detect(AppContext.BaseDirectory);
        return channel != InstallChannel.Manual ? channel : Detect(Environment.ProcessPath);
    }

    /// <summary>El desinstalador que deja el instalador de Windows junto a los exe.</summary>
    public const string InstallerUninstaller = "unins000.exe";

    /// <summary>Qué hacer para pasar a <paramref name="latest"/> (o a la última, si aún no se sabe cuál es).</summary>
    public static UpdateAdvice For(InstallChannel channel, ReleaseInfo? latest, bool windows)
    {
        var page = latest?.Page;
        return channel switch
        {
            InstallChannel.Scoop => new UpdateAdvice(channel, ScoopCommand, page),
            InstallChannel.Winget => new UpdateAdvice(channel, WingetCommand, page),
            InstallChannel.Source => new UpdateAdvice(channel, SourceCommand, page),
            InstallChannel.Installer => latest?.Installer is { } setup
                ? new UpdateAdvice(channel, null, setup.Url, setup.Name)
                : new UpdateAdvice(channel, null, page),
            _ => latest?.PackageFor(windows) is { } package
                ? new UpdateAdvice(channel, null, page, package.Name)
                : new UpdateAdvice(channel, null, page),
        };
    }

    private static bool Is(string part, string name) => part.Equals(name, StringComparison.OrdinalIgnoreCase);
}
