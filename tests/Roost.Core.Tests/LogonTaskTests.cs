using System.Text;
using System.Xml.Linq;
using Roost.Core.Platform;

namespace Roost.Core.Tests;

/// <summary>
/// E4: la tarea al iniciar sesión que lanza <c>roost up</c> sin la app. Con un schtasks de pega
/// (<see cref="FakeCommandRunner"/>): nunca se crea ni se borra una tarea de verdad.
/// </summary>
public class LogonTaskTests
{
    private const string Exe = @"C:\Program Files\Roost\roost.exe";
    private static readonly XNamespace Ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";

    private static readonly Func<string, string?> NoHome = _ => null;

    [WindowsFact("Solo en Windows: la tarea programada (schtasks).")]
    public void Xml_EsDelUsuario_SinElevar_SinVentana_YSinLimite()
    {
        var xml = LogonTask.Xml(Exe, @"PORTATIL\ana", @"C:\Windows\System32\conhost.exe");
        var doc = XDocument.Parse(xml);
        var root = doc.Root!;
        Assert.Equal(@"PORTATIL\ana", root.Element(Ns + "Triggers")!.Element(Ns + "LogonTrigger")!.Element(Ns + "UserId")!.Value);
        var principal = root.Element(Ns + "Principals")!.Element(Ns + "Principal")!;
        Assert.Equal(@"PORTATIL\ana", principal.Element(Ns + "UserId")!.Value);
        Assert.Equal("InteractiveToken", principal.Element(Ns + "LogonType")!.Value);
        Assert.Equal("LeastPrivilege", principal.Element(Ns + "RunLevel")!.Value);
        var settings = root.Element(Ns + "Settings")!;
        Assert.Equal("false", settings.Element(Ns + "DisallowStartIfOnBatteries")!.Value);
        Assert.Equal("false", settings.Element(Ns + "StopIfGoingOnBatteries")!.Value);
        Assert.Equal("PT0S", settings.Element(Ns + "ExecutionTimeLimit")!.Value);
        Assert.Equal("IgnoreNew", settings.Element(Ns + "MultipleInstancesPolicy")!.Value);
        var exec = root.Element(Ns + "Actions")!.Element(Ns + "Exec")!;
        Assert.Equal(@"C:\Windows\System32\conhost.exe", exec.Element(Ns + "Command")!.Value);
        Assert.Equal($"--headless \"{Exe}\" up", exec.Element(Ns + "Arguments")!.Value);
        Assert.Equal(@"C:\Program Files\Roost", exec.Element(Ns + "WorkingDirectory")!.Value);
        Assert.StartsWith("<?xml version=\"1.0\" encoding=\"UTF-16\"?>", xml);

        var status = LogonTask.Parse(xml);
        Assert.True(status.Exists);
        Assert.True(status.Enabled);
        Assert.Equal(Exe, LogonTask.RoostExeIn(status.Arguments));
        Assert.False(status.IsStale(_ => true));
        Assert.True(status.IsStale(_ => false));
    }

    [Fact]
    public async Task Status_LeeLaTarea_ConQuery_YSinEllaEsQueNoEsta()
    {
        var disabled = XDocument.Parse(LogonTask.Xml(Exe, @"PORTATIL\ana", @"C:\Windows\System32\conhost.exe"));
        disabled.Root!.Element(Ns + "Settings")!.Element(Ns + "Enabled")!.Value = "false";
        var xml = disabled.ToString();
        var runner = new FakeCommandRunner().On("/query", stdout: xml);
        var status = await new LogonTask(runner, "schtasks.exe").StatusAsync();
        Assert.True(status.Exists);
        Assert.False(status.Enabled);   // desactivada a mano en el Programador de tareas
        Assert.Equal([$"/query /tn {LogonTask.TaskName} /xml ONE"], runner.Commands);

        var missing = await new LogonTask(new FakeCommandRunner().On("/query", exitCode: 1, stderr: "ERROR: El sistema no puede encontrar el archivo especificado.")).StatusAsync();
        Assert.Equal(LogonTaskStatus.Missing, missing);

        var broken = await new LogonTask(new FakeCommandRunner().On("/query", exitCode: 2, stderr: "ERROR: El servicio no responde.")).StatusAsync();
        Assert.False(broken.Exists);
        Assert.Equal("El servicio no responde.", broken.Error);
    }

    [WindowsFact("Solo en Windows: la tarea programada (schtasks).")]
    public async Task Enable_CreaConElXmlEnUtf16_YBorraElTemporal()
    {
        string? file = null;
        byte[]? bytes = null;
        var runner = new FakeCommandRunner().On("/create", onRun: spec =>
        {
            file = spec.Arguments[spec.Arguments.ToList().IndexOf("/xml") + 1];
            bytes = File.ReadAllBytes(file);
        });
        await new LogonTask(runner, "schtasks.exe", NoHome).EnableAsync(Exe);

        Assert.Equal(["/create", "/tn", LogonTask.TaskName, "/xml", file!, "/f"], runner.Calls.Single().Arguments);
        Assert.Equal([0xFF, 0xFE], bytes![..2]);   // UTF-16 LE con BOM, lo que lee schtasks
        var text = Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        Assert.Contains($"<UserId>{LogonTask.CurrentUser}</UserId>", text);
        Assert.Contains($"--headless \"{Exe}\" up", text);
        Assert.False(File.Exists(file));
    }

    [WindowsFact("Solo en Windows: la tarea programada (schtasks).")]
    public async Task Enable_ConRoostHome_NoCreaNada_YSiSchtasksFallaLoDice()
    {
        var runner = new FakeCommandRunner();
        var withHome = new LogonTask(runner, "schtasks.exe", name => name == RoostPaths.HomeVariable ? @"C:\pruebas" : null);
        var ex = await Assert.ThrowsAsync<RoostException>(() => withHome.EnableAsync(Exe));
        Assert.Contains("ROOST_HOME", ex.Message);
        Assert.Empty(runner.Calls);

        var denied = new LogonTask(new FakeCommandRunner().On("/create", exitCode: 1, stderr: "ERROR: Acceso denegado."), "schtasks.exe", NoHome);
        ex = await Assert.ThrowsAsync<RoostException>(() => denied.EnableAsync(Exe));
        Assert.Equal("No se pudo crear la tarea al iniciar sesion: Acceso denegado.", ex.Message);
    }

    [Fact]
    public async Task Disable_PreguntaAntes_YSoloBorraSiEsta()
    {
        var none = new FakeCommandRunner().On("/query", exitCode: 1);
        await new LogonTask(none).DisableAsync();
        Assert.Equal([$"/query /tn {LogonTask.TaskName} /xml ONE"], none.Commands);   // no estaba: no se borra nada

        var xml = LogonTask.Xml(Exe, @"PORTATIL\ana", "conhost.exe");
        var some = new FakeCommandRunner().On("/query", stdout: xml).On("/delete");
        await new LogonTask(some).DisableAsync();
        Assert.Equal($"/delete /tn {LogonTask.TaskName} /f", some.Commands.Last());

        var denied = new FakeCommandRunner().On("/query", stdout: xml).On("/delete", exitCode: 1, stderr: "ERROR: Acceso denegado.");
        var ex = await Assert.ThrowsAsync<RoostException>(() => new LogonTask(denied).DisableAsync());
        Assert.Contains("Acceso denegado", ex.Message);
    }

    [Fact]
    public void WhyNot_SoloEnWindowsYSinRoostHome()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Null(LogonTask.WhyNot(NoHome));
            Assert.NotNull(LogonTask.WhyNot(_ => @"C:\pruebas"));
        }
        else
        {
            Assert.NotNull(LogonTask.WhyNot(NoHome));
        }
        Assert.Null(LogonTask.RoostExeIn("up"));
        Assert.Null(LogonTask.RoostExeIn(null));
    }
}
