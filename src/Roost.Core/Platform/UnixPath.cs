namespace Roost.Core.Platform;

/// <summary>
/// Los shims en la terminal de Linux (y macOS): enlaces simbólicos en <c>~/.local/bin</c> que apuntan a
/// los de <c>bin/shims</c> de Roost (<c>php</c>, <c>composer</c>, <c>node</c>, <c>npm</c>, <c>npx</c>), y, si
/// se pide, un bloque en el perfil de la shell que pone esa carpeta la primera en el <c>PATH</c>. Es el
/// equivalente de <see cref="UserPath"/> (el PATH del usuario en el registro de Windows), que aquí no
/// existe: en Linux el PATH lo arma cada shell al abrirse leyendo sus ficheros de perfil.
/// <para>
/// <c>~/.local/bin</c> porque es lo que recomienda la especificación de directorios base de XDG y porque
/// el <c>~/.profile</c> de serie de Debian y Ubuntu ya lo pone el primero en el PATH si la carpeta existe
/// al iniciar sesión: crearla suele bastar. Para zsh, fish o una distro cuyo perfil no lo haga está
/// <c>--profile</c>, que escribe un bloque entre <c># roost:begin</c> y <c># roost:end</c> (idempotente: se
/// sustituye entero, y <c>roost shims remove</c> lo quita sin tocar el resto del fichero).
/// </para>
/// <para>Nunca pisa un fichero del usuario: un <c>~/.local/bin/php</c> que no sea un enlace a los shims
/// de Roost se deja y se dice. Solo se quitan los enlaces que apuntan a los shims de Roost.</para>
/// </summary>
public sealed class UnixPath
{
    public const string BeginMarker = "# roost:begin";
    public const string EndMarker = "# roost:end";

    /// <summary>El fichero propio de fish: <c>conf.d</c> se lee entero al abrir fish, y uno aparte se quita sin editar nada.</summary>
    public const string FishFileName = "roost.fish";

    private readonly string _home;
    private readonly Func<string, string?> _env;

    public UnixPath(string? home = null, Func<string, string?>? env = null)
    {
        _env = env ?? Environment.GetEnvironmentVariable;
        _home = home ?? _env("HOME") ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    }

    /// <summary>La de este usuario.</summary>
    public static UnixPath Current => new();

    /// <summary><c>~/.local/bin</c>: donde van los enlaces y lo que tiene que estar en el PATH.</summary>
    public string LinkDirectory => Path.Combine(_home, ".local", "bin");

    /// <summary>El enlace de una herramienta (<c>~/.local/bin/php</c>).</summary>
    public string LinkPath(string tool) => Path.Combine(LinkDirectory, tool);

    /// <summary>
    /// true si <c>~/.local/bin/&lt;tool&gt;</c> es un enlace que apunta dentro de <paramref name="shimsDirectory"/>
    /// (el de Roost). Un fichero normal o un enlace a otra cosa es del usuario: false.
    /// </summary>
    public bool IsRoostLink(string tool, string shimsDirectory) => TargetOf(LinkPath(tool)) is { } target && Under(target, shimsDirectory);

    /// <summary>Lo que hay en <c>~/.local/bin/&lt;tool&gt;</c> y no es de Roost (para decirlo), o null.</summary>
    public string? Foreign(string tool, string shimsDirectory)
    {
        var link = LinkPath(tool);
        if (!File.Exists(link) && !Directory.Exists(link) && TargetOf(link) is null) return null;
        return IsRoostLink(tool, shimsDirectory) ? null : link;
    }

    /// <summary>
    /// Pone (o renueva) en <c>~/.local/bin</c> los enlaces de <paramref name="tools"/> a los shims de
    /// <paramref name="shimsDirectory"/>. Crea la carpeta si falta. Lo que ya está ahí y no es un enlace de
    /// Roost se respeta: no se enlaza y vuelve en <c>Skipped</c>. Devuelve los enlazados y los respetados.
    /// </summary>
    public (IReadOnlyList<string> Linked, IReadOnlyList<string> Skipped) Link(IEnumerable<string> tools, string shimsDirectory)
    {
        if (OperatingSystem.IsWindows()) throw new RoostException("Los enlaces de ~/.local/bin son de Linux y macOS; en Windows los shims van por el PATH del usuario.");
        var linked = new List<string>();
        var skipped = new List<string>();
        try
        {
            Directory.CreateDirectory(LinkDirectory);
            foreach (var tool in tools)
            {
                var link = LinkPath(tool);
                var target = Path.Combine(shimsDirectory, tool);
                if (Foreign(tool, shimsDirectory) is not null)
                {
                    skipped.Add(tool);
                    continue;
                }
                if (TargetOf(link) == target)
                {
                    linked.Add(tool);
                    continue;
                }
                // Un enlace nuevo con nombre temporal y renombrado encima: nunca hay un rato sin php.
                var temp = Path.Combine(LinkDirectory, $".roost-{tool}-{Guid.NewGuid():N}");
                File.CreateSymbolicLink(temp, target);
                File.Move(temp, link, overwrite: true);
                linked.Add(tool);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new RoostException($"No se pudieron poner los enlaces en {LinkDirectory}: {ex.Message}");
        }
        return (linked, skipped);
    }

    /// <summary>Quita de <c>~/.local/bin</c> los enlaces que apuntan a <paramref name="shimsDirectory"/> (solo esos). Devuelve los quitados.</summary>
    public IReadOnlyList<string> Unlink(string shimsDirectory, IEnumerable<string> tools)
    {
        var removed = new List<string>();
        foreach (var tool in tools)
        {
            if (!IsRoostLink(tool, shimsDirectory)) continue;
            try
            {
                File.Delete(LinkPath(tool));
                removed.Add(tool);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new RoostException($"No se pudo quitar el enlace {LinkPath(tool)}: {ex.Message}");
            }
        }
        return removed;
    }

    /// <summary>Las herramientas de <paramref name="tools"/> que están enlazadas a <paramref name="shimsDirectory"/>.</summary>
    public IReadOnlyList<string> Linked(string shimsDirectory, IEnumerable<string> tools) => tools.Where(t => IsRoostLink(t, shimsDirectory)).ToList();

    /// <summary>PURO: true si <paramref name="path"/> (un PATH de Unix, separado por <c>:</c>) lleva <paramref name="directory"/>.</summary>
    public static bool Contains(string? path, string directory) =>
        Entries(path).Any(e => Same(e, directory));

    /// <summary>PURO: la posición (desde 0) de <paramref name="directory"/> en el PATH, o -1.</summary>
    public static int IndexOf(string? path, string directory) =>
        Entries(path).ToList().FindIndex(e => Same(e, directory));

    /// <summary>true si <c>~/.local/bin</c> está en el PATH de este proceso (el de la terminal que lanzó Roost).</summary>
    public bool IsInPath() => Contains(_env("PATH"), LinkDirectory);

    /// <summary>
    /// PURO: el bloque que pone <c>~/.local/bin</c> el primero en el PATH, para sh, bash y zsh. Con la
    /// guarda del <c>case</c>: si ya está (el <c>~/.profile</c> de Debian lo pone), no se repite.
    /// </summary>
    public static string ShellBlock => $"""
        {BeginMarker} (lo escribe roost shims install --profile; se quita con roost shims remove)
        case ":$PATH:" in *":$HOME/.local/bin:"*) ;; *) PATH="$HOME/.local/bin:$PATH"; export PATH ;; esac
        {EndMarker}
        """.Replace("\r\n", "\n");

    /// <summary>PURO: lo mismo para fish (un fichero propio en <c>~/.config/fish/conf.d</c>).</summary>
    public static string FishBlock => $"""
        {BeginMarker} (lo escribe roost shims install --profile; se quita con roost shims remove)
        fish_add_path --global --prepend --move $HOME/.local/bin
        {EndMarker}
        """.Replace("\r\n", "\n");

    /// <summary>PURO: <paramref name="content"/> con el bloque de Roost (sustituido si ya lo tenía, añadido al final si no).</summary>
    public static string WithBlock(string content, string block)
    {
        var without = WithoutBlock(content);
        if (without.Length > 0 && !without.EndsWith('\n')) without += "\n";
        if (without.Length > 0 && !without.EndsWith("\n\n", StringComparison.Ordinal)) without += "\n";
        return without + block + "\n";
    }

    /// <summary>PURO: <paramref name="content"/> sin el bloque de Roost (y sin la línea en blanco que lo precedía). Sin bloque, tal cual.</summary>
    public static string WithoutBlock(string content)
    {
        var begin = content.IndexOf(BeginMarker, StringComparison.Ordinal);
        if (begin < 0) return content;
        var end = content.IndexOf(EndMarker, begin, StringComparison.Ordinal);
        if (end < 0) return content;   // un bloque abierto sin cierre no se toca
        var stop = end + EndMarker.Length;
        if (stop < content.Length && content[stop] == '\r') stop++;
        if (stop < content.Length && content[stop] == '\n') stop++;
        var start = begin;
        // La línea en blanco que se puso delante del bloque.
        if (start >= 2 && content[start - 1] == '\n' && content[start - 2] == '\n') start--;
        return content[..start] + content[stop..];
    }

    public static bool HasBlock(string content) => content.Contains(BeginMarker, StringComparison.Ordinal);

    /// <summary>
    /// Los ficheros de perfil que tocan a este usuario, con el bloque de cada uno: bash (su
    /// <c>~/.bash_profile</c> si existe, que entonces es el que lee en vez de <c>~/.profile</c>; si no,
    /// <c>~/.profile</c>, que leen también sh y los gestores de sesión), zsh (<c>~/.zprofile</c>, si zsh es
    /// la shell del usuario o ya tiene un <c>~/.zshrc</c>) y fish (su fichero en <c>conf.d</c>, si usa fish).
    /// </summary>
    public IReadOnlyList<(string File, string Block)> ProfileFiles()
    {
        var files = new List<(string, string)>();
        var bashProfile = Path.Combine(_home, ".bash_profile");
        files.Add((File.Exists(bashProfile) ? bashProfile : Path.Combine(_home, ".profile"), ShellBlock));
        var shell = _env("SHELL") ?? "";
        if (shell.EndsWith("/zsh", StringComparison.Ordinal) || File.Exists(Path.Combine(_home, ".zshrc")) || File.Exists(Path.Combine(_home, ".zprofile")))
            files.Add((Path.Combine(_home, ".zprofile"), ShellBlock));
        if (shell.EndsWith("/fish", StringComparison.Ordinal) || Directory.Exists(Path.Combine(_home, ".config", "fish")))
            files.Add((FishFile, FishBlock));
        return files;
    }

    public string FishFile => Path.Combine(_home, ".config", "fish", "conf.d", FishFileName);

    /// <summary>Escribe el bloque en cada perfil que toca (<see cref="ProfileFiles"/>). Devuelve los ficheros tocados.</summary>
    public IReadOnlyList<string> InstallProfile()
    {
        var touched = new List<string>();
        foreach (var (file, block) in ProfileFiles())
        {
            try
            {
                var current = File.Exists(file) ? File.ReadAllText(file) : "";
                var updated = WithBlock(current, block);
                if (updated == current) continue;
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                File.WriteAllText(file, updated);
                touched.Add(file);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new RoostException($"No se pudo escribir en {file}: {ex.Message}");
            }
        }
        return touched;
    }

    /// <summary>Quita el bloque de todos los perfiles en los que esté (también el fichero de fish). Devuelve los tocados.</summary>
    public IReadOnlyList<string> RemoveProfile()
    {
        var touched = new List<string>();
        foreach (var file in Candidates())
        {
            try
            {
                if (!File.Exists(file)) continue;
                if (file == FishFile)
                {
                    if (!HasBlock(File.ReadAllText(file))) continue;
                    File.Delete(file);
                    touched.Add(file);
                    continue;
                }
                var current = File.ReadAllText(file);
                var updated = WithoutBlock(current);
                if (updated == current) continue;
                File.WriteAllText(file, updated);
                touched.Add(file);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new RoostException($"No se pudo quitar el bloque de {file}: {ex.Message}");
            }
        }
        return touched;
    }

    /// <summary>Los perfiles que tienen el bloque de Roost.</summary>
    public IReadOnlyList<string> ProfilesWithBlock()
    {
        var found = new List<string>();
        foreach (var file in Candidates())
        {
            try
            {
                if (File.Exists(file) && HasBlock(File.ReadAllText(file))) found.Add(file);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return found;
    }

    private IEnumerable<string> Candidates() =>
        new[] { ".profile", ".bash_profile", ".bash_login", ".zprofile" }.Select(f => Path.Combine(_home, f)).Append(FishFile);

    /// <summary>A qué apunta un enlace (tal cual, sin resolver), o null si no es un enlace.</summary>
    private static string? TargetOf(string link)
    {
        try
        {
            return new FileInfo(link).LinkTarget;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static bool Under(string target, string directory)
    {
        var dir = directory.TrimEnd('/');
        return target.StartsWith(dir + "/", StringComparison.Ordinal);
    }

    private static IEnumerable<string> Entries(string? path) =>
        (path ?? "").Split(':', StringSplitOptions.RemoveEmptyEntries).Select(e => e.Trim()).Where(e => e.Length > 0);

    private static bool Same(string a, string b) =>
        string.Equals(a.TrimEnd('/'), b.TrimEnd('/'), StringComparison.Ordinal);
}
