using Roost.Core.Models;
using Roost.Core.Platform;

namespace Roost.Core.Scanning.Scanners;

/// <summary>
/// Descubre servidores MariaDB. El binario moderno es <c>mariadbd</c>, pero las ramas
/// viejas (y Laragon) todavía lo llaman <c>mysqld</c>; por eso en las rutas de MariaDB
/// se prueban los dos nombres y el parser confirma por el banner "MariaDB" cuál lo es.
/// </summary>
public sealed class MariaDbScanner : DatabaseScannerBase
{
    public MariaDbScanner(IProcessRunner runner) : base(runner) { }

    public override ComponentKind Kind => ComponentKind.MariaDb;

    protected override IReadOnlyList<string> PathExecutableNames =>
        OperatingSystem.IsWindows() ? ["mariadbd.exe"] : ["mariadbd"];

    protected override IEnumerable<Candidate> EnumerateCandidates() =>
        SkipLegacyAliases(OperatingSystem.IsWindows() ? WindowsCandidates() : PosixCandidates());

    /// <summary>
    /// Desde la 10.5, MariaDB trae el servidor con los dos nombres, <c>mariadbd</c> y
    /// <c>mysqld</c>, en la misma carpeta: es UNA instalación, no dos. Si junto a un
    /// <c>mysqld</c> existe <c>mariadbd</c>, el <c>mysqld</c> se descarta. Un <c>mysqld</c>
    /// solo (MariaDB antigua) se conserva.
    /// </summary>
    public static IEnumerable<Candidate> SkipLegacyAliases(IEnumerable<Candidate> candidates)
    {
        foreach (var candidate in candidates)
        {
            if (!HasModernSibling(candidate.Path))
                yield return candidate;
        }
    }

    private static bool HasModernSibling(string path)
    {
        try
        {
            var name = Path.GetFileNameWithoutExtension(path);
            if (!name.Equals("mysqld", StringComparison.OrdinalIgnoreCase)) return false;

            var dir = Path.GetDirectoryName(path);
            return !string.IsNullOrEmpty(dir)
                && File.Exists(Path.Combine(dir, "mariadbd" + Path.GetExtension(path)));
        }
        catch
        {
            return false; // ruta rara: mejor duplicar que perder la instalación
        }
    }

    private static IEnumerable<Candidate> WindowsCandidates()
    {
        // Lo instalado por el propio Roost (roost db install mariadb@…).
        foreach (var c in RoostCandidates("mariadb", @"bin\mariadbd.exe"))
            yield return c;

        // Raíces conocidas primero (conservan el origen); el PATH queda de reserva.
        foreach (var drive in WindowsPaths.Drives())
        {
            // Laragon: tanto el binario moderno como el heredado dentro de bin\mariadb.
            foreach (var p in Globber.Expand(drive + @"laragon\bin\mariadb\*\bin\mariadbd.exe"))
                yield return new Candidate(p, "Laragon");
            foreach (var p in Globber.Expand(drive + @"laragon\bin\mariadb\*\bin\mysqld.exe"))
                yield return new Candidate(p, "Laragon");
            foreach (var p in Globber.Expand(drive + @"wamp64\bin\mariadb\*\bin\mariadbd.exe"))
                yield return new Candidate(p, "WAMP");
            foreach (var p in Globber.Expand(drive + @"wamp64\bin\mariadb\*\bin\mysqld.exe"))
                yield return new Candidate(p, "WAMP");
            foreach (var p in Globber.Expand(drive + @"wamp\bin\mariadb\*\bin\mariadbd.exe"))
                yield return new Candidate(p, "WAMP");
        }

        // C:\Program Files\MariaDB 11.4\bin\mariadbd.exe
        foreach (var pf in WindowsPaths.ProgramFiles())
        {
            foreach (var p in Globber.Expand(Path.Combine(pf, @"MariaDB*\bin\mariadbd.exe")))
                yield return new Candidate(p, "Program Files");
            foreach (var p in Globber.Expand(Path.Combine(pf, @"MariaDB*\bin\mysqld.exe")))
                yield return new Candidate(p, "Program Files");
        }

        // PATH al final.
        foreach (var dir in PathProbe.Directories())
        {
            yield return new Candidate(Path.Combine(dir, "mariadbd.exe"), "PATH");
            yield return new Candidate(Path.Combine(dir, "mysqld.exe"), "PATH"); // MariaDB antigua
        }
    }

    private static IEnumerable<Candidate> PosixCandidates()
    {
        foreach (var p in Globber.Expand("/usr/sbin/mariadbd"))
            yield return new Candidate(p, "system");
        foreach (var p in Globber.Expand("/usr/sbin/mysqld")) // puede ser MariaDB; el parser confirma
            yield return new Candidate(p, "system");

        // PATH al final.
        foreach (var dir in PathProbe.Directories())
        {
            yield return new Candidate(Path.Combine(dir, "mariadbd"), "PATH");
            yield return new Candidate(Path.Combine(dir, "mariadb"), "PATH");
            yield return new Candidate(Path.Combine(dir, "mysqld"), "PATH"); // MariaDB antigua
        }
    }
}
