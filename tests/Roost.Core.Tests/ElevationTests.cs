using System.ComponentModel;
using System.Diagnostics;
using Roost.Core.Platform;

namespace Roost.Core.Tests;

/// <summary>
/// Testea cómo se lanza roost-helper con privilegios, con un runner falso en lugar de pkexec, sudo y
/// el helper: el camino de Linux (pkexec, cancelado, sin agente, sudo con y sin terminal, «ejecuta
/// esto») y el de Windows (el UAC y su «No»). Nada eleva nada de verdad.
/// </summary>
public class ElevationTests
{
    private const string Helper = "/usr/libexec/roost/roost-helper";
    private const string Request = "/home/ana/.local/share/roost/run/hosts-request.txt";

    private static Elevation Linux(FakeCommandRunner runner, bool root = false, bool terminal = false, bool pkexec = true, bool sudo = true) =>
        new(runner, linux: true, isRoot: () => root, hasTerminal: () => terminal,
            exists: path => path == Helper || (pkexec && path == Elevation.Pkexec) || (sudo && path == Elevation.Sudo));

    [Fact]
    public void Linux_ComoRoot_ElHelperVaDirecto_ConSuStdin()
    {
        var runner = new FakeCommandRunner().On("hosts-sync");
        var run = Linux(runner, root: true).Run(Helper, ["hosts-sync"], stdin: "a.test\n", stdinFile: Request);

        Assert.True(run.Ok);
        var call = Assert.Single(runner.Calls);
        Assert.Equal(Helper, call.Exe);
        Assert.Equal(["hosts-sync"], call.Arguments);
        Assert.Equal("a.test\n", call.Stdin);
    }

    [Fact]
    public void Linux_PkexecPrimero_YSuCodigoEsElDelHelper()
    {
        var runner = new FakeCommandRunner().On($"{Helper} dns-enable", exitCode: 69, stderr: "");
        var run = Linux(runner).Run(Helper, ["dns-enable"]);

        Assert.Equal(ElevationStatus.Done, run.Status);
        Assert.Equal(69, run.ExitCode);
        var call = Assert.Single(runner.Calls);
        Assert.Equal(Elevation.Pkexec, call.Exe);
        Assert.Equal([Helper, "dns-enable"], call.Arguments);
    }

    [Fact]
    public void Linux_CancelarEnPolkit_Es126_YNoSePruebaSudo()
    {
        var runner = new FakeCommandRunner().On($"{Helper} hosts-sync", exitCode: 126, stderr: "Error executing command as another user: Request dismissed");
        var run = Linux(runner, terminal: true).Run(Helper, ["hosts-sync"], stdin: "a.test\n");

        Assert.Equal(ElevationStatus.Cancelled, run.Status);
        Assert.Single(runner.Calls);
    }

    [Fact]
    public void Linux_SinAgenteDePolkit_ConTerminal_SePasaASudo_QuePreguntaEnLaTerminal()
    {
        var runner = new FakeCommandRunner()
            .On($"{Helper} hosts-sync", exitCode: 127, stderr: "Error getting authority: polkit agent not available")
            .On($"-- {Helper} hosts-sync", exitCode: 0);
        var run = Linux(runner, terminal: true).Run(Helper, ["hosts-sync"], stdin: "a.test\n", stdinFile: Request);

        Assert.True(run.Ok);
        Assert.Equal(2, runner.Calls.Count);
        Assert.Equal(Elevation.Sudo, runner.Calls[1].Exe);
        Assert.Equal(["--", Helper, "hosts-sync"], runner.Calls[1].Arguments);
        Assert.Equal("a.test\n", runner.Calls[1].Stdin);   // los nombres llegan igual por sudo
    }

    [Fact]
    public void Linux_SudoQueNoAutoriza_EsCancelado_YElHelperNuncaSaleCon1()
    {
        var runner = new FakeCommandRunner()
            .On($"{Helper} www-init", exitCode: 127)
            .On($"-- {Helper} www-init", exitCode: 1, stderr: "sudo: 3 incorrect password attempts\n");
        var run = Linux(runner, terminal: true).Run(Helper, ["www-init"]);

        Assert.Equal(ElevationStatus.Cancelled, run.Status);
        Assert.Contains("incorrect password", run.Output);
    }

    [Fact]
    public void Linux_SinTerminal_SudoSoloSinContrasena_YSiLaPide_EjecutaEsto()
    {
        var runner = new FakeCommandRunner()
            .On($"{Helper} hosts-sync", exitCode: 127, stderr: "polkit-agent-helper-1: pam_authenticate failed: Authentication failure")
            .On($"-n -- {Helper} hosts-sync", exitCode: 1, stderr: "sudo: a password is required\n");

        var ex = Assert.Throws<ElevationNeededException>(() => Linux(runner).Run(Helper, ["hosts-sync"], stdin: "a.test\n", stdinFile: Request));

        Assert.Equal($"sudo {Helper} hosts-sync < {Request}", ex.Command);
        Assert.Contains("Ejecuta esto en una terminal", ex.Message);
        Assert.Contains(ex.Command, ex.Message);
        Assert.Contains("pkexec no ha podido autorizar", ex.Message);
        Assert.Contains("sudo pide la contrasena y no hay terminal", ex.Message);
        Assert.Equal(["-n", "--", Helper, "hosts-sync"], runner.Calls[1].Arguments);
    }

    [Fact]
    public void Linux_SinTerminal_SudoSinContrasena_Vale()
    {
        var runner = new FakeCommandRunner()
            .On($"{Helper} setcap-nginx /home/ana/nginx", exitCode: 127)
            .On($"-n -- {Helper} setcap-nginx /home/ana/nginx", exitCode: 0);
        var run = Linux(runner).Run(Helper, ["setcap-nginx", "/home/ana/nginx"]);

        Assert.True(run.Ok);
    }

    [Fact]
    public void Linux_SinPkexecNiSudo_EjecutaEsto_SinLanzarNada()
    {
        var runner = new FakeCommandRunner();
        var ex = Assert.Throws<ElevationNeededException>(() => Linux(runner, pkexec: false, sudo: false).Run(Helper, ["dns-enable"]));

        Assert.Empty(runner.Calls);
        Assert.Contains("no hay pkexec", ex.Message);
        Assert.Contains("no hay sudo", ex.Message);
        Assert.Equal($"sudo {Helper} dns-enable", ex.Command);
    }

    [Fact]
    public void SinHelper_SeDiceDondeSeEsperaba()
    {
        var ex = Assert.Throws<RoostException>(() => Linux(new FakeCommandRunner()).Run("/otro/sitio/roost-helper", ["dns-enable"]));
        Assert.Contains("/otro/sitio/roost-helper", ex.Message);
        Assert.IsNotType<ElevationNeededException>(ex);
    }

    [Fact]
    public void LaOrdenManual_EntrecomillaLoQueHaceFalta()
    {
        var elevation = new Elevation(new FakeCommandRunner(), linux: true);
        Assert.Equal("sudo /usr/libexec/roost/roost-helper setcap-nginx '/home/ana maria/nginx'", elevation.ManualCommand(Helper, ["setcap-nginx", "/home/ana maria/nginx"]));
        Assert.Equal("'it'\\''s'", Elevation.ShellQuote("it's"));
        Assert.Equal("a.test", Elevation.ShellQuote("a.test"));
    }

    [Fact]
    public void Windows_ElUac_ConVerbRunas_YSuNoEsCancelado()
    {
        ProcessStartInfo? seen = null;
        var ok = new Elevation(linux: false, exists: _ => true, launchWindows: start => { seen = start; return 69; });
        var run = ok.Run(@"C:\Program Files\Roost\roost-helper.exe", ["hosts-sync", @"C:\roost\run\hosts-request.txt"]);

        Assert.Equal(ElevationStatus.Done, run.Status);
        Assert.Equal(69, run.ExitCode);
        Assert.NotNull(seen);
        Assert.Equal("runas", seen.Verb);
        Assert.True(seen.UseShellExecute);
        Assert.Equal(["hosts-sync", @"C:\roost\run\hosts-request.txt"], seen.ArgumentList);
        Assert.Equal(@"""C:\Program Files\Roost\roost-helper.exe"" hosts-sync C:\roost\run\hosts-request.txt", run.ManualCommand);

        var cancelled = new Elevation(linux: false, exists: _ => true, launchWindows: _ => throw new Win32Exception(1223));
        Assert.Equal(ElevationStatus.Cancelled, cancelled.Run(@"C:\roost\roost-helper.exe", ["dns-enable"]).Status);
    }

    [Fact]
    public void HelperLocator_PrimeroElDeAlLado_YSiNoElDelPaquete()
    {
        var beside = Path.Combine("/opt/roost", HelperLocator.FileName);
        Assert.Equal(beside, HelperLocator.Find("/opt/roost", path => path == beside));
        var fallback = HelperLocator.Find("/opt/roost", path => path == HelperLocator.LinuxSystemPath);
        Assert.Equal(OperatingSystem.IsLinux() ? HelperLocator.LinuxSystemPath : beside, fallback);
        Assert.Equal(beside, HelperLocator.Find("/opt/roost", _ => false));   // para el mensaje de «no se encuentra»
    }
}
