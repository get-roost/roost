using System.Net;
using System.Net.Sockets;
using Roost.Core.Platform;

namespace Roost.Core.Tests;

/// <summary>
/// Lo que Roost lee de /proc en Linux: los parseos, con ficheros grabados de una Ubuntu 24.04 (valen en
/// cualquier sistema), y los lectores de verdad, solo en Linux, sobre este mismo proceso.
/// </summary>
public class ProcFsTests
{
    // /proc/net/tcp: nginx escuchando en 127.0.0.1:8765 (0x223D, inodo 123456) y en 9840 (0x2670), una conexión
    // establecida a 9840 desde el 51234 (0xC822), y algo de root en 0.0.0.0:80.
    private const string Tcp = """
          sl  local_address rem_address   st tx_queue rx_queue tr tm->when retrnsmt   uid  timeout inode
           0: 0100007F:223D 00000000:0000 0A 00000000:00000000 00:00000000 00000000  1001        0 123456 1 0000000000000000 100 0 0 10 0
           1: 0100007F:2670 00000000:0000 0A 00000000:00000000 00:00000000 00000000  1001        0 123457 1 0000000000000000 100 0 0 10 0
           2: 0100007F:2670 0100007F:C822 01 00000000:00000000 00:00000000 00000000  1001        0 123999 1 0000000000000000 20 4 30 10 -1
           3: 0100007F:C822 0100007F:2670 01 00000000:00000000 00:00000000 00000000  1001        0 124000 1 0000000000000000 20 4 30 10 -1
           4: 00000000:0050 00000000:0000 0A 00000000:00000000 00:00000000 00000000     0        0 20000 1 0000000000000000 100 0 0 10 0

        """;

    // /proc/net/tcp6: lo mismo en [::1]:8765.
    private const string Tcp6 = """
          sl  local_address                         remote_address                        st tx_queue rx_queue tr tm->when retrnsmt   uid  timeout inode
           0: 00000000000000000000000001000000:223D 00000000000000000000000000000000:0000 0A 00000000:00000000 00:00000000 00000000  1001        0 123458 1 0000000000000000 100 0 0 10 0

        """;

    [Fact]
    public void ParseTcp_LeeLasFilas_ConLosPuertosEnDecimal()
    {
        var rows = ProcFs.ParseTcp(Tcp);

        Assert.Equal(5, rows.Count);
        Assert.Equal(8765, rows[0].LocalPort);
        Assert.Equal(ProcFs.Listen, rows[0].State);
        Assert.Equal(123456, rows[0].Inode);
        Assert.Equal(51234, rows[2].RemotePort);
        Assert.Equal(ProcFs.Established, rows[2].State);
    }

    [Fact]
    public void ListeningInodes_PorPuerto_TambienEnTcp6_YEnCualquierDireccion()
    {
        Assert.Equal([123456], ProcFs.ListeningInodes(Tcp, 8765));
        Assert.Equal([123458], ProcFs.ListeningInodes(Tcp6, 8765));
        Assert.Equal([123456, 123458], ProcFs.ListeningInodes(Tcp + Tcp6, 8765));
        Assert.Equal([20000], ProcFs.ListeningInodes(Tcp, 80));   // 0.0.0.0:80 también coge el 127.0.0.1:80
        Assert.Empty(ProcFs.ListeningInodes(Tcp, 9999));
        Assert.Empty(ProcFs.ListeningInodes(Tcp, 51234));   // establecida, no escuchando
    }

    [Fact]
    public void EstablishedTo_LosPuertosDeOrigen_DeLasConexionesAUnPuerto()
    {
        Assert.Equal([51234], ProcFs.EstablishedTo(Tcp, 9840));
        Assert.Empty(ProcFs.EstablishedTo(Tcp, 8765));
        Assert.Empty(ProcFs.EstablishedTo("", 8765));
    }

    [Fact]
    public void ParseStat_ElNombreVaEntreParentesis_YElCampo22EsLaHoraDeArranque()
    {
        var stat = ProcFs.ParseStat("1234 (nginx) S 1 1234 1234 0 -1 4194560 100 0 0 0 1 2 0 0 20 0 1 0 987654 12345678 300 18446744073709551615 1 1 0 0 0 0 0 0 0 0 0 0 17 0 0 0 0 0 0");

        Assert.NotNull(stat);
        Assert.Equal(1234, stat.Pid);
        Assert.Equal("nginx", stat.Comm);
        Assert.Equal('S', stat.State);
        Assert.Equal(1, stat.ParentPid);
        Assert.Equal(1234, stat.ProcessGroup);
        Assert.Equal(1234, stat.Session);
        Assert.Equal(987654, stat.StartTime);
    }

    [Fact]
    public void ParseStat_UnNombreConEspaciosYParentesis_SeCortaPorElUltimoCierre()
    {
        var firefox = ProcFs.ParseStat("4321 (Web Content) S 4000 4000 4000 0 -1 4194304 0 0 0 0 0 0 0 0 20 0 30 0 555 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0");
        Assert.Equal("Web Content", firefox!.Comm);
        Assert.Equal(4000, firefox.ParentPid);
        Assert.Equal(555, firefox.StartTime);

        var tricky = ProcFs.ParseStat("77 (a) (b) R 1 77 77 0 -1 0 0 0 0 0 0 0 0 0 20 0 1 0 42 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0");
        Assert.Equal("a) (b", tricky!.Comm);
        Assert.Equal(42, tricky.StartTime);
    }

    [Theory]
    [InlineData("")]
    [InlineData("basura")]
    [InlineData("12 (corto) S 1 2")]
    public void ParseStat_LoQueNoSeEntiende_EsNull(string line) => Assert.Null(ProcFs.ParseStat(line));

    [Fact]
    public void FueraDeLinux_LosLectoresDevuelvenVacio()
    {
        if (OperatingSystem.IsLinux()) return;
        Assert.Null(ProcFs.Stat(Environment.ProcessId));
        Assert.Empty(ProcFs.Pids());
        Assert.Empty(ProcFs.ChildrenOf(1));
        Assert.Null(ProcFs.ListeningInode(80));
        Assert.Empty(ProcFs.EstablishedTo(80));
    }

    [LinuxFact]
    public void Linux_ElStatDeEsteProceso_YSusPadres()
    {
        var me = ProcFs.Stat(Environment.ProcessId);

        Assert.NotNull(me);
        Assert.Equal(Environment.ProcessId, me.Pid);
        Assert.True(me.StartTime > 0);
        Assert.Equal(me.StartTime, ProcFs.StartTimeOf(Environment.ProcessId));
        Assert.Contains(Environment.ProcessId, ProcFs.ChildrenOf(me.ParentPid));
        Assert.Contains(Environment.ProcessId, ProcFs.Pids());
        Assert.NotNull(ProcFs.CommOf(Environment.ProcessId));
        Assert.Null(ProcFs.StartTimeOf(int.MaxValue - 1));
    }

    [LinuxFact]
    public void Linux_PortOwners_EncuentraElPuertoQueEscuchaEsteProceso()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;

            var owner = PortOwners.Find(port);

            Assert.NotNull(owner);
            Assert.Equal(Environment.ProcessId, owner.Pid);
            Assert.False(owner.IsForeign);
            Assert.Contains($"(PID {Environment.ProcessId})", PortOwners.Describe(owner));
        }
        finally
        {
            listener.Stop();
        }
    }

    [LinuxFact]
    public async Task Linux_EstablishedTo_VeLaConexionDeUnCliente()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, port);
            using var accepted = await listener.AcceptTcpClientAsync();
            var origin = ((IPEndPoint)client.Client.LocalEndPoint!).Port;

            Assert.Contains(origin, ProcFs.EstablishedTo(port));
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public void PortOwner_Describe_UnDuenoQueNoSeVe()
    {
        var foreign = new PortOwner(80, 0, null);
        Assert.True(foreign.IsForeign);
        Assert.Contains("solo se ven los propios", PortOwners.Describe(foreign));
        Assert.Equal("nginx (PID 7)", PortOwners.Describe(new PortOwner(80, 7, "nginx")));
    }
}
