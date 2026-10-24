using System.Formats.Tar;
using System.IO.Compression;
using Roost.Core.Install;

namespace Roost.Core.Tests;

/// <summary>
/// La extracción de un .tar.gz (los paquetes de Linux) con las mismas reglas que un zip: nada a medias,
/// la carpeta raíz quitada si lo trae todo dentro, el fichero que se exige y nada fuera de la carpeta;
/// en Linux, además, los permisos y los enlaces simbólicos tal cual.
/// </summary>
public class InstallFilesTarTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "roost-tar-" + Guid.NewGuid().ToString("N"));

    private const UnixFileMode Executable = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                                            | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
                                            | UnixFileMode.OtherRead | UnixFileMode.OtherExecute;

    private string TarGz(string name, params (string Path, string? Content, string? LinkTo, UnixFileMode Mode)[] entries)
    {
        Directory.CreateDirectory(_root);
        var file = Path.Combine(_root, name);
        using var stream = File.Create(file);
        using var gzip = new GZipStream(stream, CompressionLevel.Fastest);
        using var tar = new TarWriter(gzip, TarEntryFormat.Pax);
        foreach (var (path, content, link, mode) in entries)
        {
            var entry = link is null
                ? new PaxTarEntry(TarEntryType.RegularFile, path) { DataStream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(content ?? "")) }
                : new PaxTarEntry(TarEntryType.SymbolicLink, path) { LinkName = link };
            entry.Mode = mode;
            tar.WriteEntry(entry);
        }
        return file;
    }

    [Fact]
    public void TarGz_ConUnaCarpetaRaiz_SeInstalaSuContenido()
    {
        var archive = TarGz("nginx-1.30.6-linux-x86_64.tar.gz",
            ("nginx-1.30.6/nginx", "#!/bin/sh", null, Executable),
            ("nginx-1.30.6/conf/mime.types", "types {}", null, UnixFileMode.UserRead | UnixFileMode.UserWrite));
        var target = Path.Combine(_root, "bin", "nginx", "1.30.6");

        InstallFiles.ExtractAtomic(archive, target, "nginx", stripSingleRoot: true, "nginx-1.30.6-linux-x86_64.tar.gz");

        Assert.True(File.Exists(Path.Combine(target, "nginx")));
        Assert.Equal("types {}", File.ReadAllText(Path.Combine(target, "conf", "mime.types")));
        Assert.Empty(Directory.GetDirectories(Path.GetDirectoryName(target)!, InstallFiles.TempPrefix + "*"));
    }

    [Fact]
    public void TarGz_SinElFicheroQueSeExige_NoInstalaNada()
    {
        var archive = TarGz("php-8.4.27-nts-linux-x86_64.tar.gz", ("README", "nada", null, UnixFileMode.UserRead));
        var target = Path.Combine(_root, "bin", "php", "8.4.27-nts-x64");

        var ex = Assert.Throws<RoostException>(() =>
            InstallFiles.ExtractAtomic(archive, target, Path.Combine("bin", "php"), stripSingleRoot: false, "php-8.4.27-nts-linux-x86_64.tar.gz"));

        Assert.StartsWith("El tar.gz php-8.4.27-nts-linux-x86_64.tar.gz no trae", ex.Message);
        Assert.False(Directory.Exists(target));
        Assert.Empty(Directory.GetDirectories(Path.GetDirectoryName(target)!));
    }

    [Fact]
    public void TarGz_ConUnaEntradaFueraDeLaCarpeta_SeRechaza()
    {
        var archive = TarGz("malo.tar.gz", ("../fuera.txt", "x", null, UnixFileMode.UserRead), ("redis-server", "x", null, Executable));
        var target = Path.Combine(_root, "bin", "redis", "8.10.2");

        Assert.Throws<RoostException>(() => InstallFiles.ExtractAtomic(archive, target, "redis-server", stripSingleRoot: true, "malo.tar.gz"));

        Assert.False(Directory.Exists(target));
        Assert.False(File.Exists(Path.Combine(_root, "bin", "redis", "fuera.txt")));
    }

    [LinuxFact]
    public void TarGz_EnLinux_ConservaLosPermisosYLosEnlaces()
    {
        var archive = TarGz("mariadb.tar.gz",
            ("mariadb-11.4.13/bin/mariadbd", "elf", null, Executable),
            ("mariadb-11.4.13/lib/libmariadb.so.3", "so", null, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead),
            ("mariadb-11.4.13/lib/libmariadb.so", null, "libmariadb.so.3", Executable));
        var target = Path.Combine(_root, "bin", "mariadb", "11.4.13");

        InstallFiles.ExtractAtomic(archive, target, Path.Combine("bin", "mariadbd"), stripSingleRoot: true, "mariadb.tar.gz");

        Assert.True(File.GetUnixFileMode(Path.Combine(target, "bin", "mariadbd")).HasFlag(UnixFileMode.UserExecute));
        Assert.Equal("libmariadb.so.3", new FileInfo(Path.Combine(target, "lib", "libmariadb.so")).LinkTarget);
    }

    [LinuxFact]
    public void MarkExecutable_EnLinux_DejaElFicheroEjecutable()
    {
        Directory.CreateDirectory(_root);
        var file = Path.Combine(_root, "cloudflared");
        File.WriteAllText(file, "elf");
        File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite);

        InstallFiles.MarkExecutable(file);

        Assert.Equal(Executable, File.GetUnixFileMode(file));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }
}
