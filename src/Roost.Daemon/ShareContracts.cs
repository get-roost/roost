using System.Net;
using Roost.Core.Install;
using Roost.Core.Scanning;
using Roost.Core.Share;

namespace Roost.Daemon;

/// <summary>
/// Lo que se está compartiendo de un sitio (<c>roost share</c>), tal como lo cuentan <c>GET /shares</c>
/// y las respuestas de <c>/share/…</c>.
/// </summary>
/// <param name="Kind">«tunnel» (por internet) o «lan» (en la red local).</param>
/// <param name="Url">La URL que hay que dar: <c>https://xxx.trycloudflare.com</c> o <c>https://192.168.1.20:8443/</c>.</param>
/// <param name="Address">Donde escucha el servidor web para esto: 127.0.0.1 (el túnel) o la IP de la red local.</param>
/// <param name="Connected">Con el túnel, si cloudflared ya tiene su conexión con Cloudflare (antes, la URL aún da error).</param>
public sealed record ShareDto(string Site, string Kind, string Url, string Address, int Port, DateTimeOffset Since, bool Connected = true)
{
    public bool IsTunnel => Kind == ShareListener.KindKey(ShareKind.Tunnel);
}

/// <summary>Respuesta de <c>GET /shares</c>.</summary>
public sealed record SharesResponse(IReadOnlyList<ShareDto> Shares);

/// <summary>
/// Respuesta de <c>POST /share/{sitio}/tunnel|lan</c>, <c>…/stop</c> y <c>…/hold</c>. Con <c>hold</c>,
/// <see cref="Error"/> dice por qué se ha dejado de compartir (aunque <see cref="Ok"/> sea true).
/// </summary>
public sealed record ShareResponse(bool Ok, string? Error, ShareDto? Share)
{
    /// <summary>Lo que se ha hecho con el stack en marcha para abrir o cerrar la escucha («recargado nginx»).</summary>
    public IReadOnlyList<string> Applied { get; init; } = [];
}

/// <summary>El cuerpo de <c>POST /share/{sitio}/lan</c>: la IP de la red local y el puerto (los elige el cliente, que pone antes la regla del cortafuegos).</summary>
public sealed record ShareLanRequest(string Address, int Port);

/// <summary>
/// Lo que necesita compartir del sistema, y que los tests sustituyen: quien ejecuta cloudflared
/// (con sus líneas en vivo), cómo se elige un puerto libre de loopback para la escucha del túnel,
/// si se puede abrir un puerto en una IP, si una IP sigue siendo de este equipo y cuánto se espera
/// a la URL y a la conexión.
/// </summary>
internal sealed record ShareEnvironment(
    ICommandRunner Commands,
    Func<int> FreeLoopbackPort,
    Func<IPAddress, int, bool> CanBind,
    Func<IPAddress, bool> IsLocalAddress,
    TimeSpan UrlTimeout,
    TimeSpan ConnectTimeout,
    Func<Roost.Core.Platform.RoostPaths, string?>? Cloudflared = null)
{
    public static ShareEnvironment Real => new(
        CommandRunner.Default,
        LanNetwork.FreeLoopbackPort,
        LanNetwork.CanBind,
        address => LanNetwork.Candidates().Any(c => c.Address.Equals(address)),
        QuickTunnel.UrlTimeout,
        QuickTunnel.ConnectTimeout);

    /// <summary>El cloudflared que se lanza: el de Roost (<c>bin/cloudflared</c>); null si no está instalado.</summary>
    public string? CloudflaredExe(Roost.Core.Platform.RoostPaths paths) =>
        Cloudflared is { } find ? find(paths) : new CloudflaredInstaller(paths).Locate()?.Executable;
}
