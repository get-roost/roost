using System.Text.RegularExpressions;

namespace Roost.Core.Platform;

/// <summary>Para qué hace falta comprobar las dependencias (cada cosa pide las suyas).</summary>
public enum LinuxNeed
{
    /// <summary>El PHP de roost-builds (glibc ≥ 2.17).</summary>
    Php,
    /// <summary>MySQL de dev.mysql.com (glibc ≥ 2.28, libaio, libnuma, tar y xz para su tar.xz).</summary>
    MySql,
    /// <summary>MariaDB de downloads.mariadb.org (glibc ≥ 2.19).</summary>
    MariaDb,
    /// <summary>nginx en el 80 y el 443 sin root (setcap y getcap, de libcap2-bin).</summary>
    PrivilegedPorts,
    /// <summary>Que Chrome y Firefox confíen en la CA de Roost (certutil, de libnss3-tools).</summary>
    CertTrust,
}

/// <summary>Una dependencia del sistema: si está, dónde, y el paquete apt que la trae si falta.</summary>
public sealed record LinuxDep(string Name, bool Present, string? Found, string AptPackage, string NeededFor);

/// <summary>
/// El estado de las dependencias. <see cref="Checked"/> es false fuera de Linux: entonces no se sabe, y no
/// se avisa (como <see cref="VcRedistStatus"/> en Windows).
/// </summary>
public sealed record LinuxDepsStatus(bool Checked, Version? Glibc, IReadOnlyList<LinuxDep> Deps)
{
    public LinuxDep? this[string name] => Deps.FirstOrDefault(d => d.Name == name);

    public bool Has(string name) => this[name]?.Present == true;

    public IReadOnlyList<LinuxDep> Missing => Deps.Where(d => !d.Present).ToList();

    /// <summary>«glibc 2.39», o «glibc desconocida» (musl, o no se pudo leer).</summary>
    public string DescribeGlibc() => Glibc is null ? "glibc desconocida" : $"glibc {Glibc}";
}

/// <summary>
/// El equivalente en Linux de <see cref="VcRedist"/>: lo que los binarios que instala Roost necesitan de
/// la distro, y el paquete apt de cada cosa si falta. Comprueba la versión de glibc (la del proceso, por
/// <c>gnu_get_libc_version</c>), <c>tar</c> y <c>xz</c> (el tar.xz de MySQL), <c>setcap</c> y <c>getcap</c>
/// (puertos &lt; 1024), <c>libaio</c> (<c>libaio.so.1</c> o, desde Ubuntu 24.04 y Debian 13, <c>libaio.so.1t64</c>:
/// MySQL busca la primera), <c>libnuma</c> y <c>certutil</c>. <see cref="Evaluate"/> es la parte pura, y
/// <see cref="MissingLibraryHint"/> traduce el «error while loading shared libraries» de un binario al paquete que falta.
/// </summary>
public static class LinuxDeps
{
    public static readonly Version PhpMinimumGlibc = new(2, 17);
    public static readonly Version MariaDbMinimumGlibc = new(2, 19);
    public static readonly Version MySqlMinimumGlibc = new(2, 28);

    public const string Tar = "tar";
    public const string Xz = "xz";
    public const string SetCap = "setcap";
    public const string GetCap = "getcap";
    public const string CertUtil = "certutil";
    public const string LibAio = "libaio";
    public const string LibNuma = "libnuma";

    /// <summary>Donde suelen estar las librerías en Debian, Ubuntu y las RPM (sin ejecutar ldconfig).</summary>
    public static readonly IReadOnlyList<string> LibraryDirs =
    [
        "/lib/x86_64-linux-gnu", "/usr/lib/x86_64-linux-gnu", "/lib64", "/usr/lib64", "/usr/lib", "/lib", "/usr/local/lib",
    ];

    /// <summary>Donde se buscan las herramientas, además del PATH (los <c>sbin</c> no siempre están en el PATH de un usuario).</summary>
    public static readonly IReadOnlyList<string> ToolDirs = ["/usr/sbin", "/sbin", "/usr/bin", "/bin", "/usr/local/sbin", "/usr/local/bin"];

    public static LinuxDepsStatus Detect()
    {
        if (!OperatingSystem.IsLinux()) return new LinuxDepsStatus(false, null, []);
        return Evaluate(Posix.GlibcVersion(), FindTool, FindLibrary, OsRelease());
    }

    /// <summary>El <c>/etc/os-release</c> de esta distro, o null (fuera de Linux, o si no se puede leer).</summary>
    public static string? OsRelease()
    {
        if (!OperatingSystem.IsLinux()) return null;
        try { return File.ReadAllText("/etc/os-release"); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>
    /// PURO: el estado a partir de la glibc, de cómo se encuentra cada herramienta (su ruta o null) y cada
    /// librería (la ruta del <c>.so</c> que haya, o null), y del <c>/etc/os-release</c> (para el nombre del paquete de libaio).
    /// </summary>
    public static LinuxDepsStatus Evaluate(Version? glibc, Func<string, string?> findTool, Func<string, string?> findLibrary, string? osRelease)
    {
        var deps = new List<LinuxDep>();
        void Tool(string name, string package, string neededFor)
        {
            var found = findTool(name);
            deps.Add(new LinuxDep(name, found is not null, found, package, neededFor));
        }
        Tool(Tar, "tar", "extraer MySQL y MariaDB");
        Tool(Xz, "xz-utils", "extraer el tar.xz de MySQL");
        Tool(SetCap, "libcap2-bin", "que nginx escuche en el 80 y el 443 sin root");
        Tool(GetCap, "libcap2-bin", "que nginx escuche en el 80 y el 443 sin root");
        Tool(CertUtil, "libnss3-tools", "que Chrome y Firefox confien en la CA de Roost");

        var aio = findLibrary("libaio.so.1") ?? findLibrary("libaio.so.1t64");
        deps.Add(new LinuxDep(LibAio, aio is not null, aio, LibaioPackage(osRelease), "que arranque MySQL"));
        var numa = findLibrary("libnuma.so.1");
        deps.Add(new LinuxDep(LibNuma, numa is not null, numa, "libnuma1", "que arranque MySQL"));
        return new LinuxDepsStatus(true, glibc, deps);
    }

    /// <summary>
    /// PURO: el aviso para el usuario (sin tildes: lo enseña la CLI), o null si no falta nada para
    /// <paramref name="need"/>. Si faltan varias cosas, las junta en una sola orden de apt.
    /// </summary>
    public static string? Warning(LinuxDepsStatus status, LinuxNeed need)
    {
        if (!status.Checked) return null;
        var problems = new List<string>();
        var packages = new List<string>();
        void Need(string dep)
        {
            if (status[dep] is { Present: false } missing)
            {
                problems.Add($"{missing.Name} (para {missing.NeededFor})");
                if (!packages.Contains(missing.AptPackage)) packages.Add(missing.AptPackage);
            }
        }

        switch (need)
        {
            case LinuxNeed.Php:
                if (status.Glibc is { } g && g < PhpMinimumGlibc)
                    return $"Esta distro tiene glibc {g} y el PHP de Roost necesita la {PhpMinimumGlibc} o posterior: no va a arrancar. Hace falta una distro mas nueva.";
                return null;
            case LinuxNeed.MariaDb:
                if (status.Glibc is { } gm && gm < MariaDbMinimumGlibc)
                    return $"Esta distro tiene glibc {gm} y MariaDB necesita la {MariaDbMinimumGlibc} o posterior: no va a arrancar.";
                Need(Tar);
                break;
            case LinuxNeed.MySql:
                if (status.Glibc is { } gs && gs < MySqlMinimumGlibc)
                    return $"Esta distro tiene glibc {gs} y MySQL 8 necesita la {MySqlMinimumGlibc} o posterior: no va a arrancar. MariaDB si vale (roost db install mariadb).";
                Need(Tar);
                Need(Xz);
                Need(LibAio);
                Need(LibNuma);
                break;
            case LinuxNeed.PrivilegedPorts:
                Need(SetCap);
                Need(GetCap);
                break;
            case LinuxNeed.CertTrust:
                Need(CertUtil);
                break;
        }

        if (problems.Count == 0) return null;
        return $"Falta{(problems.Count == 1 ? "" : "n")} en esta distro {string.Join(", ", problems.Distinct())}. Instala: {AptInstall(packages)}";
    }

    /// <summary>
    /// PURO: si la salida de un binario que no arranca dice qué librería le falta («error while loading
    /// shared libraries: libaio.so.1: cannot open shared object file»), el aviso con el paquete apt que la
    /// trae; null si no es eso.
    /// </summary>
    public static string? MissingLibraryHint(string? output, string? osRelease = null)
    {
        if (output is null) return null;
        var match = MissingLibrary.Match(output);
        if (!match.Success) return null;
        var library = match.Groups[1].Value;
        var package = PackageOf(library, osRelease);
        return package is null
            ? $"Falta la libreria {library}. Busca el paquete que la trae (apt-file search {library}) e instalalo."
            : $"Falta la libreria {library}. Instalala: {AptInstall([package])}";
    }

    private static readonly Regex MissingLibrary = new(@"error while loading shared libraries: ([^:\s]+): cannot open shared object file", RegexOptions.CultureInvariant);

    /// <summary>PURO: el paquete de Debian/Ubuntu que trae una librería de las que piden los binarios de Roost; null si no se sabe.</summary>
    public static string? PackageOf(string library, string? osRelease = null)
    {
        if (library.StartsWith("libaio.so", StringComparison.Ordinal)) return LibaioPackage(osRelease);
        if (library.StartsWith("libnuma.so", StringComparison.Ordinal)) return "libnuma1";
        if (library.StartsWith("libncurses.so.6", StringComparison.Ordinal) || library.StartsWith("libtinfo.so.6", StringComparison.Ordinal)) return "libncurses6";
        if (library.StartsWith("libncurses.so.5", StringComparison.Ordinal) || library.StartsWith("libtinfo.so.5", StringComparison.Ordinal)) return "libncurses5";
        if (library.StartsWith("libxml2.so", StringComparison.Ordinal)) return "libxml2";
        if (library.StartsWith("libssl.so.3", StringComparison.Ordinal) || library.StartsWith("libcrypto.so.3", StringComparison.Ordinal)) return "libssl3";
        if (library.StartsWith("libcrypt.so", StringComparison.Ordinal)) return "libcrypt1";
        if (library.StartsWith("libstdc++.so", StringComparison.Ordinal)) return "libstdc++6";
        if (library.StartsWith("libsystemd.so", StringComparison.Ordinal)) return "libsystemd0";
        if (library.StartsWith("libpcre2", StringComparison.Ordinal)) return "libpcre2-8-0";
        if (library.StartsWith("libz.so", StringComparison.Ordinal)) return "zlib1g";
        return null;
    }

    /// <summary>
    /// PURO: el paquete de libaio según la distro: <c>libaio1t64</c> desde Ubuntu 24.04 y Debian 13 (la
    /// transición a time_t de 64 bits, que renombró la librería a <c>libaio.so.1t64</c>) y <c>libaio1</c> antes.
    /// Sin saber la distro, los dos.
    /// </summary>
    public static string LibaioPackage(string? osRelease)
    {
        if (osRelease is null) return "libaio1t64 (o libaio1 en distros anteriores a 2024)";
        var id = Value(osRelease, "ID");
        var version = Value(osRelease, "VERSION_ID");
        if (id == "ubuntu" && Version.TryParse(version, out var ubuntu)) return ubuntu >= new Version(24, 4) ? "libaio1t64" : "libaio1";
        if (id == "debian" && int.TryParse(version, out var debian)) return debian >= 13 ? "libaio1t64" : "libaio1";
        return "libaio1t64 (o libaio1 en distros anteriores a 2024)";
    }

    private static string? Value(string osRelease, string key)
    {
        foreach (var line in osRelease.Split('\n'))
        {
            if (line.StartsWith(key + "=", StringComparison.Ordinal))
                return line[(key.Length + 1)..].Trim().Trim('"');
        }
        return null;
    }

    /// <summary>PURO: «sudo apt install a b».</summary>
    public static string AptInstall(IReadOnlyList<string> packages) => "sudo apt install " + string.Join(' ', packages);

    /// <summary>Una herramienta por el PATH y los <c>sbin</c>; su ruta o null.</summary>
    public static string? FindTool(string name)
    {
        var dirs = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(':', StringSplitOptions.RemoveEmptyEntries).Concat(ToolDirs);
        foreach (var dir in dirs)
        {
            var candidate = Path.Combine(dir, name);
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    /// <summary>Una librería por su nombre de fichero en <see cref="LibraryDirs"/>; su ruta o null.</summary>
    public static string? FindLibrary(string fileName)
    {
        foreach (var dir in LibraryDirs)
        {
            var candidate = Path.Combine(dir, fileName);
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }
}
