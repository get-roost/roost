using System.Buffers.Binary;
using System.Net;
using Roost.Core.Config;
using Roost.Core.Dns;
using Roost.Core.Platform;
using Roost.Core.Sites;

namespace Roost.Core.Tests;

/// <summary>
/// Testea el DNS local: cómo lee y contesta las preguntas (puro), el servidor de verdad en un puerto
/// efímero de loopback (no el 53), las reglas NRPT leídas como datos y que en modo DNS los sitios no
/// tocan el hosts. Nada pone ni quita reglas en el sistema.
/// </summary>
public class LocalDnsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "roost-dns-" + Guid.NewGuid().ToString("N"));

    private static byte[] Ask(string name, ushort type = DnsMessage.TypeA)
    {
        Assert.True(DnsMessage.TryParseQuery(LocalDnsProbe.Query(0x1234, name, type), out var query));
        return DnsMessage.Answer(query!);
    }

    private static int Rcode(byte[] response) => response[3] & 0x0F;
    private static int Answers(byte[] response) => BinaryPrimitives.ReadUInt16BigEndian(response.AsSpan(6));

    [Theory]
    [InlineData("tienda.test")]
    [InlineData("api.tienda.test")]
    [InlineData("TIENDA.Test")]
    public void Test_EsCientoVeintisieteCeroCeroUno(string name)
    {
        var response = Ask(name);

        Assert.Equal(0x1234, BinaryPrimitives.ReadUInt16BigEndian(response));
        Assert.Equal(0x84, response[2] & 0x84);   // respuesta (QR) y con autoridad (AA)
        Assert.Equal(0, Rcode(response));
        Assert.Equal(IPAddress.Loopback, LocalDnsProbe.FirstAddress(response));
    }

    [Fact]
    public void Aaaa_EsUnoLocalIpv6_PorqueElServidorWebEscuchaEnLasDos()
    {
        var response = Ask("tienda.test", DnsMessage.TypeAaaa);

        Assert.Equal(0, Rcode(response));
        Assert.Equal(1, Answers(response));
        // El registro de respuesta: nombre (puntero, 2), tipo (2), clase (2), TTL (4), longitud (2) y los 16 bytes.
        Assert.Equal(DnsMessage.TypeAaaa, BinaryPrimitives.ReadUInt16BigEndian(response.AsSpan(response.Length - 26)));
        Assert.Equal(16, BinaryPrimitives.ReadUInt16BigEndian(response.AsSpan(response.Length - 18)));
        Assert.Equal(IPAddress.IPv6Loopback, new IPAddress(response.AsSpan(response.Length - 16)));
    }

    [Fact]
    public void Aaaa_SinIpv6EnLaMaquina_VaSinRespuesta_ParaQueSeUseIpv4()
    {
        Assert.True(DnsMessage.TryParseQuery(LocalDnsProbe.Query(0x1234, "tienda.test", DnsMessage.TypeAaaa), out var query));
        var response = DnsMessage.Answer(query!, ipv6: false);

        Assert.Equal(0, Rcode(response));   // NODATA, no un error: el nombre existe
        Assert.Equal(0, Answers(response));
    }

    [Theory]
    [InlineData("example.com")]
    [InlineData("tienda.testing")]
    [InlineData("test.com")]
    public void LoQueNoEsPuntoTest_SeRechaza(string name)
    {
        var response = Ask(name);

        Assert.Equal(5, Rcode(response));   // REFUSED
        Assert.Equal(0, Answers(response));
    }

    [Fact]
    public void UnMensajeRaro_NoSeLee_YSeContestaFormerrConSuId()
    {
        Assert.False(DnsMessage.TryParseQuery([0x12, 0x34, 0x01], out _));
        var answer = Ask("tienda.test");
        Assert.False(DnsMessage.TryParseQuery(answer, out _));   // una respuesta no es una pregunta
        var truncated = LocalDnsProbe.Query(7, "tienda.test", DnsMessage.TypeA)[..15];
        Assert.False(DnsMessage.TryParseQuery(truncated, out _));

        var error = DnsMessage.FormatError(truncated)!;
        Assert.Equal(7, BinaryPrimitives.ReadUInt16BigEndian(error));
        Assert.Equal(1, Rcode(error));
    }

    [Fact]
    public async Task ElServidor_ContestaPorUdp_YNoComparteSuPuerto()
    {
        await using var server = LocalDnsServer.Start(new IPEndPoint(IPAddress.Loopback, 0));

        Assert.True(await LocalDnsProbe.AsksAsync("tienda.test", endPoint: server.EndPoint));
        Assert.False(await LocalDnsProbe.AsksAsync("example.com", endPoint: server.EndPoint));
        Assert.True(server.Answered >= 2);
        var ex = Assert.Throws<RoostException>(() => LocalDnsServer.Start(server.EndPoint));
        Assert.Contains("roost dns remove", ex.Message);
    }

    [Fact]
    public async Task SinServidor_LaSondaDiceQueNo_SinLanzar()
    {
        var free = new IPEndPoint(IPAddress.Loopback, 0);
        await using (var server = LocalDnsServer.Start(free)) free = server.EndPoint;

        Assert.False(await LocalDnsProbe.AsksAsync("tienda.test", endPoint: free));
    }

    [Fact]
    public void Nrpt_ReconoceLaReglaDeRoost_YLaDeOtros()
    {
        NrptRule tailscale = new([".100.100.in-addr.arpa"], "100.100.100.100;fd7a:115c:a1e0::53", null);
        NrptRule roost = new([".test"], "127.53.0.1", "Roost");
        NrptRule otra = new([".test"], "127.0.0.1", "Acrylic");

        Assert.False(LocalDns.HasRoostRule([tailscale]));
        Assert.True(LocalDns.HasRoostRule([tailscale, roost]));
        Assert.Null(LocalDns.ForeignRule([tailscale, roost]));
        Assert.Equal(otra, LocalDns.ForeignRule([tailscale, otra]));
    }

    [Fact]
    public void Resolver_SeLeeYSeGuarda_YUnoQueNoExisteFalla()
    {
        var file = Path.Combine(_root, "roost.toml");
        Assert.False(RoostConfig.Load(file).UsesLocalDns);   // por defecto, el hosts

        RoostConfig.SetResolver(file, RoostConfig.ResolverDns);
        Assert.True(RoostConfig.Load(file).UsesLocalDns);

        File.WriteAllText(file, "[sites]\nresolver = \"mdns\"\n");
        Assert.Throws<RoostException>(() => RoostConfig.Load(file));
    }

    [Fact]
    public void ConDnsLocal_LosSitiosNoTocanElHosts()
    {
        var paths = new RoostPaths(_root, Path.Combine(_root, "cache"));
        RoostConfig.SetResolver(paths.ConfigFile, RoostConfig.ResolverDns);
        var sites = new SiteManager(paths);
        sites.Add("tienda");

        // Con el hosts del sistema y un helper que no existe, si lo intentara, lanzaría.
        Assert.False(sites.SyncHosts(Path.Combine(_root, "no-existe.exe")));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }
}
