using Roost.Core.Platform;

namespace Roost.Core.Tests;

/// <summary>
/// E4 en Linux: la unidad de <c>systemd --user</c> (o el .desktop sin systemd) que lanza <c>roost up</c>
/// al iniciar sesión. Con un systemctl de pega (<see cref="FakeCommandRunner"/>) y una HOME temporal:
/// nunca se toca systemd ni la sesión de verdad.
/// </summary>
public class UserUnitTests : IDisposable
{
    private const string Exe = "/opt/roost/roost";
    private readonly string _home = Path.Combine(Path.GetTempPath(), "roost-userunit-" + Guid.NewGuid().ToString("N"));
    private readonly FakeCommandRunner _runner = new();

    private UserUnit Unit(string? roostHome = null) => new(_runner, _home, name => name == RoostPaths.HomeVariable ? roostHome : null);

    [Fact]
    public void Unidad_OneshotQueSeQuedaActiva_ConUpYDown_YElRoostHomeSiLoHay()
    {
        var text = UserUnit.Unit(Exe, null);

        Assert.Contains("Type=oneshot", text);
        Assert.Contains("RemainAfterExit=yes", text);
        Assert.Contains($"ExecStart=\"{Exe}\" up", text);
        Assert.Contains($"ExecStop=\"{Exe}\" down", text);
        Assert.Contains("WantedBy=default.target", text);
        Assert.DoesNotContain("Environment=", text);
        Assert.DoesNotContain("\r", text);

        var withHome = UserUnit.Unit("/ruta con espacios/roost", "/home/ana/casa");
        Assert.Contains("Environment=\"ROOST_HOME=/home/ana/casa\"", withHome);
        Assert.Contains("ExecStart=\"/ruta con espacios/roost\" up", withHome);

        var parsed = UserUnit.ParseUnit(withHome, "/x/roost.service", enabled: true);
        Assert.True(parsed.Exists);
        Assert.True(parsed.Enabled);
        Assert.Equal(AutostartKind.SystemdUser, parsed.Kind);
        Assert.Equal("/ruta con espacios/roost", parsed.Command);
        Assert.Equal("/home/ana/casa", parsed.RoostHome);
        Assert.False(parsed.IsStale(_ => true));
        Assert.True(parsed.IsStale(_ => false));
    }

    [Fact]
    public void Desktop_EsUnaEntradaDeAutostartOculta_ConEnvParaElRoostHome()
    {
        var text = UserUnit.Desktop(Exe, null);

        Assert.StartsWith("[Desktop Entry]\n", text);
        Assert.Contains($"Exec={Exe} up", text);
        Assert.Contains("NoDisplay=true", text);
        Assert.Contains("X-GNOME-Autostart-enabled=true", text);
        Assert.DoesNotContain("env ", text);

        var withHome = UserUnit.Desktop("/ruta con espacios/roost", "/home/ana/casa");
        Assert.Contains("Exec=env ROOST_HOME=/home/ana/casa \"/ruta con espacios/roost\" up", withHome);

        var parsed = UserUnit.ParseDesktop(withHome, "/x/roost-stack.desktop");
        Assert.Equal(AutostartKind.Desktop, parsed.Kind);
        Assert.True(parsed.Enabled);
        Assert.Equal("/ruta con espacios/roost", parsed.Command);
        Assert.Equal("/home/ana/casa", parsed.RoostHome);

        Assert.False(UserUnit.ParseDesktop(text + "Hidden=true\n", "/x").Enabled);
    }

    [Fact]
    public async Task SystemdDisponible_SegunIsSystemRunning()
    {
        _runner.On("--user is-system-running", stdout: "running\n");
        Assert.Null(await Unit().WhySystemdUnavailableAsync());

        var degraded = new FakeCommandRunner().On("--user is-system-running", exitCode: 1, stdout: "degraded\n");
        Assert.Null(await new UserUnit(degraded, _home).WhySystemdUnavailableAsync());

        var wsl = new FakeCommandRunner().On("--user is-system-running", exitCode: 1, stderr: "Failed to connect to bus: No medium found\n");
        var why = await new UserUnit(wsl, _home).WhySystemdUnavailableAsync();
        Assert.Contains("no contesta", why);
        Assert.Contains("Failed to connect to bus", why);
    }

    [LinuxFact]
    public async Task Enable_ConSystemd_EscribeLaUnidad_RecargaYActiva_YDisableLaQuita()
    {
        _runner.On("--user is-system-running", stdout: "running\n")
            .On("--user daemon-reload")
            .On("--user enable roost.service", onRun: _ =>
            {
                // Lo que hace systemctl: el enlace en default.target.wants.
                var unit = Unit();
                Directory.CreateDirectory(Path.GetDirectoryName(unit.WantsLink)!);
                File.CreateSymbolicLink(unit.WantsLink, unit.UnitFile);
            })
            .On("--user disable roost.service", onRun: _ => File.Delete(Unit().WantsLink));
        var unit = Unit("/home/ana/casa");

        var put = await unit.EnableAsync(Exe);

        Assert.Equal(AutostartKind.SystemdUser, put.Kind);
        Assert.Null(put.Error);
        Assert.True(File.Exists(unit.UnitFile));
        Assert.Contains("Environment=\"ROOST_HOME=/home/ana/casa\"", File.ReadAllText(unit.UnitFile));
        Assert.Equal(["--user is-system-running", "--user daemon-reload", "--user enable roost.service"], _runner.Commands);
        var status = unit.Status();
        Assert.True(status.Exists);
        Assert.True(status.Enabled);
        Assert.Equal(Exe, status.Command);
        Assert.Equal("/home/ana/casa", status.RoostHome);

        await unit.DisableAsync();

        Assert.False(File.Exists(unit.UnitFile));
        Assert.False(unit.Status().Exists);
        Assert.Contains("--user disable roost.service", _runner.Commands);
        Assert.Equal("--user daemon-reload", _runner.Commands.Last());
    }

    [Fact]
    public async Task Enable_SinSystemd_EscribeElDesktop_YLoDice()
    {
        _runner.On("--user is-system-running", exitCode: 1, stderr: "Failed to connect to bus: No medium found\n");
        var unit = Unit();

        var put = await unit.EnableAsync(Exe);

        Assert.Equal(AutostartKind.Desktop, put.Kind);
        Assert.Contains("Failed to connect to bus", put.Error);
        Assert.True(File.Exists(unit.DesktopFile));
        Assert.False(File.Exists(unit.UnitFile));
        Assert.Equal(AutostartKind.Desktop, unit.Status().Kind);
        Assert.DoesNotContain(_runner.Commands, c => c.Contains("enable"));

        await unit.DisableAsync();

        Assert.False(File.Exists(unit.DesktopFile));
        Assert.False(unit.Status().Exists);
    }

    [Fact]
    public void SinNada_NoEsta()
    {
        var status = Unit().Status();
        Assert.False(status.Exists);
        Assert.Equal(AutostartKind.None, status.Kind);
        Assert.Null(status.Error);
    }

    public void Dispose()
    {
        try { Directory.Delete(_home, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
