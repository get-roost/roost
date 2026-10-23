using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using Roost.Core.Platform;
using Roost.Core.Updates;

namespace Roost.Core.Tests;

/// <summary>
/// Descargar y verificar una Roost nueva SIN red ni instalador: una release falsa (el setup y su
/// <c>SHA256SUMS.txt</c>) servida por un <see cref="HttpMessageHandler"/>, en una caché temporal.
/// Nada ejecuta el «setup», que es un fichero cualquiera.
/// </summary>
public class UpdateInstallerTests : IDisposable
{
    private const string Tag = "v0.2.0";
    private const string SetupName = "roost-0.2.0-win-x64-setup.exe";
    private const string SetupUrl = $"https://github.com/get-roost/roost/releases/download/{Tag}/{SetupName}";
    private const string SumsUrl = $"https://github.com/get-roost/roost/releases/download/{Tag}/SHA256SUMS.txt";

    private readonly string _root;
    private readonly RoostPaths _paths;
    private readonly byte[] _setup;
    private readonly string _setupSha;

    public UpdateInstallerTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "roost-update-installer-" + Guid.NewGuid().ToString("N"));
        _paths = new RoostPaths(_root, Path.Combine(_root, "cache"));
        _setup = new byte[5000];
        new Random(7).NextBytes(_setup);
        _setupSha = Convert.ToHexStringLower(SHA256.HashData(_setup));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private static ReleaseInfo Release(bool withSums = true, bool withSetup = true)
    {
        var assets = new List<string>
        {
            $$"""{ "name": "roost-0.2.0-win-x64.zip", "size": 52428800, "browser_download_url": "https://github.com/get-roost/roost/releases/download/{{Tag}}/roost-0.2.0-win-x64.zip" }""",
            $$"""{ "name": "roost-0.2.0-linux-x64.tar.gz", "size": 31457280, "browser_download_url": "https://github.com/get-roost/roost/releases/download/{{Tag}}/roost-0.2.0-linux-x64.tar.gz" }""",
        };
        if (withSetup) assets.Add($$"""{ "name": "{{SetupName}}", "size": 5000, "browser_download_url": "{{SetupUrl}}" }""");
        if (withSums) assets.Add($$"""{ "name": "SHA256SUMS.txt", "size": 256, "browser_download_url": "{{SumsUrl}}" }""");
        var json = $$"""
            {
              "html_url": "https://github.com/get-roost/roost/releases/tag/{{Tag}}",
              "tag_name": "{{Tag}}",
              "name": "Roost 0.2.0",
              "draft": false,
              "prerelease": false,
              "assets": [ {{string.Join(",", assets)}} ],
              "body": "## Novedades\n\n- Se actualiza desde la app"
            }
            """;
        Assert.True(GitHubReleases.TryParse(json, out var release, out var error), error);
        return release!;
    }

    /// <summary>Un stream que se corta (IOException) después de <paramref name="after"/> bytes: una conexión que se cae.</summary>
    private sealed class CutStream(byte[] data, int after) : Stream
    {
        private readonly MemoryStream _inner = new(data);

        public override int Read(Span<byte> buffer)
        {
            if (_inner.Position >= after) throw new IOException("la conexion se ha cerrado");
            var allowed = (int)Math.Min(buffer.Length, after - _inner.Position);
            return _inner.Read(buffer[..allowed]);
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(Read(buffer.Span));

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            Task.FromResult(Read(buffer.AsSpan(offset, count)));

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => _inner.Length;
        public override long Position { get => _inner.Position; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>Un IProgress que apunta en el acto (Progress&lt;T&gt; lo haría en otro hilo, después).</summary>
    private sealed class SyncProgress(Action<long> report) : IProgress<long>
    {
        public void Report(long value) => report(value);
    }

    /// <summary>GitHub de mentira: sirve el setup (con Range) y el SHA256SUMS.txt.</summary>
    private sealed class FakeGitHub(byte[] setup, string sums) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        /// <summary>En qué petición del setup se corta la conexión, y tras cuántos bytes (null: ninguna).</summary>
        public (int Request, int After)? Cut { get; set; }

        /// <summary>El servidor no sabe reanudar: contesta 200 entero aunque le pidan un Range.</summary>
        public bool IgnoreRange { get; set; }

        private int _setupRequests;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            var url = request.RequestUri!.ToString();
            if (url == SumsUrl)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(sums) });
            if (url != SetupUrl)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));

            _setupRequests++;
            var from = 0;
            var status = HttpStatusCode.OK;
            if (!IgnoreRange && request.Headers.Range?.Ranges.FirstOrDefault() is { From: { } f })
            {
                if (f >= setup.Length) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.RequestedRangeNotSatisfiable));
                from = (int)f;
                status = HttpStatusCode.PartialContent;
            }
            var body = setup[from..];
            Stream stream = Cut is { } cut && cut.Request == _setupRequests ? new CutStream(body, cut.After) : new MemoryStream(body);
            var content = new StreamContent(stream);
            content.Headers.ContentLength = body.Length;
            if (status == HttpStatusCode.PartialContent)
                content.Headers.ContentRange = new ContentRangeHeaderValue(from, setup.Length - 1, setup.Length);
            return Task.FromResult(new HttpResponseMessage(status) { Content = content });
        }
    }

    private UpdateInstaller Installer(HttpMessageHandler github, Func<string, SignatureStatus>? signature = null) =>
        new(_paths, new HttpClient(github), signature ?? (_ => SignatureStatus.Unsigned), retryDelay: TimeSpan.Zero);

    private string Sums(string? setupSha = null) =>
        $"{new string('a', 64)}  roost-0.2.0-linux-x64.tar.gz\n{setupSha ?? _setupSha}  {SetupName}\n{new string('b', 64)}  roost-0.2.0-win-x64.zip\n";

    [Fact]
    public void Sha256Sums_ElFormatoDeSha256sum_ConSusVariantes()
    {
        var text = "\uFEFF" + new string('a', 64) + "  roost-0.2.0-win-x64.zip\r\n"
            + new string('B', 64) + " *roost-0.2.0-win-x64-setup.exe\r\n"
            + new string('c', 64) + "  dist/roost-0.2.0-linux-x64.tar.gz\n"
            + "# comentario\n"
            + "esto no es una linea del formato\n"
            + new string('d', 10) + "  corto.zip\n";

        var sums = Sha256Sums.Parse(text);

        Assert.Equal(3, sums.Count);
        Assert.Equal(new string('a', 64), sums["roost-0.2.0-win-x64.zip"]);
        Assert.Equal(new string('b', 64), sums["ROOST-0.2.0-WIN-X64-SETUP.EXE"]);   // en minúsculas y sin distinguir mayúsculas en el nombre
        Assert.Equal(new string('c', 64), sums["roost-0.2.0-linux-x64.tar.gz"]);
        Assert.Empty(Sha256Sums.Parse(null));
    }

    [Fact]
    public void ElFicheroDeCadaCanal()
    {
        var release = Release();
        Assert.Equal(SetupName, UpdateInstaller.AssetFor(release, InstallChannel.Installer, windows: true)?.Name);
        Assert.Equal("roost-0.2.0-win-x64.zip", UpdateInstaller.AssetFor(release, InstallChannel.Manual, windows: true)?.Name);
        Assert.Equal("roost-0.2.0-linux-x64.tar.gz", UpdateInstaller.AssetFor(release, InstallChannel.Manual, windows: false)?.Name);
        Assert.Null(UpdateInstaller.AssetFor(release, InstallChannel.Scoop, windows: true));
        Assert.Null(UpdateInstaller.AssetFor(release, InstallChannel.Winget, windows: true));
        Assert.Null(UpdateInstaller.AssetFor(release, InstallChannel.Source, windows: true));
        Assert.Null(UpdateInstaller.AssetFor(Release(withSetup: false), InstallChannel.Installer, windows: true));
        Assert.True(UpdateInstaller.InstallsFromApp(InstallChannel.Installer));
        Assert.False(UpdateInstaller.InstallsFromApp(InstallChannel.Manual));
    }

    [Fact]
    public void LaLineaDelSetup_SilenciosaYConLoQueNecesitaParaVolver()
    {
        Assert.Equal("/SILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS", UpdateInstaller.SetupArguments(relaunchApp: false, startStack: false));
        Assert.Equal("/SILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS /ROOSTRELAUNCH=1 /ROOSTSTART=1 /LOG=\"C:\\roost\\logs\\roost-setup.log\"",
            UpdateInstaller.SetupArguments(relaunchApp: true, startStack: true, logFile: @"C:\roost\logs\roost-setup.log"));
        Assert.EndsWith(Path.Combine("logs", "roost-setup.log"), Installer(new FakeGitHub(_setup, Sums())).SetupLogFile);
    }

    [Fact]
    public async Task HashBueno_SeDescargaVerificaYQuedaEnLaCache()
    {
        var github = new FakeGitHub(_setup, Sums());
        var installer = Installer(github);
        var release = Release();
        var seen = new List<long>();

        var download = await installer.DownloadVerifiedAsync(release, release.Installer!, new SyncProgress(seen.Add));

        Assert.Equal(Path.Combine(_paths.Downloads, SetupName), download.Path);
        Assert.Equal(_setup, await File.ReadAllBytesAsync(download.Path));
        Assert.Equal(5000, download.Size);
        Assert.Equal(_setupSha, download.Sha256);
        Assert.Equal(SignatureStatus.Unsigned, download.Signature);
        Assert.False(File.Exists(download.Path + ".part"));
        Assert.Equal([SumsUrl, SetupUrl], github.Requests.Select(r => r.RequestUri!.ToString()));

        // El progreso llega en bytes (el último, el total).
        Assert.Equal(5000, seen.Last());

        // Ya descargado y con el hash bueno: no se vuelve a bajar.
        await installer.DownloadAsync(release.Installer!, _setupSha);
        Assert.Equal(2, github.Requests.Count);
    }

    [Fact]
    public async Task HashMalo_SeRechaza_YNoQuedaNingunFichero()
    {
        var github = new FakeGitHub(_setup, Sums(setupSha: new string('f', 64)));
        var release = Release();

        var ex = await Assert.ThrowsAsync<RoostException>(() => Installer(github).DownloadVerifiedAsync(release, release.Installer!));

        Assert.Contains("SHA-256", ex.Message);
        Assert.Contains("No se ejecuta nada", ex.Message);
        Assert.False(File.Exists(Path.Combine(_paths.Downloads, SetupName)));
        Assert.False(File.Exists(Path.Combine(_paths.Downloads, SetupName + ".part")));
    }

    [Fact]
    public async Task SinSha256Sums_NoSeDescargaNada()
    {
        var github = new FakeGitHub(_setup, Sums());
        var release = Release(withSums: false);

        var ex = await Assert.ThrowsAsync<RoostException>(() => Installer(github).DownloadVerifiedAsync(release, release.Installer!));

        Assert.Contains("SHA256SUMS.txt", ex.Message);
        Assert.Empty(github.Requests);
    }

    [Fact]
    public async Task ElSumsSinElSetup_SeRechaza()
    {
        var github = new FakeGitHub(_setup, $"{new string('a', 64)}  roost-0.2.0-win-x64.zip\n");
        var release = Release();

        var ex = await Assert.ThrowsAsync<RoostException>(() => Installer(github).DownloadVerifiedAsync(release, release.Installer!));

        Assert.Contains(SetupName, ex.Message);
        Assert.Single(github.Requests);   // solo el SHA256SUMS.txt: el setup ni se pide
    }

    [Fact]
    public async Task DescargaCortada_SeReanudaDondeSeQuedo()
    {
        var github = new FakeGitHub(_setup, Sums()) { Cut = (Request: 1, After: 1200) };
        var release = Release();

        var download = await Installer(github).DownloadVerifiedAsync(release, release.Installer!);

        Assert.Equal(_setup, await File.ReadAllBytesAsync(download.Path));
        var setupRequests = github.Requests.Where(r => r.RequestUri!.ToString() == SetupUrl).ToList();
        Assert.Equal(2, setupRequests.Count);
        Assert.Null(setupRequests[0].Headers.Range);
        Assert.Equal(1200, setupRequests[1].Headers.Range?.Ranges.Single().From);   // lo que ya tenía
    }

    [Fact]
    public async Task ServidorQueNoReanuda_EmpiezaDeCero_SinDuplicarBytes()
    {
        var github = new FakeGitHub(_setup, Sums()) { Cut = (Request: 1, After: 1200), IgnoreRange = true };
        var release = Release();

        var download = await Installer(github).DownloadVerifiedAsync(release, release.Installer!);

        Assert.Equal(_setup, await File.ReadAllBytesAsync(download.Path));
        Assert.Equal(5000, download.Size);
    }

    [Fact]
    public async Task TresCortesSeguidos_FallaConMotivo_YDejaElPartParaLuego()
    {
        var cutting = new CuttingGitHub(_setup, Sums());
        var release = Release();

        var ex = await Assert.ThrowsAsync<RoostException>(() => Installer(cutting).DownloadVerifiedAsync(release, release.Installer!));

        Assert.Contains("se reanuda donde se quedo", ex.Message);
        Assert.Equal(UpdateInstaller.Attempts, cutting.SetupRequests);
        Assert.True(File.Exists(Path.Combine(_paths.Downloads, SetupName + ".part")));
        Assert.False(File.Exists(Path.Combine(_paths.Downloads, SetupName)));
    }

    /// <summary>Un servidor que corta todas las descargas del setup a los 100 bytes.</summary>
    private sealed class CuttingGitHub(byte[] setup, string sums) : HttpMessageHandler
    {
        public int SetupRequests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.ToString() == SumsUrl)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(sums) });
            SetupRequests++;
            var from = (int)(request.Headers.Range?.Ranges.FirstOrDefault()?.From ?? 0);
            var content = new StreamContent(new CutStream(setup[from..], 100));
            content.Headers.ContentLength = setup.Length - from;
            return Task.FromResult(new HttpResponseMessage(from > 0 ? HttpStatusCode.PartialContent : HttpStatusCode.OK) { Content = content });
        }
    }

    [Fact]
    public async Task FirmaInvalida_SeRechaza_YSeBorraElFichero()
    {
        var github = new FakeGitHub(_setup, Sums());
        var release = Release();

        var ex = await Assert.ThrowsAsync<RoostException>(() =>
            Installer(github, _ => SignatureStatus.Invalid).DownloadVerifiedAsync(release, release.Installer!));

        Assert.Contains("firma", ex.Message);
        Assert.Contains("No se ejecuta nada", ex.Message);
        Assert.False(File.Exists(Path.Combine(_paths.Downloads, SetupName)));
    }

    [Fact]
    public async Task FirmaValida_SeAcepta_YSeCuenta()
    {
        var github = new FakeGitHub(_setup, Sums());
        var release = Release();

        var download = await Installer(github, _ => SignatureStatus.Valid).DownloadVerifiedAsync(release, release.Installer!);

        Assert.Equal(SignatureStatus.Valid, download.Signature);
    }

    [Fact]
    public void Authenticode_QueSignificaLoQueDevuelveWindows()
    {
        Assert.Equal(SignatureStatus.Valid, Authenticode.Interpret(0, 0));
        Assert.Equal(SignatureStatus.Unsigned, Authenticode.Interpret(unchecked((int)0x800B0100), unchecked((int)0x800B0100)));   // TRUST_E_NOSIGNATURE
        Assert.Equal(SignatureStatus.Unsigned, Authenticode.Interpret(unchecked((int)0x800B0003), 0));                           // un txt: formato desconocido
        Assert.Equal(SignatureStatus.Invalid, Authenticode.Interpret(unchecked((int)0x800B0100), unchecked((int)0x80096010)));   // «sin firma» por un hash que no cuadra
        Assert.Equal(SignatureStatus.Invalid, Authenticode.Interpret(unchecked((int)0x800B0109), 0));                            // CERT_E_UNTRUSTEDROOT
        Assert.Equal(SignatureStatus.Invalid, Authenticode.Interpret(unchecked((int)0x800B0111), 0));                            // TRUST_E_EXPLICIT_DISTRUST
        Assert.Equal(SignatureStatus.NotChecked, Authenticode.Interpret(unchecked((int)0x80092003), 0));                         // CRYPT_E_FILE_ERROR: ni se pudo leer
    }

    [Fact]
    public void Authenticode_EnEstaMaquina()
    {
        var text = Path.Combine(_root, "setup.txt");
        Directory.CreateDirectory(_root);
        File.WriteAllText(text, "esto no es un ejecutable");

        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(SignatureStatus.NotChecked, Authenticode.Check(text));
            return;
        }
        Assert.Equal(SignatureStatus.Unsigned, Authenticode.Check(text));
        // Un binario de Windows va firmado por Microsoft: la cadena entera tiene que valer.
        Assert.Equal(SignatureStatus.Valid, Authenticode.Check(Path.Combine(Environment.SystemDirectory, "kernel32.dll")));
        Assert.Equal(SignatureStatus.NotChecked, Authenticode.Check(Path.Combine(_root, "no-existe.exe")));   // CRYPT_E_FILE_ERROR
    }

    [Fact]
    public void ElVigilante_EsperaAlSetup_YRelanzaLaAppSiFallo()
    {
        var script = UpdateLauncher.WatchdogScript(4242, @"C:\Program Files\Roost's\roost-desktop.exe");

        Assert.Contains("GetProcessById(4242)", script);
        Assert.Contains("$p.Handle", script);          // sin el handle, PowerShell 5.1 no sabe el código de salida
        Assert.Contains("WaitForExit()", script);
        Assert.Contains(@"'C:\Program Files\Roost''s\roost-desktop.exe'", script);
        Assert.Contains("'--update-failed=' + $c", script);
        Assert.Contains("-ne 0", script);

        Assert.Equal(2, UpdateLauncher.ParseFailedArgument("--update-failed=2"));
        Assert.Null(UpdateLauncher.ParseFailedArgument("--update-failed=dos"));
        Assert.Null(UpdateLauncher.ParseFailedArgument("--updated"));
        Assert.Contains("Windows no dio permiso", UpdateLauncher.DescribeSetupExit(2));
        Assert.Contains("codigo 9", UpdateLauncher.DescribeSetupExit(9));
    }

    [Fact]
    public void ElMarcador_IdaYVuelta_YQuePasoSegunLaVersionQueCorre()
    {
        Assert.Null(UpdateMarker.TryRead(_paths));
        var marker = new UpdateMarker("0.1.2", "0.2.0", StackWasRunning: true, new DateTimeOffset(2026, 11, 3, 9, 0, 0, TimeSpan.Zero), @"C:\roost\logs\roost-setup.log");

        UpdateMarker.Write(_paths, marker);
        var read = UpdateMarker.TryRead(_paths);

        Assert.Equal(marker, read);
        Assert.Equal(UpdateOutcome.Updated, read!.Outcome("0.2.0"));
        Assert.Equal(UpdateOutcome.Updated, read.Outcome("0.2.1"));
        Assert.Equal(UpdateOutcome.NotUpdated, read.Outcome("0.1.2"));
        Assert.Equal(UpdateOutcome.NotUpdated, read.Outcome("dev"));

        UpdateMarker.Delete(_paths);
        Assert.Null(UpdateMarker.TryRead(_paths));
        UpdateMarker.Delete(_paths);   // sin fichero tampoco lanza

        File.WriteAllText(UpdateMarker.FilePath(_paths), "{ roto");
        Assert.Null(UpdateMarker.TryRead(_paths));
    }

    [Fact]
    public void LasNotas_EnBloquesLegibles()
    {
        var blocks = ReleaseNotes.Parse("""
            ## Novedades

            - **nginx 1.30.7**, con el arreglo de [CVE-2026-1234](https://nginx.org/x).
              - detalle anidado
            * `roost update --install`
            1. primero
            ---
            Un párrafo que sigue
            en dos líneas.

            ```sh
            roost update --install
            ```
            <!-- interno -->
            <details>
            ![captura](https://x/y.png)
            **Full Changelog**: https://github.com/get-roost/roost/compare/v0.1.2...v0.2.0
            """);

        Assert.Equal(
        [
            new NotesBlock(NotesBlockKind.Heading, "Novedades", 2),
            new NotesBlock(NotesBlockKind.Bullet, "nginx 1.30.7, con el arreglo de CVE-2026-1234."),
            new NotesBlock(NotesBlockKind.Bullet, "detalle anidado", 1),
            new NotesBlock(NotesBlockKind.Bullet, "roost update --install"),
            new NotesBlock(NotesBlockKind.Bullet, "primero"),
            new NotesBlock(NotesBlockKind.Paragraph, "Un párrafo que sigue en dos líneas."),
            new NotesBlock(NotesBlockKind.Code, "roost update --install"),
            new NotesBlock(NotesBlockKind.Paragraph, "captura Full Changelog: https://github.com/get-roost/roost/compare/v0.1.2...v0.2.0"),
        ], blocks);
        Assert.Empty(ReleaseNotes.Parse(null));
        Assert.Empty(ReleaseNotes.Parse("  \r\n"));
    }

    [Fact]
    public void LasNotas_DeLaRelease_SoloHastaLaMarcaDelWorkflow_YSinTablas()
    {
        // Lo que escribe release.yml: la sección del CHANGELOG, la marca y, detrás, descargas y firma.
        var notes = $"""
            ## Novedades

            - **Actualizar desde la app**, con el SHA-256 verificado.
            - El VC++ en el instalador.

            {ReleaseNotes.EndMarker}

            ## Descargas

            | Fichero | Qué lleva |
            |---|---|
            | roost-0.2.0-win-x64-setup.exe | el instalador |

            ## Firma y procedencia

            Esta versión no va firmada.
            """;

        Assert.Equal(
        [
            new NotesBlock(NotesBlockKind.Heading, "Novedades", 2),
            new NotesBlock(NotesBlockKind.Bullet, "Actualizar desde la app, con el SHA-256 verificado."),
            new NotesBlock(NotesBlockKind.Bullet, "El VC++ en el instalador."),
        ], ReleaseNotes.Parse(notes));
        Assert.Equal(["- Actualizar desde la app, con el SHA-256 verificado.", "- El VC++ en el instalador."], GitHubReleases.Summarize(notes));

        // Sin la marca (las releases de antes), se lee todo, pero las tablas no.
        var old = ReleaseNotes.Parse("## Descargas\n\n| Fichero | Qué lleva |\n|---|---|\n| a | b |\n\nTexto.");
        Assert.Equal([new NotesBlock(NotesBlockKind.Heading, "Descargas", 2), new NotesBlock(NotesBlockKind.Paragraph, "Texto.")], old);
        Assert.Equal(["Texto."], GitHubReleases.Summarize("## Descargas\n\n| a | b |\n|---|---|\n\nTexto."));
    }

    [Fact]
    public async Task AlAcabarDeBajar_AvisaAntesDeVerificar_YLaFirmaSeCuentaSinTildes()
    {
        var github = new FakeGitHub(_setup, Sums());
        var installer = Installer(github);
        var release = Release();
        var seen = new List<long>();
        var verifyingAt = -1L;

        await installer.DownloadVerifiedAsync(release, release.Installer!, new SyncProgress(seen.Add), verifying: () => verifyingAt = seen.LastOrDefault());
        Assert.Equal(5000, verifyingAt);   // se avisa con todo bajado, antes del hash

        // Ya en la caché: también se avisa (se vuelve a calcular su hash).
        verifyingAt = -1;
        await installer.DownloadAsync(release.Installer!, _setupSha, new SyncProgress(seen.Add), verifying: () => verifyingAt = seen.Last());
        Assert.Equal(5000, verifyingAt);

        Assert.Equal("firma digital valida", UpdateInstaller.DescribeSignature(SignatureStatus.Valid));
        Assert.StartsWith("sin firma digital", UpdateInstaller.DescribeSignature(SignatureStatus.Unsigned));
        Assert.Equal("firma no comprobada", UpdateInstaller.DescribeSignature(SignatureStatus.NotChecked));
    }

    [Fact]
    public void VcRedist_SegunLoQueDiceElRegistro()
    {
        Assert.Equal(new VcRedistStatus(false, null), VcRedist.Evaluate(null, null, null, null));
        Assert.Equal(new VcRedistStatus(false, null), VcRedist.Evaluate(0, 14, 50, 35719));
        Assert.Equal(new VcRedistStatus(true, new Version(14, 50, 35719)), VcRedist.Evaluate(1, 14, 50, 35719));
        Assert.Equal(new VcRedistStatus(true, new Version(14, 29)), VcRedist.Evaluate(1, 14, 29, null));
        Assert.Equal(new VcRedistStatus(true, null), VcRedist.Evaluate(1, null, null, null));

        var old = VcRedist.Evaluate(1, 14, 29, 30133);
        Assert.False(old.SatisfiesApache);
        Assert.True(VcRedist.Evaluate(1, 14, 50, 0).SatisfiesApache);
        Assert.Null(VcRedist.Warning(old, forApache: false));
        Assert.Contains("14.50", VcRedist.Warning(old, forApache: true));
        Assert.Contains("aka.ms/vs/17/release/vc_redist.x64.exe", VcRedist.Warning(new VcRedistStatus(false, null), forApache: false));
        Assert.Null(VcRedist.Warning(new VcRedistStatus(false, null, Checked: false), forApache: true));   // no se sabe: no se avisa
        Assert.Equal("no instalado", new VcRedistStatus(false, null).Describe());

        // En esta máquina solo se comprueba que leer el registro no lanza.
        var detected = VcRedist.Detect();
        if (!OperatingSystem.IsWindows()) Assert.False(detected.Checked);
    }

    [Fact]
    public void ElShim_EstaViejo_SiNoEsElDeLaApp()
    {
        var app = Path.Combine(_root, "app");
        Directory.CreateDirectory(app);
        var shims = new Roost.Core.Shims.ShimInstaller(_paths);
        var exe = OperatingSystem.IsWindows() ? ".exe" : "";
        Assert.False(shims.IsOutdated(app));   // sin shims puestos no hay nada que renovar

        Directory.CreateDirectory(shims.Directory);
        File.WriteAllText(Path.Combine(shims.Directory, "php" + exe), "shim 0.1.2");
        Assert.False(shims.IsOutdated(app));   // sin el shim de origen tampoco

        File.WriteAllText(Path.Combine(app, "roost-shim" + exe), "shim 0.1.2");
        Assert.False(shims.IsOutdated(app));
        File.WriteAllText(Path.Combine(app, "roost-shim" + exe), "shim 0.2.0");
        Assert.True(shims.IsOutdated(app));
    }
}
