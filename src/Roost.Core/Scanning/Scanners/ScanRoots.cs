using Roost.Core.Platform;

namespace Roost.Core.Scanning.Scanners;

/// <summary>
/// Dónde buscan los scanners de nginx y Apache: las unidades, «Archivos de programa», el perfil,
/// la raíz de Roost, el PATH y los ejecutables de los servicios de Windows. Por defecto, los de esta
/// máquina (<see cref="Machine"/>); los tests ponen carpetas temporales, así que el orden de los
/// candidatos y el origen de cada uno se prueban sin tocar la máquina. En POSIX, las rutas típicas
/// cuelgan de <see cref="PosixRoot"/> (<c>/</c> de verdad).
/// </summary>
public sealed record ScanRoots
{
    /// <summary>Raíces de unidad (<c>C:\</c>, <c>D:\</c>…), donde viven Laragon, XAMPP, WAMP o un <c>C:\Apache24</c> suelto.</summary>
    public IReadOnlyList<string> Drives { get; init; } = [];

    public IReadOnlyList<string> ProgramFiles { get; init; } = [];

    /// <summary><c>%USERPROFILE%</c> (scoop); null si no hay.</summary>
    public string? UserProfile { get; init; }

    /// <summary><c>%APPDATA%</c>, donde instala Apache el paquete de chocolatey si no se le dice otra cosa.</summary>
    public string? AppData { get; init; }

    /// <summary>La raíz de Roost (lo instalado con <c>roost up</c> / <c>roost web use</c>); null si no se puede calcular.</summary>
    public string? RoostRoot { get; init; }

    public IReadOnlyList<string> PathDirectories { get; init; } = [];

    /// <summary>Los ejecutables de los servicios de Windows (su <c>ImagePath</c>); se leen al pedirlos.</summary>
    public Func<IReadOnlyList<string>> ServiceExecutables { get; init; } = () => [];

    /// <summary>La raíz de las rutas de distro, Homebrew y <c>/usr/local</c>: <c>/</c> salvo en los tests.</summary>
    public string PosixRoot { get; init; } = "/";

    /// <summary>Los de esta máquina. Nunca lanza: lo que no se puede leer, vacío.</summary>
    public static ScanRoots Machine()
    {
        string? roost = null;
        try { roost = RoostPaths.Default().Root; } catch { /* sin raíz calculable, sin lo de Roost */ }
        string? appData = null;
        try { appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData); } catch { /* sin perfil */ }
        return new ScanRoots
        {
            Drives = OperatingSystem.IsWindows() ? [.. WindowsPaths.Drives()] : [],
            ProgramFiles = OperatingSystem.IsWindows() ? [.. WindowsPaths.ProgramFiles()] : [],
            UserProfile = OperatingSystem.IsWindows() ? WindowsPaths.UserProfile() : null,
            AppData = string.IsNullOrEmpty(appData) ? null : appData,
            RoostRoot = roost,
            PathDirectories = [.. PathProbe.Directories()],
            ServiceExecutables = () => WindowsServices.Executables(),
        };
    }

    /// <summary>
    /// <c>usr/sbin/nginx</c> bajo <see cref="PosixRoot"/>, con el origen que le da su ruta de verdad
    /// (<c>/usr/sbin/nginx</c> es de la distro; <c>/usr/local/…</c> y <c>/opt/…</c>, no).
    /// </summary>
    public IEnumerable<Candidate> Posix(string pattern)
    {
        var relative = pattern.TrimStart('/');
        foreach (var path in Globber.Expand(Path.Combine(PosixRoot, relative)))
        {
            var real = "/" + Path.GetRelativePath(PosixRoot, path).Replace('\\', '/');
            yield return new Candidate(path, PosixOrigin.Of(real));
        }
    }
}
