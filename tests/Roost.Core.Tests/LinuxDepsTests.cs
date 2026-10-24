using Roost.Core.Platform;

namespace Roost.Core.Tests;

/// <summary>Las dependencias de la distro (el VcRedist de Linux): lo puro, sin mirar la máquina.</summary>
public class LinuxDepsTests
{
    private const string Noble = "PRETTY_NAME=\"Ubuntu 24.04.3 LTS\"\nNAME=\"Ubuntu\"\nVERSION_ID=\"24.04\"\nID=ubuntu\nID_LIKE=debian\n";
    private const string Jammy = "NAME=\"Ubuntu\"\nVERSION_ID=\"22.04\"\nID=ubuntu\n";
    private const string Bookworm = "NAME=\"Debian GNU/Linux\"\nVERSION_ID=\"12\"\nID=debian\n";
    private const string Trixie = "NAME=\"Debian GNU/Linux\"\nVERSION_ID=\"13\"\nID=debian\n";

    private static LinuxDepsStatus Status(Version? glibc = null, string? missingTool = null, string? missingLibrary = null, string? osRelease = null) =>
        LinuxDeps.Evaluate(
            glibc ?? new Version(2, 39),
            tool => tool == missingTool ? null : "/usr/bin/" + tool,
            lib => lib == missingLibrary || (missingLibrary == "libaio" && lib.StartsWith("libaio", StringComparison.Ordinal)) ? null : "/lib/x86_64-linux-gnu/" + lib,
            osRelease ?? Noble);

    [Fact]
    public void Evaluate_ConTodo_NadaFalta_YNoHayAviso()
    {
        var status = Status();

        Assert.True(status.Checked);
        Assert.Empty(status.Missing);
        Assert.Equal("glibc 2.39", status.DescribeGlibc());
        foreach (var need in Enum.GetValues<LinuxNeed>())
            Assert.Null(LinuxDeps.Warning(status, need));
    }

    [Fact]
    public void Libaio_ValeElT64_YElPaqueteDependeDeLaDistro()
    {
        // En la 24.04 solo está libaio.so.1t64: para la sonda es que libaio está (el enlace lo hará el instalador de MySQL).
        var status = LinuxDeps.Evaluate(new Version(2, 39), _ => "/usr/bin/x", lib => lib == "libaio.so.1t64" ? "/lib/x86_64-linux-gnu/libaio.so.1t64" : lib == "libaio.so.1" ? null : "/lib/" + lib, Noble);
        Assert.True(status.Has(LinuxDeps.LibAio));
        Assert.EndsWith("libaio.so.1t64", status[LinuxDeps.LibAio]!.Found);

        Assert.Equal("libaio1t64", LinuxDeps.LibaioPackage(Noble));
        Assert.Equal("libaio1", LinuxDeps.LibaioPackage(Jammy));
        Assert.Equal("libaio1", LinuxDeps.LibaioPackage(Bookworm));
        Assert.Equal("libaio1t64", LinuxDeps.LibaioPackage(Trixie));
        Assert.Contains("libaio1t64", LinuxDeps.LibaioPackage(null));
        Assert.Contains("libaio1", LinuxDeps.LibaioPackage("ID=fedora\nVERSION_ID=41\n"));
    }

    [Fact]
    public void Warning_MySql_NombraLoQueFalta_YUnaSolaOrdenDeApt()
    {
        var status = LinuxDeps.Evaluate(new Version(2, 39), tool => tool == "xz" ? null : "/usr/bin/" + tool, lib => lib.StartsWith("libaio", StringComparison.Ordinal) ? null : "/lib/" + lib, Noble);

        var warning = LinuxDeps.Warning(status, LinuxNeed.MySql);

        Assert.NotNull(warning);
        Assert.StartsWith("Faltan en esta distro xz (para extraer el tar.xz de MySQL), libaio (para que arranque MySQL). Instala: sudo apt install xz-utils libaio1t64", warning);
        Assert.Null(LinuxDeps.Warning(status, LinuxNeed.Php));            // a PHP no le hace falta nada de eso
        Assert.Null(LinuxDeps.Warning(status, LinuxNeed.PrivilegedPorts));
    }

    [Fact]
    public void Warning_Glibc_DemasiadoVieja_SegunQuienLaPide()
    {
        var old = Status(glibc: new Version(2, 17));
        Assert.Null(LinuxDeps.Warning(old, LinuxNeed.Php));          // la 2.17 justa vale para el PHP de roost-builds
        Assert.Contains("MariaDB necesita la 2.19", LinuxDeps.Warning(old, LinuxNeed.MariaDb));
        Assert.Contains("MySQL 8 necesita la 2.28", LinuxDeps.Warning(old, LinuxNeed.MySql));

        var ancient = Status(glibc: new Version(2, 12));
        Assert.Contains("el PHP de Roost necesita la 2.17", LinuxDeps.Warning(ancient, LinuxNeed.Php));
    }

    [Fact]
    public void Warning_SetcapYCertutil_ConSuPaquete()
    {
        Assert.Equal("Falta en esta distro setcap (para que nginx escuche en el 80 y el 443 sin root). Instala: sudo apt install libcap2-bin",
            LinuxDeps.Warning(Status(missingTool: "setcap"), LinuxNeed.PrivilegedPorts));
        Assert.Equal("Falta en esta distro certutil (para que Chrome y Firefox confien en la CA de Roost). Instala: sudo apt install libnss3-tools",
            LinuxDeps.Warning(Status(missingTool: "certutil"), LinuxNeed.CertTrust));
    }

    [Fact]
    public void MissingLibraryHint_TraduceElErrorDelBinario_AlPaquete()
    {
        var mysqld = "bin/mysqld: error while loading shared libraries: libaio.so.1: cannot open shared object file: No such file or directory\n";

        Assert.Equal("Falta la libreria libaio.so.1. Instalala: sudo apt install libaio1t64", LinuxDeps.MissingLibraryHint(mysqld, Noble));
        Assert.Equal("Falta la libreria libaio.so.1. Instalala: sudo apt install libaio1", LinuxDeps.MissingLibraryHint(mysqld, Jammy));
        Assert.Contains("apt-file search libfoo.so.9", LinuxDeps.MissingLibraryHint("x: error while loading shared libraries: libfoo.so.9: cannot open shared object file", Noble));
        Assert.Null(LinuxDeps.MissingLibraryHint("mysqld  Ver 8.4.11", Noble));
        Assert.Null(LinuxDeps.MissingLibraryHint(null, Noble));
    }

    [Theory]
    [InlineData("libnuma.so.1", "libnuma1")]
    [InlineData("libncurses.so.6", "libncurses6")]
    [InlineData("libtinfo.so.5", "libncurses5")]
    [InlineData("libxml2.so.2", "libxml2")]
    [InlineData("libssl.so.3", "libssl3")]
    [InlineData("libzzz.so.1", null)]
    public void PackageOf_LasLibreriasQuePidenLosBinarios(string library, string? package) =>
        Assert.Equal(package, LinuxDeps.PackageOf(library, Noble));

    [Fact]
    public void Detect_FueraDeLinux_NoSeSabe_YNoSeAvisa()
    {
        if (OperatingSystem.IsLinux()) return;
        var status = LinuxDeps.Detect();
        Assert.False(status.Checked);
        Assert.Null(LinuxDeps.Warning(status, LinuxNeed.MySql));
    }

    [LinuxFact]
    public void Detect_EnLinux_LeeLaGlibcYLasHerramientas()
    {
        var status = LinuxDeps.Detect();

        Assert.True(status.Checked);
        Assert.NotNull(status.Glibc);
        Assert.True(status.Glibc >= LinuxDeps.PhpMinimumGlibc);
        Assert.True(status.Has(LinuxDeps.Tar));   // tar está en cualquier distro
        Assert.Null(LinuxDeps.Warning(status, LinuxNeed.Php));
    }
}
