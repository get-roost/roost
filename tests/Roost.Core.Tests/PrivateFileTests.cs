using Roost.Core.Platform;

namespace Roost.Core.Tests;

/// <summary>Los ficheros que solo puede leer el usuario (claves): en Linux nacen ya 0600 / 0700, sin la ventana del umask.</summary>
public class PrivateFileTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "roost-private-" + Guid.NewGuid().ToString("N"));

    public PrivateFileTests()
    {
        Directory.CreateDirectory(_dir);
    }

    [Fact]
    public void Write_EscribeElContenido_YSobreescribe()
    {
        var file = Path.Combine(_dir, "clave");

        PrivateFile.Write(file, "secreto");
        Assert.Equal("secreto", File.ReadAllText(file));

        PrivateFile.Write(file, "otro");
        Assert.Equal("otro", File.ReadAllText(file));
    }

    [LinuxFact]
    public void Linux_Write_NaceCon0600_YUnoQueExistiaBajaA0600()
    {
        var file = Path.Combine(_dir, "clave");

        PrivateFile.Write(file, "secreto");
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(file));

        var open = Path.Combine(_dir, "abierta");
        File.WriteAllText(open, "x");
        File.SetUnixFileMode(open, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        PrivateFile.Write(open, "secreto");
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(open));
    }

    [LinuxFact]
    public void Linux_EnsureDirectory_NaceCon0700_YUnaQueExistiaBajaA0700()
    {
        var dir = Path.Combine(_dir, "keys");

        PrivateFile.EnsureDirectory(dir);
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(dir));

        var open = Path.Combine(_dir, "run");
        Directory.CreateDirectory(open);
        Assert.NotEqual(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(open));
        PrivateFile.EnsureDirectory(open);
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(open));
    }

    [LinuxFact]
    public void Linux_Posix_UidCanWriteYGlibc()
    {
        Assert.True(Posix.Uid >= 0);
        Assert.True(Posix.CanWrite(_dir));
        Assert.False(Posix.CanWrite("/proc/1/root/no-existe"));
        Assert.True(Posix.IsAlive(Environment.ProcessId));
        Assert.False(Posix.IsAlive(int.MaxValue - 1));
        Assert.NotNull(Posix.GlibcVersion());
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }
}
