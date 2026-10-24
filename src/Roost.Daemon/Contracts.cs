namespace Roost.Daemon;

/// <summary>Un servicio del stack tal como lo cuenta <c>GET /status</c>.</summary>
public sealed record ServiceDto(string Name, string State, int? Pid, int Restarts, int? Port, string? LastError);

/// <summary>Un sitio <c>*.test</c> servido: su URL y el PHP que lo sirve.</summary>
public sealed record SiteDto(string Name, string Url, string Php);

/// <summary>
/// Respuesta de <c>GET /status</c>. <c>Db</c> es null si el stack va sin base de datos;
/// <c>Since</c>, desde cuándo está en marcha el stack actual. <c>WebVersion</c> es la versión
/// del servidor web, y <c>Web</c> cuál es («nginx» o «apache»; null en un daemon viejo: nginx).
/// <c>Ports</c> son los puertos del stack en marcha (los de sus servicios y el HTTPS de los
/// sitios), que son suyos aunque un servicio esté parado a mano; null en un daemon viejo.
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
    string? Web = null,
    IReadOnlyList<int>? Ports = null)
{
    /// <summary>«nginx 1.30.6», «Apache 2.4.69».</summary>
    public string? WebLabel => WebVersion is null ? null : $"{(Web == "apache" ? "Apache" : "nginx")} {WebVersion}";
}

/// <summary>
/// Respuesta de <c>POST /up</c>, <c>/down</c>, <c>/restart</c>, <c>/reload</c>, <c>/stop</c> y <c>/start</c>.
/// Tras un <c>up</c> que no se pudo aplicar, <c>Status.Running</c> dice si lo de antes sigue en
/// marcha (la configuración nueva se rechazó antes de parar nada) o si el stack ha quedado parado.
/// </summary>
public sealed record CommandResponse(bool Ok, string? Error, StatusResponse Status)
{
    /// <summary>
    /// Lo que se ha hecho con el stack en marcha, en palabras: «recargado nginx», «reiniciado
    /// php-8.3 (uno a uno)», «arrancado php-7.4» (E2, la recarga selectiva). Vacío si no se ha
    /// tocado nada, si el stack no estaba en marcha (un <c>up</c> que lo arranca) o en un daemon
    /// viejo. Con <see cref="Ok"/> a false y algo aquí, el cambio se ha aplicado a medias.
    /// </summary>
    public IReadOnlyList<string> Applied { get; init; } = [];
}
