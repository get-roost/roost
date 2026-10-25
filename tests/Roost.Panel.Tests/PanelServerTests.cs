using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Roost.Core.Platform;
using Roost.WebPanel;

namespace Roost.Panel.Tests;

/// <summary>
/// El panel de verdad (Kestrel en un puerto libre de 127.0.0.1) sobre una raíz de Roost temporal y vacía:
/// sin daemon (la API contesta «parado»), sin binarios y sin red (el catálogo no se pide). Prueba la
/// seguridad de punta a punta (token → cookie, 401, Host, Origin y la cabecera anti-CSRF, CSP, sin CORS)
/// y que las pantallas tienen datos con los que pintarse.
/// </summary>
public sealed class PanelServerTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "roost-panel-tests-" + Guid.NewGuid().ToString("n"));
    private PanelApp _app = null!;
    private PanelHost.Server _server = null!;
    private string _token = null!;
    private HttpClient _anonymous = null!;
    private HttpClient _session = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        var paths = new RoostPaths(_root, Path.Combine(_root, "cache"));
        _token = PanelState.NewToken();
        // Sin HttpClient a internet: el que haya no se usa en estos tests (nada instala ni consulta catálogos).
        _app = new PanelApp(paths, new HttpClient(new NoNetworkHandler()));
        _server = await PanelHost.StartAsync(_app, 0, _token, _ => { });
        _anonymous = Client(cookies: false);
        _session = Client(cookies: true);
        // La URL con el token deja la cookie y redirige sin él.
        var first = await _session.GetAsync("/?t=" + _token);
        Assert.Equal(HttpStatusCode.Found, first.StatusCode);
        Assert.Equal("/", first.Headers.Location!.ToString());
    }

    public Task DisposeAsync()
    {
        _anonymous.Dispose();
        _session.Dispose();
        _server.Dispose();
        _app.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
        return Task.CompletedTask;
    }

    private HttpClient Client(bool cookies, string? host = null)
    {
        var handler = new HttpClientHandler { UseCookies = cookies, AllowAutoRedirect = false };
        var client = new HttpClient(handler) { BaseAddress = new Uri(_server.BaseUrl) };
        if (host is not null) client.DefaultRequestHeaders.Host = host;
        return client;
    }

    private HttpRequestMessage Write(HttpMethod method, string path, bool origin = true, bool csrf = true, string? originValue = null)
    {
        var request = new HttpRequestMessage(method, path) { Content = JsonContent.Create(new { }) };
        if (origin) request.Headers.TryAddWithoutValidation("Origin", originValue ?? $"http://127.0.0.1:{_server.Port}");
        if (csrf) request.Headers.TryAddWithoutValidation(PanelSecurity.CsrfHeader, "1");
        return request;
    }

    [Fact]
    public void EscuchaSoloEnLoopback()
    {
        var urls = _server.App.Urls;
        Assert.All(urls, u => Assert.Contains("127.0.0.1", u));
        Assert.DoesNotContain(urls, u => u.Contains("0.0.0.0") || u.Contains("[::]") || u.Contains("*"));
    }

    [Fact]
    public async Task SinCookie_TodoEs401()
    {
        foreach (var path in new[] { "/", "/index.html", "/app.css", "/js/app.js", "/api/status", "/api/ping", "/api/home", "/api/sites" })
        {
            var response = await _anonymous.GetAsync(path);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }
        var post = await _anonymous.SendAsync(Write(HttpMethod.Post, "/api/stack/down"));
        Assert.Equal(HttpStatusCode.Unauthorized, post.StatusCode);
    }

    [Fact]
    public async Task TokenMalo_Es401_YNoDejaCookie()
    {
        var response = await _anonymous.GetAsync("/?t=" + PanelState.NewToken());
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.False(response.Headers.Contains("Set-Cookie"));
    }

    [Fact]
    public async Task TokenBueno_DejaLaCookieHttpOnlyStrict_YRedirigeSinEl()
    {
        using var client = Client(cookies: false);
        var response = await client.GetAsync("/?t=" + _token);
        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        Assert.Equal("/", response.Headers.Location!.ToString());
        var cookie = Assert.Single(response.Headers.GetValues("Set-Cookie"));
        Assert.StartsWith(PanelSecurity.CookieName + "=" + _token, cookie);
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("path=/", cookie, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ConCookie_LaInterfazYLaApiContestan()
    {
        var index = await _session.GetAsync("/");
        Assert.Equal(HttpStatusCode.OK, index.StatusCode);
        Assert.Equal("text/html", index.Content.Headers.ContentType!.MediaType);
        Assert.Contains("<title>Roost</title>", await index.Content.ReadAsStringAsync());

        var js = await _session.GetAsync("/js/app.js");
        Assert.Equal(HttpStatusCode.OK, js.StatusCode);
        Assert.Equal("text/javascript", js.Content.Headers.ContentType!.MediaType);

        var css = await _session.GetAsync("/app.css");
        Assert.Equal("text/css", css.Content.Headers.ContentType!.MediaType);

        var ping = await _session.GetFromJsonAsync<JsonElement>("/api/ping");
        Assert.True(ping.GetProperty("ok").GetBoolean());

        var missing = await _session.GetAsync("/no-existe.js");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        var missingApi = await _session.GetAsync("/api/no-existe");
        Assert.Equal(HttpStatusCode.NotFound, missingApi.StatusCode);
    }

    [Fact]
    public async Task HostAjeno_SeRechazaAntesQueNada()
    {
        using var evil = Client(cookies: false, host: "evil.example");
        // Ni con el token bueno: la comprobación de Host va primero.
        var response = await evil.GetAsync("/?t=" + _token);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.False(response.Headers.Contains("Set-Cookie"));

        using var otherPort = Client(cookies: false, host: $"127.0.0.1:{_server.Port + 1}");
        Assert.Equal(HttpStatusCode.BadRequest, (await otherPort.GetAsync("/api/ping")).StatusCode);

        // localhost y [::1] con el puerto bueno sí valen (con la cookie).
        using var localhost = Client(cookies: false, host: $"localhost:{_server.Port}");
        localhost.DefaultRequestHeaders.Add("Cookie", $"{PanelSecurity.CookieName}={_token}");
        Assert.Equal(HttpStatusCode.OK, (await localhost.GetAsync("/api/ping")).StatusCode);
    }

    [Fact]
    public async Task Post_SinOrigin_OConOrigenAjeno_OSinCabeceraAntiCsrf_Es403()
    {
        Assert.Equal(HttpStatusCode.Forbidden, (await _session.SendAsync(Write(HttpMethod.Post, "/api/stack/down", origin: false))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _session.SendAsync(Write(HttpMethod.Post, "/api/stack/down", originValue: "http://evil.example"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _session.SendAsync(Write(HttpMethod.Post, "/api/stack/down", originValue: $"http://localhost:{_server.Port}"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _session.SendAsync(Write(HttpMethod.Post, "/api/stack/down", csrf: false))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _session.SendAsync(Write(HttpMethod.Delete, "/api/sites/x", origin: false))).StatusCode);

        // Bien formado: pasa (y sin daemon, /stack/down dice que ya está parado).
        var ok = await _session.SendAsync(Write(HttpMethod.Post, "/api/stack/down"));
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        var body = await ok.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.GetProperty("ok").GetBoolean());
    }

    [Fact]
    public async Task Cabeceras_CspEstricta_SinCors_YLaApiSinCache()
    {
        var page = await _session.GetAsync("/");
        Assert.Equal(PanelSecurity.ContentSecurityPolicy, page.Headers.GetValues("Content-Security-Policy").Single());
        Assert.Equal("nosniff", page.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("DENY", page.Headers.GetValues("X-Frame-Options").Single());
        Assert.Equal("no-referrer", page.Headers.GetValues("Referrer-Policy").Single());
        Assert.False(page.Headers.Contains("Access-Control-Allow-Origin"));
        Assert.False(page.Headers.Contains("Server"));

        var api = await _session.GetAsync("/api/status");
        Assert.Equal(PanelSecurity.ContentSecurityPolicy, api.Headers.GetValues("Content-Security-Policy").Single());
        Assert.Contains("no-store", api.Headers.CacheControl!.ToString());
        Assert.False(api.Headers.Contains("Access-Control-Allow-Origin"));

        // Un preflight CORS no se contesta con permiso: OPTIONS no es GET, así que exige Origin propio y la cabecera.
        var preflight = new HttpRequestMessage(HttpMethod.Options, "/api/stack/down");
        preflight.Headers.TryAddWithoutValidation("Origin", "http://evil.example");
        preflight.Headers.TryAddWithoutValidation("Access-Control-Request-Method", "POST");
        var response = await _session.SendAsync(preflight);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"));
    }

    [Fact]
    public async Task ElToken_NoSaleEnLosErrores_NiEnLasRespuestas()
    {
        var unauthorized = await _anonymous.GetAsync("/");
        Assert.DoesNotContain(_token, await unauthorized.Content.ReadAsStringAsync());
        var status = await _session.GetStringAsync("/api/status");
        Assert.DoesNotContain(_token, status);
    }

    // La API sobre una raíz vacía: lo que necesitan las pantallas para pintarse, sin daemon ni binarios.

    [Fact]
    public async Task Status_SinDaemon_DiceParado_YLaBarraDeEstado()
    {
        var status = await _session.GetFromJsonAsync<JsonElement>("/api/status");
        Assert.True(IsNullOrMissing(status, "daemon"));
        Assert.Equal(_root, status.GetProperty("root").GetString());
        Assert.False(status.GetProperty("ca").GetProperty("created").GetBoolean());
        Assert.True(status.GetProperty("welcome").GetBoolean());   // sin ningún PHP de Roost
        Assert.Equal(0, status.GetProperty("sites").GetInt32());
        Assert.Equal(OperatingSystem.IsWindows() ? "windows" : "linux", status.GetProperty("os").GetString());
    }

    [Fact]
    public async Task Home_SinNada_ProponeIniciar_YNginxSeInstalaAlIniciar()
    {
        var home = await _session.GetFromJsonAsync<JsonElement>("/api/home");
        Assert.StartsWith("Parado.", home.GetProperty("summary").GetString());
        var services = home.GetProperty("services").EnumerateArray().ToList();
        var web = Assert.Single(services);
        Assert.Equal("nginx", web.GetProperty("name").GetString());
        Assert.Equal("Se instala solo al iniciar por primera vez", web.GetProperty("description").GetString());
        Assert.Equal("Parado", web.GetProperty("state").GetString());
        Assert.Empty(home.GetProperty("sites").EnumerateArray());
    }

    [Fact]
    public async Task StackUp_SinPhp_DiceQueInstalesUno_ConElTextoDeRoost()
    {
        var response = await _session.SendAsync(Write(HttpMethod.Post, "/api/stack/up"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains("No hay ningun PHP instalado por Roost", body.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Sitios_Php_Web_Bd_Auditoria_Registros_Ajustes_Novedades_Contestan()
    {
        var sites = await _session.GetFromJsonAsync<JsonElement>("/api/sites");
        Assert.Empty(sites.GetProperty("sites").EnumerateArray());
        Assert.Equal("hosts", sites.GetProperty("resolver").GetString());

        var php = await _session.GetFromJsonAsync<JsonElement>("/api/php");   // sin ?catalog: no va a la red
        Assert.Empty(php.GetProperty("installed").EnumerateArray());
        Assert.True(IsNullOrMissing(php, "available"));

        var web = await _session.GetFromJsonAsync<JsonElement>("/api/web");
        Assert.Equal("nginx", web.GetProperty("server").GetString());
        Assert.False(web.GetProperty("nginx").GetProperty("installed").GetBoolean());
        // En Linux, Apache es el de la distro: se puede elegir si está, y si no, el motivo dice cómo ponerlo.
        var apache = web.GetProperty("apache");
        var distroApache = OperatingSystem.IsWindows() || Core.Install.LinuxApache.Locate(Core.Scanning.ProcessRunner.Default) is not null;
        Assert.Equal(!OperatingSystem.IsWindows(), apache.GetProperty("distro").GetBoolean());
        Assert.Equal(distroApache, apache.GetProperty("available").GetBoolean());
        if (!distroApache) Assert.Contains("apt install apache2", apache.GetProperty("why").GetString());

        var db = await _session.GetFromJsonAsync<JsonElement>("/api/db");
        Assert.True(IsNullOrMissing(db, "global"));
        Assert.Equal("127.0.0.1", db.GetProperty("host").GetString());

        var audit = await _session.GetFromJsonAsync<JsonElement>("/api/audit");
        Assert.False(audit.GetProperty("scanned").GetBoolean());
        Assert.Equal("Embedded", audit.GetProperty("dataset").GetProperty("origin").GetString());

        // Con la raíz vacía solo puede salir el log del helper, que es de ruta fija en la máquina.
        var logs = await _session.GetFromJsonAsync<JsonElement>("/api/logs");
        Assert.All(logs.GetProperty("files").EnumerateArray(), f => Assert.Equal("helper", f.GetProperty("id").GetString()));

        var settings = await _session.GetFromJsonAsync<JsonElement>("/api/settings");
        Assert.Equal(Path.Combine(_root, "roost.toml"), settings.GetProperty("configFile").GetString());
        Assert.False(settings.GetProperty("ca").GetProperty("created").GetBoolean());

        var news = await _session.GetFromJsonAsync<JsonElement>("/api/news");
        Assert.NotEmpty(news.GetProperty("entries").EnumerateArray());
    }

    [Fact]
    public async Task Sitio_Nuevo_SeRegistra_YSeQuita_ConElHostsDePruebas()
    {
        // ROOST_HOSTS_FILE: el hosts de pruebas, que Roost escribe directamente, sin helper ni permiso.
        var hostsFile = Path.Combine(_root, "hosts.txt");
        File.WriteAllText(hostsFile, "127.0.0.1 localhost\n");
        var before = Environment.GetEnvironmentVariable("ROOST_HOSTS_FILE");
        Environment.SetEnvironmentVariable("ROOST_HOSTS_FILE", hostsFile);
        try
        {
            var preview = await _session.GetFromJsonAsync<JsonElement>("/api/sites/preview?name=tienda");
            Assert.True(preview.GetProperty("validName").GetBoolean());
            Assert.False(preview.GetProperty("exists").GetBoolean());

            var created = await _session.SendAsync(new HttpRequestMessage(HttpMethod.Post, "/api/sites")
            {
                Content = JsonContent.Create(new { name = "tienda", createDatabase = false }),
                Headers = { { "Origin", $"http://127.0.0.1:{_server.Port}" }, { PanelSecurity.CsrfHeader, "1" } },
            });
            Assert.Equal(HttpStatusCode.OK, created.StatusCode);
            var body = await created.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("tienda.test", body.GetProperty("site").GetProperty("host").GetString());
            Assert.True(body.GetProperty("site").GetProperty("folderCreated").GetBoolean());
            Assert.True(body.GetProperty("hosts").GetProperty("ok").GetBoolean());
            Assert.Contains("127.0.0.1 tienda.test", File.ReadAllText(hostsFile));
            Assert.True(File.Exists(Path.Combine(_root, "www", "tienda", "index.php")));

            var list = await _session.GetFromJsonAsync<JsonElement>("/api/sites");
            var site = Assert.Single(list.GetProperty("sites").EnumerateArray());
            Assert.True(site.GetProperty("inHosts").GetBoolean());
            Assert.True(site.GetProperty("folderExists").GetBoolean());

            var removed = await _session.SendAsync(Write(HttpMethod.Delete, "/api/sites/tienda"));
            Assert.Equal(HttpStatusCode.OK, removed.StatusCode);
            Assert.DoesNotContain("tienda.test", File.ReadAllText(hostsFile));
            Assert.True(Directory.Exists(Path.Combine(_root, "www", "tienda")));   // la carpeta nunca se toca

            var missing = await _session.SendAsync(Write(HttpMethod.Delete, "/api/sites/tienda"));
            Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
        }
        finally
        {
            Environment.SetEnvironmentVariable("ROOST_HOSTS_FILE", before);
        }
    }

    [Fact]
    public async Task Tareas_Desconocidas_404_YCancelarUnaQueNoExiste_404()
    {
        Assert.Equal(HttpStatusCode.NotFound, (await _session.GetAsync("/api/jobs/no")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _session.SendAsync(Write(HttpMethod.Post, "/api/jobs/no/cancel"))).StatusCode);
        var running = await _session.GetFromJsonAsync<JsonElement>("/api/jobs");
        Assert.Empty(running.EnumerateArray());
    }

    /// <summary>Los null no se escriben (WhenWritingNull): la propiedad falta o es null.</summary>
    private static bool IsNullOrMissing(JsonElement element, string name) =>
        !element.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null;

    /// <summary>Si algo intentara ir a internet en los tests, falla claro en vez de colgarse.</summary>
    private sealed class NoNetworkHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new HttpRequestException("Los tests del panel no van a la red: " + request.RequestUri);
    }
}
