namespace Roost.Cli.Net;

/// <summary>
/// HttpClient compartido por los comandos que van a la red (hoy, los de <c>roost php</c>).
/// El timeout cubre hasta recibir las cabeceras; la descarga del zip en si no lo tiene,
/// para que una conexion lenta no corte un PHP de 30 MB a medias.
/// </summary>
internal static class RoostHttp
{
    public static readonly HttpClient Client = Create();

    private static HttpClient Create()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("roost/" + Roost.Core.Updates.RoostVersion.Current);
        return http;
    }
}
