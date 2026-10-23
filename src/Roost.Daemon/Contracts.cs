namespace Roost.Daemon;

/// <summary>Un servicio del stack tal como lo cuenta <c>GET /status</c>.</summary>
public sealed record ServiceDto(string Name, string State, int? Pid, int Restarts, int? Port, string? LastError);

/// <summary>Un sitio <c>*.test</c> servido: su URL y el PHP que lo sirve.</summary>
public sealed record SiteDto(string Name, string Url, string Php);

/// <summary>
/// Respuesta de <c>GET /status</c>. <c>Db</c> es null si el stack va sin base de datos;
/// <c>Since</c>, desde cuándo está en marcha el stack actual. <c>WebVersion</c> es la versión
/// del servidor web, y <c>Web</c> cuál es («nginx» o «apache»; null en un daemon viejo: nginx).
/// </summary>
public sealed record StatusResponse(
    int DaemonPid,
    bool Running,
    string? Url,
    string? Php,
    string? WebVersion,
    string? Db,
    int? DbPort,
    IReadOnlyList<SiteDto> Sites,
    IReadOnlyList<ServiceDto> Services,
    DateTimeOffset? Since = null,
    string? Web = null)
{
    /// <summary>«nginx 1.30.6», «Apache 2.4.69».</summary>
    public string? WebLabel => WebVersion is null ? null : $"{(Web == "apache" ? "Apache" : "nginx")} {WebVersion}";
}

/// <summary>Respuesta de <c>POST /up</c> y <c>POST /down</c>.</summary>
public sealed record CommandResponse(bool Ok, string? Error, StatusResponse Status);
