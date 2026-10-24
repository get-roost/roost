using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Roost.Core.Platform;
using Roost.Core.Sites;

namespace Roost.Core.Tests;

/// <summary>
/// WSL, SOLO con dobles: el hosts de Windows que se escribe con el roost-helper.exe de Windows (un
/// PowerShell de pega que recibe la orden), las líneas para copiar cuando no hay Roost de Windows,
/// y la CA en el almacén de Windows con un certutil.exe de pega. Ningún test ejecuta un .exe de
/// Windows ni toca el hosts ni el almacén de nadie.
/// </summary>
public class WslInteropTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "roost-wsl-" + Guid.NewGuid().ToString("N"));

    private const string WindowsHelper = WslInterop.WindowsRoostDir + "/roost-helper.exe";
    private const string RequestForWindows = @"\\wsl.localhost\Ubuntu-24.04\home\ana\.local\share\roost\run\hosts-request.txt";

    public WslInteropTests() => Directory.CreateDirectory(_root);

    [Theory]
    [InlineData("/mnt/c/Users/ana/x.txt", "Ubuntu", @"C:\Users\ana\x.txt")]
    [InlineData("/mnt/d/web", null, @"D:\web")]
    [InlineData("/home/ana/x.txt", "Ubuntu-24.04", @"\\wsl.localhost\Ubuntu-24.04\home\ana\x.txt")]
    [InlineData("/home/ana/x.txt", null, null)]
    [InlineData("relativa", "Ubuntu", null)]
    public void LasRutasDeLinux_SeVenDesdeWindows(string linux, string? distro, string? expected)
    {
        Assert.Equal(expected, WslInterop.ToWindowsPath(linux, distro));
    }

    [Theory]
    [InlineData(@"C:\Users\ana", "/mnt/c/Users/ana")]
    [InlineData("D:/web/x", "/mnt/d/web/x")]
    [InlineData(@"\\servidor\share", null)]
    [InlineData("relativa", null)]
    public void LasRutasDeWindows_SeVenDesdeLinux(string windows, string? expected)
    {
        Assert.Equal(expected, WslInterop.ToLinuxPath(windows));
    }

    [Fact]
    public void ElScriptDelUac_LanzaElExe_YTraduceElNoDelUsuario()
    {
        var script = WslInterop.RunAsScript(WindowsHelper, ["hosts-sync", @"C:\con espacios\hosts-request.txt", "it's"]);

        Assert.Contains($"Start-Process -FilePath '{WindowsHelper}' -ArgumentList @('hosts-sync', '\"C:\\con espacios\\hosts-request.txt\"', 'it''s') -Verb RunAs -Wait -PassThru", script);
        Assert.Contains("NativeErrorCode -eq 1223) { exit 126 }", script);
        Assert.Contains("exit 127", script);

        var spec = WslInterop.RunAs(WindowsHelper, ["dns-enable"]);
        Assert.Equal(WslInterop.PowerShell, spec.Exe);
        Assert.Equal("-EncodedCommand", spec.Arguments[^2]);
        Assert.Equal(WslInterop.RunAsScript(WindowsHelper, ["dns-enable"]), Encoding.Unicode.GetString(Convert.FromBase64String(spec.Arguments[^1])));
    }

    private HostsUpdater WslHosts(string hostsFile, FakeCommandRunner runner, bool windowsRoost) =>
        new(hostsFile, isSystemFile: true, windowsFromWsl: true)
        {
            Runner = runner,
            FileExists = path => windowsRoost && path == WindowsHelper,
            WindowsPathOf = _ => RequestForWindows,
        };

    [Fact]
    public void ConRoostDeWindows_ElHostsLoEscribeSuHelper_ConElUac_DesdePowerShell()
    {
        var hosts = Path.Combine(_root, "hosts");
        var request = Path.Combine(_root, "run", "hosts-request.txt");
        File.WriteAllText(hosts, "# Copyright (c) 1993-2009 Microsoft Corp.\r\n127.0.0.1 localhost\r\n");
        var runner = new FakeCommandRunner().On("-NoProfile", onRun: _ =>
        {
            // Lo que haría el roost-helper.exe de Windows con la petición.
            File.WriteAllText(hosts, HostsBlock.Apply(File.ReadAllText(hosts), File.ReadAllLines(request)));
        });

        var changed = WslHosts(hosts, runner, windowsRoost: true).Sync(["tienda.test", "api.tienda.test"], "/usr/libexec/roost/roost-helper", request);

        Assert.True(changed);
        var call = Assert.Single(runner.Calls);
        Assert.Equal(WslInterop.PowerShell, call.Exe);
        var script = Encoding.Unicode.GetString(Convert.FromBase64String(call.Arguments[^1]));
        // El exe va con su ruta de WINDOWS: PowerShell no sabe que es /mnt/c (probado en la 24.04: con la de Linux, «no se encuentra el archivo»).
        Assert.Contains(@"-FilePath 'C:\Program Files\Roost\roost-helper.exe' -ArgumentList @('hosts-sync', '" + RequestForWindows + "')", script);
        Assert.Equal(["api.tienda.test", "tienda.test"], File.ReadAllLines(request));
        Assert.Equal(["api.tienda.test", "tienda.test"], HostsBlock.ReadHosts(File.ReadAllText(hosts)));
        Assert.StartsWith("# Copyright (c) 1993-2009 Microsoft Corp.\r\n127.0.0.1 localhost\r\n", File.ReadAllText(hosts));
    }

    [Fact]
    public void ElNoDelUac_EnWindows_EsCancelado()
    {
        var hosts = Path.Combine(_root, "hosts");
        File.WriteAllText(hosts, "127.0.0.1 localhost\r\n");
        var runner = new FakeCommandRunner().On("-NoProfile", exitCode: 126);

        var ex = Assert.Throws<RoostException>(() => WslHosts(hosts, runner, windowsRoost: true).Sync(["tienda.test"], "/x/roost-helper", Path.Combine(_root, "run", "req.txt")));

        Assert.Contains("cancelado el permiso de administrador de Windows", ex.Message);
        Assert.IsNotType<ElevationNeededException>(ex);
    }

    [Fact]
    public void SinRoostDeWindows_LasLineasParaCopiar_SinLanzarNada()
    {
        var hosts = Path.Combine(_root, "hosts");
        File.WriteAllText(hosts, "127.0.0.1 localhost\r\n");
        var runner = new FakeCommandRunner();

        var ex = Assert.Throws<ElevationNeededException>(() => WslHosts(hosts, runner, windowsRoost: false).Sync(["tienda.test", "blog.test"], "/x/roost-helper", Path.Combine(_root, "run", "req.txt")));

        Assert.Empty(runner.Calls);
        Assert.Equal("# roost:begin  (lo gestiona Roost: no edites estas lineas)\r\n127.0.0.1 blog.test\r\n127.0.0.1 tienda.test\r\n# roost:end\r\n", ex.Command);
        Assert.Contains(hosts, ex.Message);
        Assert.Contains(ex.Command, ex.Message);
        Assert.Equal("127.0.0.1 localhost\r\n", File.ReadAllText(hosts));   // intacto
    }

    [Fact]
    public void LaCaEnWindows_ConCertutilExe_PorUnaRutaQueWindowsVea()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var ca = new CertificateRequest("CN=Roost Local CA (ana@pc)", key, HashAlgorithmName.SHA256)
            .CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(10));
        var runner = new FakeCommandRunner()
            .On("-user -store Root", exitCode: 1, stdout: "CertUtil: -store command FAILED")
            .On("-user -addstore Root")
            .On("-user -delstore Root");
        var store = new WslTrustStore(runner, distro: "Ubuntu-24.04", exists: path => path == WslInterop.Certutil)
        {
            // En Linux el temporal es /tmp/roost-ca-….crt; aquí (Windows) se simula lo que Windows vería de él.
            WindowsPathOf = temp => @"\\wsl.localhost\Ubuntu-24.04\tmp\" + Path.GetFileName(temp),
        };

        Assert.False(store.IsTrusted(ca));
        Assert.Equal([WslTrustStore.Label], store.Trust(ca).Stores);
        Assert.Equal([WslTrustStore.Label], store.Untrust(ca).Stores);

        Assert.All(runner.Calls, c => Assert.Equal(WslInterop.Certutil, c.Exe));
        var add = runner.Calls[1];
        Assert.Equal(["-user", "-addstore", "Root"], add.Arguments.Take(3));
        Assert.StartsWith(@"\\wsl.localhost\Ubuntu-24.04\", add.Arguments[3]);
        Assert.EndsWith(".crt", add.Arguments[3]);
        Assert.Equal(["-user", "-delstore", "Root", ca.Thumbprint], runner.Calls[2].Arguments);
        Assert.Equal(["-user", "-store", "Root", ca.Thumbprint], runner.Calls[0].Arguments);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }
}
