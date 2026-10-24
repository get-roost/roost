using System.Net;
using Roost.Core.Platform;
using Roost.Core.Updates;

namespace Roost.Core.Tests;

/// <summary>
/// El aviso de actualizaciones SIN red: un <see cref="HttpMessageHandler"/> falso hace de API
/// de GitHub, la caché va a una carpeta temporal y el reloj es de mentira.
/// </summary>
public class UpdateCheckerTests : IDisposable
{
    private readonly string _root;
    private readonly RoostPaths _paths;
    private readonly ClockStub _clock = new(new DateTimeOffset(2026, 11, 3, 9, 0, 0, TimeSpan.Zero));

    public UpdateCheckerTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "roost-updates-" + Guid.NewGuid().ToString("N"));
        _paths = new RoostPaths(_root, Path.Combine(_root, "cache"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private sealed class ClockStub(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class FakeGitHub(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        public FakeGitHub(HttpStatusCode status, string body = "") : this((_, _) => Task.FromResult(
            new HttpResponseMessage(status) { Content = new StringContent(body) }))
        {
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return respond(request, cancellationToken);
        }
    }

    /// <summary>Una release como la devuelve /releases/latest (recortada: GitHub trae muchos más campos).</summary>
    public static string Release(string tag = "v0.2.0", bool prerelease = false, bool draft = false, string body = "## Novedades\r\n\r\n- **nginx 1.30.7**\r\n- `roost update`") => $$"""
        {
          "url": "https://api.github.com/repos/get-roost/roost/releases/1",
          "html_url": "https://github.com/get-roost/roost/releases/tag/{{tag}}",
          "id": 1,
          "author": { "login": "get-roost" },
          "tag_name": "{{tag}}",
          "name": "Roost {{tag.TrimStart('v')}}",
          "draft": {{(draft ? "true" : "false")}},
          "prerelease": {{(prerelease ? "true" : "false")}},
          "created_at": "2026-11-02T10:00:00Z",
          "published_at": "2026-11-02T10:05:00Z",
          "assets": [
            { "name": "roost-{{tag.TrimStart('v')}}-win-x64.zip", "size": 52428800, "browser_download_url": "https://github.com/get-roost/roost/releases/download/{{tag}}/roost-{{tag.TrimStart('v')}}-win-x64.zip" },
            { "name": "roost-{{tag.TrimStart('v')}}-linux-x64.tar.gz", "size": 31457280, "browser_download_url": "https://github.com/get-roost/roost/releases/download/{{tag}}/roost-{{tag.TrimStart('v')}}-linux-x64.tar.gz" },
            { "name": "SHA256SUMS.txt", "size": 256, "browser_download_url": "https://github.com/get-roost/roost/releases/download/{{tag}}/SHA256SUMS.txt" }
          ],
          "body": {{System.Text.Json.JsonSerializer.Serialize(body)}}
        }
        """;

    private UpdateChecker Checker(HttpMessageHandler handler, string installed = "0.1.0", Uri? endpoint = null, TimeSpan? timeout = null) =>
        new(_paths, new HttpClient(handler), installed, endpoint ?? GitHubReleases.LatestUrl, _clock, timeout);

    [Fact]
    public async Task Release200_ConUnaNueva_AvisaYGuardaLaCache()
    {
        var github = new FakeGitHub(HttpStatusCode.OK, Release());
        var check = await Checker(github).CheckAsync();

        Assert.Equal(UpdateStatus.Available, check.Status);
        Assert.True(check.UpdateAvailable);
        Assert.False(check.FromCache);
        Assert.Equal("0.1.0", check.Installed);
        var latest = Assert.IsType<ReleaseInfo>(check.Latest);
        Assert.Equal("v0.2.0", latest.Tag);
        Assert.Equal(SemanticVersion.Parse("0.2.0"), latest.Version);
        Assert.Equal("Roost 0.2.0", latest.Name);
        Assert.Equal("https://github.com/get-roost/roost/releases/tag/v0.2.0", latest.Page.ToString());
        Assert.Equal(new DateTimeOffset(2026, 11, 2, 10, 5, 0, TimeSpan.Zero), latest.PublishedAt);
        Assert.Equal(3, latest.Assets.Count);
        Assert.Equal("roost-0.2.0-win-x64.zip", latest.PackageFor(windows: true)?.Name);
        Assert.Equal("roost-0.2.0-linux-x64.tar.gz", latest.PackageFor(windows: false)?.Name);
        Assert.Equal(_clock.Now, check.CheckedAt);
        Assert.True(File.Exists(Path.Combine(_paths.Cache, "update-check.json")));

        // Lo que pide la API de GitHub.
        var request = Assert.Single(github.Requests);
        Assert.Equal(GitHubReleases.LatestUrl, request.RequestUri);
        Assert.Equal("roost/0.1.0", request.Headers.UserAgent.ToString());
        Assert.Equal("application/vnd.github+json", request.Headers.Accept.ToString());
    }

    [Fact]
    public async Task Release200_LaMismaQueLaInstalada_AlDia()
    {
        var check = await Checker(new FakeGitHub(HttpStatusCode.OK, Release("v0.2.0")), installed: "0.2.0").CheckAsync();

        Assert.Equal(UpdateStatus.UpToDate, check.Status);
        Assert.False(check.UpdateAvailable);
        Assert.False(check.InstalledIsNewer);
    }

    [Fact]
    public async Task BuildDeDesarrollo_MasNuevaQueLaPublicada_AlDia()
    {
        var check = await Checker(new FakeGitHub(HttpStatusCode.OK, Release("v0.2.0")), installed: "0.3.0-dev").CheckAsync();

        Assert.Equal(UpdateStatus.UpToDate, check.Status);
        Assert.True(check.InstalledIsNewer);
        Assert.False(check.UpdateAvailable);
    }

    [Fact]
    public async Task PrereleaseInstalada_SuVersionFinalEsNueva()
    {
        var check = await Checker(new FakeGitHub(HttpStatusCode.OK, Release("v0.2.0")), installed: "0.2.0-beta.1").CheckAsync();

        Assert.Equal(UpdateStatus.Available, check.Status);
    }

    [Fact]
    public async Task Respuesta404_AunNoHayNingunaVersion_YSeGuardaEnLaCache()
    {
        var github = new FakeGitHub(HttpStatusCode.NotFound, """{"message":"Not Found","documentation_url":"https://docs.github.com"}""");
        var checker = Checker(github);

        var check = await checker.CheckAsync();
        Assert.Equal(UpdateStatus.NoReleases, check.Status);
        Assert.Null(check.Latest);
        Assert.Null(check.Error);

        var cached = checker.Cached();
        Assert.NotNull(cached);
        Assert.Equal(UpdateStatus.NoReleases, cached.Status);
        Assert.True(cached.FromCache);
    }

    [Fact]
    public async Task ErrorDeRed_NoLanza_YDiceElMotivo()
    {
        var github = new FakeGitHub((_, _) => throw new HttpRequestException("No such host is known."));
        var check = await Checker(github).CheckAsync();

        Assert.Equal(UpdateStatus.Failed, check.Status);
        Assert.Contains("api.github.com", check.Error);
        Assert.Contains("No such host", check.Error);
        Assert.Null(check.Latest);
        Assert.False(File.Exists(Path.Combine(_paths.Cache, "update-check.json")));   // un fallo no se guarda
    }

    [Fact]
    public async Task SinRespuesta_SeCortaPorTiempo()
    {
        var github = new FakeGitHub(async (_, token) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        var check = await Checker(github, timeout: TimeSpan.FromMilliseconds(50)).CheckAsync();

        Assert.Equal(UpdateStatus.Failed, check.Status);
        Assert.Contains("no ha contestado", check.Error);
    }

    [Theory]
    [InlineData("<html><body>Unicorn!</body></html>", "no es JSON valido")]
    [InlineData("""{"message":"Moved Permanently"}""", "no es una release")]
    [InlineData("""[1, 2, 3]""", null)]                       // una lista sin releases: no hay ninguna
    [InlineData("\"v0.2.0\"", "no es un objeto")]
    [InlineData("""{"tag_name":"latest","html_url":"https://github.com/x"}""", "no es una version")]
    [InlineData("""{"tag_name":"v0.2.0","html_url":"javascript:alert(1)"}""", "html_url")]
    public async Task JsonRaro_NoLanza(string body, string? error)
    {
        var check = await Checker(new FakeGitHub(HttpStatusCode.OK, body)).CheckAsync();

        if (error is null)
            Assert.Equal(UpdateStatus.NoReleases, check.Status);
        else
        {
            Assert.Equal(UpdateStatus.Failed, check.Status);
            Assert.Contains(error, check.Error);
        }
    }

    [Fact]
    public async Task LimiteDeLaApi_LoDiceClaro()
    {
        var github = new FakeGitHub((_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = new StringContent("""{"message":"API rate limit exceeded"}""") };
            response.Headers.Add("X-RateLimit-Remaining", "0");
            return Task.FromResult(response);
        });
        var check = await Checker(github).CheckAsync();

        Assert.Equal(UpdateStatus.Failed, check.Status);
        Assert.Contains("60 por hora", check.Error);
    }

    [Fact]
    public async Task ErrorDelServidor_NoLanza()
    {
        var check = await Checker(new FakeGitHub(HttpStatusCode.BadGateway)).CheckAsync();

        Assert.Equal(UpdateStatus.Failed, check.Status);
        Assert.Contains("502", check.Error);
    }

    [Fact]
    public async Task CacheFresca_NoVaALaRed()
    {
        var github = new FakeGitHub(HttpStatusCode.OK, Release());
        var checker = Checker(github);
        await checker.CheckAsync();

        _clock.Now += TimeSpan.FromHours(23);
        var check = await checker.CheckAsync();

        Assert.Single(github.Requests);
        Assert.True(check.FromCache);
        Assert.Equal(UpdateStatus.Available, check.Status);
        Assert.Equal("v0.2.0", check.Latest?.Tag);
        Assert.Equal("Roost 0.2.0", check.Latest?.Name);
        Assert.Equal(3, check.Latest?.Assets.Count);
        Assert.Contains("nginx 1.30.7", check.Latest?.Notes);
    }

    [Fact]
    public async Task CacheCaducada_VuelveAPreguntar()
    {
        var github = new FakeGitHub(HttpStatusCode.OK, Release());
        var checker = Checker(github);
        await checker.CheckAsync();

        _clock.Now += TimeSpan.FromHours(25);
        var check = await checker.CheckAsync();

        Assert.Equal(2, github.Requests.Count);
        Assert.False(check.FromCache);
        Assert.Equal(_clock.Now, check.CheckedAt);
    }

    [Fact]
    public async Task Forzar_PreguntaAunqueLaCacheSeaFresca()
    {
        var github = new FakeGitHub(HttpStatusCode.OK, Release());
        var checker = Checker(github);
        await checker.CheckAsync();

        var check = await checker.CheckAsync(force: true);

        Assert.Equal(2, github.Requests.Count);
        Assert.False(check.FromCache);
    }

    [Fact]
    public async Task CacheDelFuturo_NoSeDaPorBuena()
    {
        var checker = Checker(new FakeGitHub(HttpStatusCode.OK, Release()));
        await checker.CheckAsync();

        _clock.Now -= TimeSpan.FromDays(2);   // el reloj ha ido hacia atrás
        Assert.False(checker.IsFresh(checker.Cached()!));
    }

    [Fact]
    public async Task FalloConCacheVieja_SigueAvisandoDeLaNueva()
    {
        await Checker(new FakeGitHub(HttpStatusCode.OK, Release())).CheckAsync();
        _clock.Now += TimeSpan.FromDays(3);

        var check = await Checker(new FakeGitHub((_, _) => throw new HttpRequestException("sin red"))).CheckAsync();

        Assert.Equal(UpdateStatus.Failed, check.Status);
        Assert.True(check.UpdateAvailable);
        Assert.True(check.FromCache);
        Assert.Equal("v0.2.0", check.Latest?.Tag);
        Assert.Equal(_clock.Now - TimeSpan.FromDays(3), check.CheckedAt);
    }

    [Fact]
    public async Task LaCache_SeComparaConLaVersionDeAhora()
    {
        // Se comprobó con la 0.1.0 instalada; luego se actualizó a la 0.2.0.
        await Checker(new FakeGitHub(HttpStatusCode.OK, Release())).CheckAsync();

        var cached = Checker(new FakeGitHub(HttpStatusCode.OK, Release()), installed: "0.2.0").Cached();

        Assert.Equal(UpdateStatus.UpToDate, cached?.Status);
    }

    [Fact]
    public async Task LaCache_DeOtraUrl_NoVale()
    {
        await Checker(new FakeGitHub(HttpStatusCode.OK, Release())).CheckAsync();

        Assert.Null(Checker(new FakeGitHub(HttpStatusCode.OK), endpoint: new Uri("http://127.0.0.1:8123/latest.json")).Cached());
    }

    [Fact]
    public async Task LaCache_Rota_SeIgnoraYSePreguntaOtraVez()
    {
        Directory.CreateDirectory(_paths.Cache);
        File.WriteAllText(Path.Combine(_paths.Cache, "update-check.json"), "{ esto no es json");
        var github = new FakeGitHub(HttpStatusCode.OK, Release());
        var checker = Checker(github);

        Assert.Null(checker.Cached());
        var check = await checker.CheckAsync();

        Assert.Equal(UpdateStatus.Available, check.Status);
        Assert.Single(github.Requests);
        Assert.NotNull(checker.Cached());   // y queda una buena
    }

    [Fact]
    public async Task VersionInstaladaIrreconocible_NoLanza()
    {
        var check = await Checker(new FakeGitHub(HttpStatusCode.OK, Release()), installed: "dev").CheckAsync();

        Assert.Equal(UpdateStatus.Failed, check.Status);
        Assert.Contains("dev", check.Error);
        Assert.Equal("v0.2.0", check.Latest?.Tag);
    }

    [Fact]
    public void Parse_LatestQueEsPrereleaseOBorrador_NoCuenta()
    {
        Assert.True(GitHubReleases.TryParse(Release("v0.3.0", prerelease: true), out var pre, out _));
        Assert.Null(pre);
        Assert.True(GitHubReleases.TryParse(Release("v0.3.0", draft: true), out var draft, out _));
        Assert.Null(draft);
        // Una etiqueta con guion es prerelease aunque no la marcaran.
        Assert.True(GitHubReleases.TryParse(Release("v0.3.0-beta.1"), out var tagged, out _));
        Assert.Null(tagged);
    }

    [Fact]
    public void Parse_LosFicheros_SoloPorHttps_SalvoEnLoopbackParaProbar()
    {
        var json = """
            {
              "tag_name": "v0.2.0", "html_url": "https://github.com/get-roost/roost/releases/tag/v0.2.0",
              "assets": [
                { "name": "roost-0.2.0-win-x64-setup.exe", "size": 1, "browser_download_url": "http://127.0.0.1:8767/roost-0.2.0-win-x64-setup.exe" },
                { "name": "SHA256SUMS.txt", "size": 1, "browser_download_url": "http://localhost:8767/SHA256SUMS.txt" },
                { "name": "roost-0.2.0-win-x64.zip", "size": 1, "browser_download_url": "http://descargas.example.com/roost-0.2.0-win-x64.zip" },
                { "name": "roost-0.2.0-linux-x64.tar.gz", "size": 1, "browser_download_url": "ftp://github.com/x.tar.gz" }
              ]
            }
            """;

        Assert.True(GitHubReleases.TryParse(json, out var release, out _));
        Assert.Equal(["roost-0.2.0-win-x64-setup.exe", "SHA256SUMS.txt"], release!.Assets.Select(a => a.Name));
    }

    [Fact]
    public void Parse_ListaDeReleases_SeQuedaConLaEstableMasNueva()
    {
        var json = "[" + string.Join(",", Release("v0.4.0", draft: true), Release("v0.3.0-beta.1", prerelease: true),
            Release("v0.1.0"), Release("v0.2.0")) + "]";

        Assert.True(GitHubReleases.TryParse(json, out var release, out _));
        Assert.Equal("v0.2.0", release?.Tag);
    }

    [Fact]
    public void Notas_EnCorto_SinMarkdown()
    {
        var notes = """
            ## Novedades

            - **nginx 1.30.7**, con el arreglo de [CVE-2026-1234](https://nginx.org/x).
            * `roost update` avisa de las versiones nuevas
            ---
            ```sh
            roost update
            ```
            <!-- nota interna -->
            Gracias a __todos__.
            - uno
            - dos
            - tres
            """;

        var lines = GitHubReleases.Summarize(notes, maxLines: 4);

        Assert.Equal(
        [
            "- nginx 1.30.7, con el arreglo de CVE-2026-1234.",
            "- roost update avisa de las versiones nuevas",
            "Gracias a todos.",
            "- uno",
            "…",
        ], lines);
        Assert.Empty(GitHubReleases.Summarize(null));
        Assert.Empty(GitHubReleases.Summarize("  \r\n "));
    }

    [Theory]
    [InlineData(@"C:\Users\ana\scoop\apps\roost\current\", InstallChannel.Scoop)]
    [InlineData(@"C:\Users\ana\scoop\apps\roost\0.2.0\roost.exe", InstallChannel.Scoop)]
    [InlineData(@"C:\ProgramData\scoop\apps\roost\current\roost-desktop.exe", InstallChannel.Scoop)]
    [InlineData(@"D:\herramientas\apps\roost\current\", InstallChannel.Scoop)]          // SCOOP en otra carpeta
    [InlineData(@"C:\Users\ana\AppData\Local\Microsoft\WinGet\Packages\Roost.Roost_Microsoft.Winget.Source_8wekyb3d8bbwe\", InstallChannel.Winget)]
    [InlineData(@"C:\Program Files\WinGet\Packages\Roost.Roost_Microsoft.Winget.Source_8wekyb3d8bbwe\roost.exe", InstallChannel.Winget)]
    [InlineData(@"C:\Users\ana\AppData\Local\Microsoft\WinGet\Links\roost.exe", InstallChannel.Winget)]
    [InlineData(@"C:\proyectos\roost\src\Roost.Cli\bin\Debug\net10.0\", InstallChannel.Source)]
    [InlineData("/home/ana/roost/src/Roost.Cli/bin/Release/net10.0/", InstallChannel.Source)]
    [InlineData(@"C:\Users\ana\AppData\Local\Microsoft\WinGet\Packages\Otro.Paquete_8wekyb3d8bbwe\", InstallChannel.Manual)]
    [InlineData(@"C:\herramientas\roost\", InstallChannel.Manual)]
    [InlineData(@"C:\apps\roost\", InstallChannel.Manual)]                                  // sin scoop ni current
    [InlineData("/opt/roost/", InstallChannel.Manual)]
    [InlineData("", InstallChannel.Manual)]
    [InlineData(null, InstallChannel.Manual)]
    public void ComoSeInstalo_SaleDeLaRuta(string? path, InstallChannel esperado)
    {
        Assert.Equal(esperado, UpdateInstructions.Detect(path));
    }

    [Fact]
    public void ComoActualizar_SegunElCanal()
    {
        Assert.True(GitHubReleases.TryParse(Release("v0.2.0"), out var release, out _));

        var scoop = UpdateInstructions.For(InstallChannel.Scoop, release, windows: true);
        Assert.Equal("scoop update roost", scoop.Command);
        Assert.Equal(release!.Page, scoop.Url);

        Assert.Equal("winget upgrade Roost.Roost", UpdateInstructions.For(InstallChannel.Winget, release, windows: true).Command);

        var manual = UpdateInstructions.For(InstallChannel.Manual, release, windows: true);
        Assert.Null(manual.Command);
        Assert.Equal("roost-0.2.0-win-x64.zip", manual.Package);
        Assert.Contains("roost-0.2.0-win-x64.zip", manual.Text);
        Assert.Contains("releases/tag/v0.2.0", manual.Text);
        Assert.Equal("roost-0.2.0-linux-x64.tar.gz", UpdateInstructions.For(InstallChannel.Manual, release, windows: false).Package);

        // Sin saber aún cuál es la última: la página de releases, sin paquete.
        var unknown = UpdateInstructions.For(InstallChannel.Manual, null, windows: true);
        Assert.Null(unknown.Url);
        Assert.Null(unknown.Package);
    }

    [Fact]
    public void LaVariableDeEntorno_CambiaLaUrl_SoloSiEsValida()
    {
        try
        {
            Environment.SetEnvironmentVariable(UpdateChecker.UrlVariable, "http://127.0.0.1:8123/latest.json");
            Assert.Equal("http://127.0.0.1:8123/latest.json", UpdateChecker.EndpointFromEnvironment().ToString());
            Environment.SetEnvironmentVariable(UpdateChecker.UrlVariable, "file:///C:/latest.json");
            Assert.Equal(GitHubReleases.LatestUrl, UpdateChecker.EndpointFromEnvironment());
            Environment.SetEnvironmentVariable(UpdateChecker.UrlVariable, null);
            Assert.Equal(GitHubReleases.LatestUrl, UpdateChecker.EndpointFromEnvironment());
        }
        finally
        {
            Environment.SetEnvironmentVariable(UpdateChecker.UrlVariable, null);
        }
    }

    [Fact]
    public void LaVersionInstalada_SinLaMetadataDeBuild()
    {
        Assert.DoesNotContain('+', RoostVersion.Current);
        Assert.True(SemanticVersion.TryParse(RoostVersion.Current, out _), RoostVersion.Current);
    }
}
