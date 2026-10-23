using System.Net;
using System.Security.Cryptography;
using System.Text;
using Roost.Core.Audit;
using Roost.Core.Platform;

namespace Roost.Core.Tests;

/// <summary>
/// El cliente del feed firmado, con un servidor falso y una clave de prueba (nunca la de verdad):
/// qué guarda, qué rechaza y qué se evalúa sin red.
/// </summary>
public class AdvisoryFeedTests : IDisposable
{
    private const string Url = "https://feed.test/advisories.json";
    private static readonly DateTimeOffset Now = new(2026, 10, 23, 8, 0, 0, TimeSpan.Zero);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "roost-feed-" + Guid.NewGuid().ToString("N"));
    private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly FakeServer _server = new();
    private DateTimeOffset _now = Now;

    private string[] Keys => [FeedSignature.PublicKeyOf(_key)];

    private AdvisoryFeed Feed() => new(new RoostPaths(_root, Path.Combine(_root, "cache")), new HttpClient(_server), Url, Keys, () => _now);

    private static byte[] Json(DateTimeOffset generated, string schema = "1.0.0") => Encoding.UTF8.GetBytes("""
        {"schemaVersion":"@SCHEMA@","generated":"@GENERATED@","source":"prueba",
         "products":{"php":{"displayName":"PHP","branches":{"8.3":{"latest":"8.3.36","eol":"2027-12-31","status":"security"}}}},
         "advisories":[{"id":"CVE-2026-109619","product":"php","affected":[">=8.3.0 <8.3.36"],"severity":"critical"}]}
        """.Replace("@SCHEMA@", schema).Replace("@GENERATED@", generated.ToString("O")));

    private void Serve(byte[] json, ECDsa? signer = null, string etag = "\"v1\"")
    {
        _server.Files[Url] = (json, etag);
        _server.Files[Url + ".sig"] = (FeedSignature.Sign(json, signer ?? _key), null);
    }

    private static AuditDataset Embedded(DateTimeOffset generated) => new() { SchemaVersion = "0.1.0", Generated = generated };

    [Fact]
    public void Firma_DeIdaYVuelta_YConOtraClaveOTocadaNoVale()
    {
        var data = Json(Now);
        var signature = FeedSignature.Sign(data, _key);

        Assert.True(FeedSignature.Verify(data, signature, Keys));
        using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        Assert.False(FeedSignature.Verify(data, signature, [FeedSignature.PublicKeyOf(other)]));
        data[10] ^= 1;
        Assert.False(FeedSignature.Verify(data, signature, Keys));
        Assert.False(FeedSignature.Verify(data, signature, ["esto no es una clave"]));
        Assert.StartsWith("-----BEGIN PUBLIC KEY-----", FeedSignature.ToPem(Keys[0]));
    }

    [Fact]
    public void LaClaveDeConfianza_EsUnaP256QueSeLee()
    {
        var key = Assert.Single(FeedSignature.TrustedKeys);
        using var ecdsa = ECDsa.Create();
        ecdsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(key), out _);
        Assert.Equal(256, ecdsa.KeySize);
    }

    [Fact]
    public async Task Actualizar_VerificaYGuarda_YDesdeAhiSeEvaluaConElFeed()
    {
        Serve(Json(Now));
        var feed = Feed();

        Assert.Equal(FeedUpdateStatus.Updated, (await feed.UpdateAsync()).Status);
        var (dataset, origin) = feed.LoadBest(Embedded(Now.AddDays(-1)));

        Assert.Equal(DatasetKind.Feed, origin.Kind);
        Assert.Null(origin.Note);
        Assert.Equal(Now, dataset.Generated);
        Assert.Equal("critical", Assert.Single(dataset.Advisories).Severity);
        Assert.Equal("feed firmado del 2026-10-23 08:00 UTC (hace 0 dias)", origin.Describe(dataset, Now));
    }

    [Fact]
    public async Task ConLaCacheDeHoy_NoPregunta_YForzandoUsaElETag()
    {
        Serve(Json(Now));
        var feed = Feed();
        await feed.UpdateAsync();
        _server.Requests.Clear();

        Assert.Equal(FeedUpdateStatus.Fresh, (await feed.UpdateAsync()).Status);
        Assert.Empty(_server.Requests);

        _now = Now.AddHours(25);
        Assert.Equal(FeedUpdateStatus.NotModified, (await feed.UpdateAsync()).Status);
        Assert.Equal("\"v1\"", Assert.Single(_server.Requests).IfNoneMatch);
        Assert.Equal(Now.AddHours(25), feed.LastChecked);
    }

    [Fact]
    public async Task UnaFirmaDeOtraClave_NoSeGuarda()
    {
        using var attacker = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        Serve(Json(Now), attacker);
        var feed = Feed();

        var update = await feed.UpdateAsync();

        Assert.Equal(FeedUpdateStatus.Failed, update.Status);
        Assert.Contains("firma", update.Detail);
        Assert.False(File.Exists(feed.FeedFile));
        var (_, origin) = feed.LoadBest(Embedded(Now.AddDays(-1)));
        Assert.Equal(DatasetKind.Embedded, origin.Kind);
    }

    [Fact]
    public async Task UnFeedMasViejoQueElGuardado_SeRechaza()
    {
        Serve(Json(Now));
        var feed = Feed();
        await feed.UpdateAsync();

        Serve(Json(Now.AddDays(-30)), etag: "\"v0\"");   // bien firmado, pero antiguo: alguien lo repite
        var update = await feed.UpdateAsync(force: true);

        Assert.Equal(FeedUpdateStatus.Failed, update.Status);
        Assert.Contains("mas viejo", update.Detail);
        Assert.Equal(Now, feed.LoadCached(out _)!.Generated);
    }

    [Fact]
    public async Task UnEsquemaMasNuevo_SeIgnora()
    {
        Serve(Json(Now, schema: "2.0.0"));
        var update = await Feed().UpdateAsync();

        Assert.Equal(FeedUpdateStatus.Failed, update.Status);
        Assert.Contains("actualiza Roost", update.Detail);
    }

    [Fact]
    public async Task SiElEmbebidoEsMasNuevo_GanaElEmbebido()
    {
        Serve(Json(Now));
        var feed = Feed();
        await feed.UpdateAsync();

        var (dataset, origin) = feed.LoadBest(Embedded(Now.AddDays(2)));   // un Roost recién actualizado

        Assert.Equal(DatasetKind.Embedded, origin.Kind);
        Assert.Equal(Now.AddDays(2), dataset.Generated);
    }

    [Fact]
    public async Task LaCacheTocada_NoSeUsa()
    {
        Serve(Json(Now));
        var feed = Feed();
        await feed.UpdateAsync();
        var bytes = File.ReadAllBytes(feed.FeedFile);
        File.WriteAllBytes(feed.FeedFile, [.. bytes.Take(bytes.Length - 2), (byte)' ', (byte)'}']);

        Assert.Null(feed.LoadCached(out var problem));
        Assert.Contains("firma", problem);
    }

    [Fact]
    public async Task SinRed_NoLanza_YUnFeedViejoSeUsaAvisando()
    {
        Serve(Json(Now));
        var feed = Feed();
        await feed.UpdateAsync();

        _server.Down = true;
        _now = Now.AddDays(10);
        var update = await feed.UpdateAsync();
        Assert.Equal(FeedUpdateStatus.Failed, update.Status);
        Assert.StartsWith("no se pudo bajar el feed", update.Detail);

        var (dataset, origin) = feed.LoadBest(Embedded(Now.AddDays(-1)));
        Assert.Equal(DatasetKind.Feed, origin.Kind);
        Assert.Contains("hace 10 dias", origin.Describe(dataset, _now));
        Assert.Contains("es viejo", origin.Note);
    }

    [Fact]
    public async Task UnServidorQueNoTieneFeed_NoLanza()
    {
        var update = await Feed().UpdateAsync();

        Assert.Equal(FeedUpdateStatus.Failed, update.Status);
        Assert.Contains("404", update.Detail);
    }

    public void Dispose()
    {
        _key.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private sealed class FakeServer : HttpMessageHandler
    {
        public Dictionary<string, (byte[] Body, string? ETag)> Files { get; } = new();
        public List<(string Url, string? IfNoneMatch)> Requests { get; } = new();
        public bool Down { get; set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (Down) throw new HttpRequestException("sin red");
            var url = request.RequestUri!.ToString();
            var ifNoneMatch = request.Headers.TryGetValues("If-None-Match", out var v) ? v.Single() : null;
            Requests.Add((url, ifNoneMatch));
            if (!Files.TryGetValue(url, out var file))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            if (ifNoneMatch is not null && ifNoneMatch == file.ETag)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotModified));
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(file.Body) };
            if (file.ETag is not null) response.Headers.ETag = System.Net.Http.Headers.EntityTagHeaderValue.Parse(file.ETag);
            return Task.FromResult(response);
        }
    }
}
