using Roost.Core.Git;

namespace Roost.Core.Tests;

/// <summary>
/// El OpenSSH de Windows y los GIT_SSH_COMMAND: el texto exacto, las comillas para el sh de MinGit
/// (probado en real con espacios, ' y $) y las de ssh dentro de -o (con los % doblados).
/// </summary>
public class OpenSshTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "roost-openssh-" + Guid.NewGuid().ToString("N"));

    private static readonly OpenSshTools Tools = new(@"C:\Windows\System32\OpenSSH");

    [WindowsFact("Solo en Windows: el OpenSSH de System32.")]
    public void KeyCommand_SoloLaClaveDeRoost_SinPreguntas_YSoloElKnownHostsDeRoost()
    {
        var command = OpenSsh.KeyCommand(Tools, @"C:\roost\state\git\keys\tienda", @"C:\roost\state\git\known_hosts");

        Assert.Equal(
            "'C:/Windows/System32/OpenSSH/ssh.exe' -F none -o 'IdentityFile=\"C:/roost/state/git/keys/tienda\"' -o IdentitiesOnly=yes -o IdentityAgent=none "
            + "-o BatchMode=yes -o StrictHostKeyChecking=yes -o 'UserKnownHostsFile=\"C:/roost/state/git/known_hosts\"' -o ConnectTimeout=20",
            command);
    }

    [WindowsFact("Solo en Windows: el OpenSSH de System32.")]
    public void OwnCommand_LasDelUsuario_PeroEstrictoYConLasHuellasDeRoostPrimero()
    {
        var command = OpenSsh.OwnCommand(Tools, @"C:\roost\state\git\known_hosts");

        Assert.Equal(
            "'C:/Windows/System32/OpenSSH/ssh.exe' -o BatchMode=yes -o StrictHostKeyChecking=yes "
            + "-o 'UserKnownHostsFile=\"C:/roost/state/git/known_hosts\" ~/.ssh/known_hosts ~/.ssh/known_hosts2' -o ConnectTimeout=20",
            command);
        Assert.DoesNotContain("IdentityFile", command);
        Assert.DoesNotContain("-F none", command);   // su ~/.ssh/config, como en su terminal
    }

    [Theory]
    [InlineData(@"C:\Users\Ana Perez\roost", @"'C:/Users/Ana Perez/roost'")]
    [InlineData(@"C:\Users\O'Brien\r$oost", @"'C:/Users/O'\''Brien/r$oost'")]
    public void ShellQuote_ComillasSimplesPosix(string path, string expected)
    {
        Assert.Equal(expected, OpenSsh.ShellQuote(OpenSsh.ForwardSlashes(path)));
    }

    [Fact]
    public void ConfigPath_ComillasDoblesYPorcentajesDoblados()
    {
        Assert.Equal("\"C:/Users/Ana Perez/100%%/kh\"", OpenSsh.ConfigPath(@"C:\Users\Ana Perez\100%\kh"));
    }

    [Fact]
    public void Locate_SinOpenSsh_DiceQueEsUnaCaracteristicaOpcional()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, OperatingSystem.IsWindows() ? "ssh.exe" : "ssh"), "");

        var ex = Assert.Throws<RoostException>(() => OpenSsh.Locate(_root));

        Assert.Null(OpenSsh.TryLocate(_root));
        Assert.Contains("ssh-keygen", ex.Message);
        Assert.Contains("ssh-keyscan", ex.Message);
        if (OperatingSystem.IsWindows())
        {
            Assert.Contains("caracteristica opcional de Windows", ex.Message);
            Assert.Contains("Add-WindowsCapability -Online -Name OpenSSH.Client~~~~0.0.1.0", ex.Message);
        }
    }

    [Fact]
    public void Locate_ConLosTres_DaSusRutas()
    {
        Directory.CreateDirectory(_root);
        var ext = OperatingSystem.IsWindows() ? ".exe" : "";
        foreach (var tool in new[] { "ssh", "ssh-keygen", "ssh-keyscan" }) File.WriteAllText(Path.Combine(_root, tool + ext), "");

        var tools = OpenSsh.Locate(_root);

        Assert.Equal(Path.Combine(_root, "ssh-keygen" + ext), tools.SshKeygen);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (DirectoryNotFoundException) { }
    }
}
