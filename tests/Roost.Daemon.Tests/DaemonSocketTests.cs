using System.Net.Sockets;
using Roost.Core;
using Roost.Core.Platform;

namespace Roost.Daemon.Tests;

/// <summary>
/// El unix socket de roostd: dónde va (XDG_RUNTIME_DIR, run/ de la raíz, o una ruta corta si no cabe en
/// sun_path), la misma función para el host y el cliente, y que un socket viejo solo se quita si nadie
/// contesta en él.
/// </summary>
public class DaemonSocketTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "roost-sock-" + Guid.NewGuid().ToString("N"));

    private RoostPaths Paths(string? root = null) => new(root ?? Path.Combine(_dir, "raiz"), Path.Combine(_dir, "cache"));

    [Fact]
    public void ChooseSocket_ConXdgRuntimeDir_VaAhi_ConLaEtiquetaDeLaRaiz()
    {
        var runtime = Path.Combine(_dir, "run-user");
        Directory.CreateDirectory(runtime);
        var paths = Paths();

        var (path, own) = DaemonHost.ChooseSocket(paths, v => v == "XDG_RUNTIME_DIR" ? runtime : null, 1001);

        Assert.Equal(Path.Combine(runtime, $"roost{RoostPaths.InstanceTag(paths)}.sock"), path);
        Assert.Matches(@"roost-[0-9a-f]{8}\.sock$", path);   // una raíz que no es la de siempre lleva su etiqueta
        Assert.False(own);                                   // la carpeta es del sistema: no se le cambia el modo
        Assert.Equal(path, DaemonHost.ChooseSocket(paths, v => v == "XDG_RUNTIME_DIR" ? runtime : null, 1001).Path);   // el cliente usa la misma
    }

    [Fact]
    public void ChooseSocket_SinXdgRuntimeDir_OSiNoExiste_VaARunDeLaRaiz()
    {
        var paths = Paths();

        var (path, own) = DaemonHost.ChooseSocket(paths, _ => null, 1001);
        Assert.Equal(Path.Combine(paths.Run, "roost.sock"), path);
        Assert.True(own);

        var (missing, _) = DaemonHost.ChooseSocket(paths, v => v == "XDG_RUNTIME_DIR" ? Path.Combine(_dir, "no-existe") : null, 1001);
        Assert.Equal(Path.Combine(paths.Run, "roost.sock"), missing);
    }

    [Fact]
    public void ChooseSocket_SiLaRutaNoCabeEnSunPath_UnaCortaEnTmp()
    {
        var longRoot = Path.Combine(_dir, new string('a', 120));
        var paths = Paths(longRoot);

        var (path, own) = DaemonHost.ChooseSocket(paths, _ => null, 1001);

        Assert.True(System.Text.Encoding.UTF8.GetByteCount(path) <= DaemonHost.MaxSocketPath, path);
        Assert.StartsWith(Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar), path);
        Assert.Contains($"roost-1001{RoostPaths.InstanceTag(paths)}", path);
        Assert.EndsWith("roost.sock", path);
        Assert.True(own);
    }

    [LinuxFact]
    public void Linux_IsSocketAlive_YPrepareSocket_SoloQuitaUnSocketSinNadieDetras()
    {
        Directory.CreateDirectory(_dir);
        var socket = Path.Combine(_dir, "roost.sock");
        Assert.False(DaemonHost.IsSocketAlive(socket));   // no existe

        using (var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified))
        {
            listener.Bind(new UnixDomainSocketEndPoint(socket));
            listener.Listen(1);
            Assert.True(DaemonHost.IsSocketAlive(socket));
            Assert.Throws<RoostException>(() => DaemonHost.PrepareSocket(socket, ownDirectory: false));   // hay un roostd vivo
            Assert.True(File.Exists(socket));
        }

        // Cerrado el que escuchaba, el fichero sigue ahí huérfano: ahora sí se quita.
        Assert.True(File.Exists(socket));
        Assert.False(DaemonHost.IsSocketAlive(socket));
        DaemonHost.PrepareSocket(socket, ownDirectory: false);
        Assert.False(File.Exists(socket));
    }

    [LinuxFact]
    public void Linux_PrepareSocket_ConCarpetaPropia_LaDejaA0700()
    {
        var run = Path.Combine(_dir, "raiz", "run");
        DaemonHost.PrepareSocket(Path.Combine(run, "roost.sock"), ownDirectory: true);
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(run));
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }
}
