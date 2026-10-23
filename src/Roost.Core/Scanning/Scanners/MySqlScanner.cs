using Roost.Core.Models;
using Roost.Core.Platform;

namespace Roost.Core.Scanning.Scanners;

/// <summary>
/// Descubre servidores MySQL (<c>mysqld</c>). En Windows barre PATH + Laragon/XAMPP/
/// WAMP + "Archivos de programa\MySQL"; en Linux/macOS barre PATH + <c>/usr/sbin</c>.
/// El parser descarta lo que en realidad sea MariaDB (mismo nombre de binario a veces).
/// </summary>
public sealed class MySqlScanner : DatabaseScannerBase
{
    public MySqlScanner(IProcessRunner runner) : base(runner) { }

    public override ComponentKind Kind => ComponentKind.MySql;

    protected override IReadOnlyList<string> PathExecutableNames =>
        OperatingSystem.IsWindows() ? ["mysqld.exe"] : ["mysqld"];

    protected override IEnumerable<Candidate> EnumerateCandidates() =>
        OperatingSystem.IsWindows() ? WindowsCandidates() : PosixCandidates();

    private static IEnumerable<Candidate> WindowsCandidates()
    {
        // Lo instalado por el propio Roost (roost db install mysql@…).
        foreach (var c in RoostCandidates("mysql", @"bin\mysqld.exe"))
            yield return c;

        // Raíces conocidas primero (conservan el origen); el PATH queda de reserva.
        foreach (var drive in WindowsPaths.Drives())
        {
            foreach (var p in Globber.Expand(drive + @"laragon\bin\mysql\*\bin\mysqld.exe"))
                yield return new Candidate(p, "Laragon");
            foreach (var p in Globber.Expand(drive + @"xampp\mysql\bin\mysqld.exe"))
                yield return new Candidate(p, "XAMPP");
            foreach (var p in Globber.Expand(drive + @"wamp64\bin\mysql\*\bin\mysqld.exe"))
                yield return new Candidate(p, "WAMP");
            foreach (var p in Globber.Expand(drive + @"wamp\bin\mysql\*\bin\mysqld.exe"))
                yield return new Candidate(p, "WAMP");
        }

        // C:\Program Files\MySQL\MySQL Server 8.0\bin\mysqld.exe
        foreach (var pf in WindowsPaths.ProgramFiles())
            foreach (var p in Globber.Expand(Path.Combine(pf, @"MySQL\*\bin\mysqld.exe")))
                yield return new Candidate(p, "Program Files");

        // PATH al final (servidor).
        foreach (var dir in PathProbe.Directories())
            yield return new Candidate(Path.Combine(dir, "mysqld.exe"), "PATH");
    }

    private static IEnumerable<Candidate> PosixCandidates()
    {
        // /usr/sbin, de la distro; /usr/local/mysql, el tarball oficial.
        foreach (var pattern in new[] { "/usr/sbin/mysqld", "/usr/local/mysql/bin/mysqld" })
            foreach (var p in Globber.Expand(pattern))
                yield return new Candidate(p, PosixOrigin.Of(p));

        // PATH al final, y solo el servidor: `mysql` es el cliente, que no es una instalación.
        foreach (var dir in PathProbe.Directories())
            yield return new Candidate(Path.Combine(dir, "mysqld"), "PATH");
    }
}
