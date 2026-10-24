using Roost.Core.Audit;
using Roost.Core.Models;

namespace Roost.Catalog.Tests;

/// <summary>
/// K2: los boletines de nginx.org y httpd.apache.org, sin red: trozos del HTML real de las dos
/// páginas (de octubre de 2026), y lo que sale en el feed con las reglas de siempre.
/// </summary>
public class WebAdvisoriesTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 24, 7, 17, 0, TimeSpan.Zero);

    // De nginx.org/en/security_advisories.html, tal cual (con lo que rodea a la lista).
    private const string NginxPage = """
        <div id="content"><h2>nginx security advisories</h2><p>
        All nginx security issues should be reported via one of the methods listed
        <a href="https://github.com/nginx/nginx/blob/master/SECURITY.md">here</a>.
        </p><ul>

        <li><p>Buffer overflow when using alias with rewrite<br>Severity: medium<br><a href="https://my.f5.com/manage/s/article/K000162416">Advisory</a><br><a href="https://www.cve.org/CVERecord?id=CVE-2026-15176">CVE-2026-15176</a><br>Not vulnerable: 1.31.7+, 1.30.6+<br>Vulnerable: 0.1.29-1.31.6</p></li>

        <li><p>Buffer overflow in script evaluation<br>Severity: medium<br><a href="https://my.f5.com/manage/s/article/K000162607">Advisory</a><br><a href="https://www.cve.org/CVERecord?id=CVE-2026-93479">CVE-2026-93479</a><br>Not vulnerable: 1.31.7+, 1.30.6+<br>Vulnerable: 1.31.2-1.31.6, 1.30.0-1.30.5</p></li>

        <li><p>Buffer overflow when using map and regex<br>Severity: <b>major</b><br><a href="https://my.f5.com/manage/s/article/K000162097">Advisory</a><br><a href="https://www.cve.org/CVERecord?id=CVE-2026-42533">CVE-2026-42533</a><br>Not vulnerable: 1.31.3+, 1.30.4+<br>Vulnerable: 0.9.6-1.31.2</p></li>

        <li><p>Use-after-free in HTTP/3<br>Severity: <b>major</b><br><a href="https://my.f5.com/manage/s/article/K000161616">Advisory</a><br><a href="https://www.cve.org/CVERecord?id=CVE-2026-42530">CVE-2026-42530</a><br>Not vulnerable: 1.31.2+<br>Vulnerable: 1.31.0-1.31.1</p></li>

        <li><p>Memory disclosure in HTTP/3<br>Severity: medium<br><a href="https://my.f5.com/manage/s/article/K000139611">Advisory</a><br><a href="https://www.cve.org/CVERecord?id=CVE-2024-34161">CVE-2024-34161</a><br>Not vulnerable: 1.27.0+, 1.26.1+<br>Vulnerable: 1.25.0-1.25.5, 1.26.0<br>The patch  <a href="/download/patch.2024.mp4.txt">pgp</a></p></li>

        <li><p>Vulnerabilities with Windows directory aliases<br>Severity: medium<br><a href="http://mailman.nginx.org/pipermail/nginx-announce/2012/000086.html">Advisory</a><br><a href="https://www.cve.org/CVERecord?id=CVE-2011-4963">CVE-2011-4963</a><br>Not vulnerable: 1.3.1+, 1.2.1+<br>Vulnerable: nginx/Windows 0.7.52-1.3.0</p></li>

        <li><p>Invalid UTF-8 sequence in the log<br>Severity: none<br><a href="https://www.cve.org/CVERecord?id=CVE-2009-4487">CVE-2009-4487</a><br>Not vulnerable: none<br>Vulnerable: all</p></li>

        </ul></div>
        """;

    // De httpd.apache.org/security/vulnerabilities_24.html: tres secciones con las formas de «Affects».
    private const string ApachePage = """
        <h1 id="2.4.69">Fixed in Apache HTTP Server 2.4.69</h1>
        <p class="cve-summary">Released <time datetime="2026-10-01">2026-10-01</time> &middot; <strong>20 CVEs</strong></p>
        <dl class="cve-list">
        <dt><h3 id="CVE-2026-42528"><a href="https://www.cve.org/CVERecord?id=CVE-2026-42528">CVE-2026-42528</a> (moderate): <name name="CVE-2026-42528">mod_dav shared lock overflow</name>
        <a class="headerlink" href="#CVE-2026-42528" title="Permanent link">&para;</a></h3></dt>
        <dd><p>A memory calculation bug in mod_dav in Apache httpd 2.4.67 and earlier.</p>
        <table class="table"><tr><td class="cve-header">Report received</td><td class="cve-value">2026-04-27</td></tr>
        <tr><td class="cve-header">Update 2.4.69 released</td><td class="cve-value">2026-10-01</td></tr>
        <tr><td class="cve-header">Affects</td><td class="cve-value"> through 2.4.68</td></tr>
        </table></dd>
        <dt><h3 id="CVE-2026-57941"><a href="https://www.cve.org/CVERecord?id=CVE-2026-57941">CVE-2026-57941</a> (important): <name name="CVE-2026-57941">Apache HTTP Server: mod_http2 use-after-free / wild write via shared session-&gt;bbtmp re-entrancy</name>
        <a class="headerlink" href="#CVE-2026-57941" title="Permanent link">&para;</a></h3></dt>
        <dd><table class="table"><tr><td class="cve-header">Affects</td><td class="cve-value">2.4.17 through 2.4.68</td></tr>
        </table></dd>
        </dl>
        <h1 id="2.4.51">Fixed in Apache HTTP Server 2.4.51</h1>
        <p class="cve-summary">Released <time datetime="2021-10-07">2021-10-07</time></p>
        <dl class="cve-list">
        <dt><h3 id="CVE-2021-42013"><a href="https://www.cve.org/CVERecord?id=CVE-2021-42013">CVE-2021-42013</a> (critical): <name name="CVE-2021-42013">Path Traversal and Remote Code Execution in Apache HTTP Server 2.4.49 and 2.4.50 (incomplete fix of CVE-2021-41773)</name></h3></dt>
        <dd><table class="table"><tr><td class="cve-header">Affects</td><td class="cve-value">2.4.50, 2.4.49</td></tr></table></dd>
        </dl>
        <h1 id="2.4.50">Fixed in Apache HTTP Server 2.4.50</h1>
        <p class="cve-summary">Released <time datetime="2021-10-04">2021-10-04</time></p>
        <dl class="cve-list">
        <dt><h3 id="CVE-2021-41773"><a href="https://www.cve.org/CVERecord?id=CVE-2021-41773">CVE-2021-41773</a> (important): <name name="CVE-2021-41773">Path traversal and file disclosure vulnerability</name></h3></dt>
        <dd><table class="table"><tr><td class="cve-header">Affects</td><td class="cve-value">2.4.49</td></tr></table></dd>
        <dt><h3 id="CVE-2021-41524"><a href="https://www.cve.org/CVERecord?id=CVE-2021-41524">CVE-2021-41524</a> (moderate): <name name="CVE-2021-41524">null pointer dereference in h2 fuzzing</name></h3></dt>
        <dd><table class="table"><tr><td class="cve-header">Affects</td><td class="cve-value">algo raro</td></tr></table></dd>
        </dl>
        """;

    private static bool Hit(Advisory a, string version) =>
        ComponentVersion.TryParse(version, out var v) && a.Affected.Any(r => VersionRange.Satisfies(v, r));

    // ---- nginx ---------------------------------------------------------------------------------

    [Fact]
    public void Nginx_AloVulnerableSeLeQuitaCadaRamaCorregida()
    {
        var skipped = new List<string>();
        var list = Sources.ParseNginxAdvisories(NginxPage, skipped).Select(s => s.Advisory).ToList();

        var alias = list.Single(a => a.Id == "CVE-2026-15176");
        Assert.Equal("nginx", alias.Product);
        Assert.Equal("Buffer overflow when using alias with rewrite", alias.Title);
        Assert.Equal("medium", alias.Severity);
        Assert.Equal([">=0.1.29 <1.30.6", ">=1.31.0 <=1.31.6"], alias.Affected);
        Assert.Equal(new Dictionary<string, string> { ["1.31"] = "1.31.7", ["1.30"] = "1.30.6" }, alias.FixedIn);
        Assert.Equal(["https://my.f5.com/manage/s/article/K000162416", "https://www.cve.org/CVERecord?id=CVE-2026-15176"], alias.Refs);

        Assert.True(Hit(alias, "1.30.5"));
        Assert.False(Hit(alias, "1.30.6"));    // con el tramo entero «0.1.29-1.31.6», la 1.30.6 corregida saldría en rojo
        Assert.True(Hit(alias, "1.29.8"));     // una rama EOL no tiene arreglo
        Assert.True(Hit(alias, "1.31.6"));
        Assert.False(Hit(alias, "1.31.8"));
        Assert.False(Hit(alias, "0.1.28"));
    }

    [Fact]
    public void Nginx_TramosVariosVersionesSueltasYGravedad()
    {
        var list = Sources.ParseNginxAdvisories(NginxPage, []).Select(s => s.Advisory).ToList();

        Assert.Equal([">=1.31.2 <=1.31.6", ">=1.30.0 <=1.30.5"], list.Single(a => a.Id == "CVE-2026-93479").Affected);
        Assert.Equal("high", list.Single(a => a.Id == "CVE-2026-42533").Severity);   // «major»
        Assert.Equal([">=1.31.0 <=1.31.1"], list.Single(a => a.Id == "CVE-2026-42530").Affected);   // solo la mainline
        Assert.Equal([">=1.25.0 <=1.25.5", "=1.26.0"], list.Single(a => a.Id == "CVE-2024-34161").Affected);
        var windows = list.Single(a => a.Id == "CVE-2011-4963");
        Assert.EndsWith("(solo nginx para Windows)", windows.Title);
        Assert.True(Hit(windows, "1.0.5"));
        Assert.DoesNotContain(windows.Refs, r => r.StartsWith("http://", StringComparison.Ordinal));
    }

    [Fact]
    public void Nginx_LoQueNoSeLee_NoSePublica_YSeDice()
    {
        var skipped = new List<string>();
        var list = Sources.ParseNginxAdvisories(NginxPage, skipped);

        Assert.DoesNotContain(list, s => s.Advisory.Id == "CVE-2009-4487");   // «Vulnerable: all»
        Assert.Contains("CVE-2009-4487", Assert.Single(skipped));
        Assert.Null(Sources.NginxRanges("1.31.0-1.31.1", "1.31.0+", new Dictionary<string, string>(), out _));   // se contradice
        Assert.Null(Sources.NginxRanges("1.25.x", "1.26.0+", new Dictionary<string, string>(), out _));
    }

    // ---- Apache --------------------------------------------------------------------------------

    [Theory]
    [InlineData(" through 2.4.68", "2.4.0")]
    [InlineData("2.4.17 through 2.4.68", "2.4.17")]
    [InlineData("2.4 through 2.4.63", "2.4.0")]
    [InlineData("<=2.4.53", "2.4.0")]
    [InlineData("<=2.4.48, !<2.4.17", "2.4.17")]
    [InlineData(">=2.4.7, <=2.4.51", "2.4.7")]
    [InlineData(" before 2.4.66", "2.4.0")]
    [InlineData("2.4.30 before 2.4.66", "2.4.30")]
    [InlineData("2.4.68", "2.4.68")]
    [InlineData("2.4.50, 2.4.49", "2.4.49")]
    [InlineData("2.4.2, 2.4.1, 2.2.22, 2.2.21", "2.4.1")]   // la página es la de la 2.4: lo de la 2.2 no cuenta
    public void Apache_LaPrimeraAfectadaSaleDeAffects(string affects, string lower)
        => Assert.Equal(lower, Sources.ApacheLowerBound(affects)?.ToString());

    [Theory]
    [InlineData("algo raro")]
    [InlineData("2.2.34, 2.2.32")]
    [InlineData("")]
    public void Apache_UnAffectsQueNoSeEntiende_NoDaCota(string affects)
        => Assert.Null(Sources.ApacheLowerBound(affects));

    [Fact]
    public void Apache_LaCotaDeArribaEsLaVersionQueLoCorrige()
    {
        var skipped = new List<string>();
        var list = Sources.ParseApacheAdvisories(ApachePage, skipped);

        var dav = list.Single(s => s.Advisory.Id == "CVE-2026-42528");
        Assert.Equal(new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero), dav.Published);
        Assert.Equal("apache", dav.Advisory.Product);
        Assert.Equal([">=2.4.0 <2.4.69"], dav.Advisory.Affected);
        Assert.Equal("2.4.69", dav.Advisory.FixedIn["2.4"]);
        Assert.Equal("medium", dav.Advisory.Severity);   // «moderate»
        Assert.Contains("https://httpd.apache.org/security/vulnerabilities_24.html#CVE-2026-42528", dav.Advisory.Refs);

        var h2 = list.Single(s => s.Advisory.Id == "CVE-2026-57941").Advisory;
        Assert.Equal("mod_http2 use-after-free / wild write via shared session->bbtmp re-entrancy", h2.Title);   // sin «Apache HTTP Server: »
        Assert.Equal("high", h2.Severity);   // «important»
        Assert.Equal([">=2.4.17 <2.4.69"], h2.Affected);

        Assert.Equal([">=2.4.49 <2.4.51"], list.Single(s => s.Advisory.Id == "CVE-2021-42013").Advisory.Affected);
        Assert.Equal("critical", list.Single(s => s.Advisory.Id == "CVE-2021-42013").Advisory.Severity);
        Assert.Equal([">=2.4.49 <2.4.50"], list.Single(s => s.Advisory.Id == "CVE-2021-41773").Advisory.Affected);
        Assert.DoesNotContain(list, s => s.Advisory.Id == "CVE-2021-41524");
        Assert.Contains("CVE-2021-41524", Assert.Single(skipped));
    }

    // ---- En el feed ----------------------------------------------------------------------------

    private static CatalogInputs Inputs(string nginx, string apache, HashSet<string>? kev = null)
    {
        var skipped = new List<string>();
        return new CatalogInputs
        {
            Meta = new CatalogMeta
            {
                Products = new()
                {
                    ["php"] = new ProductMeta { DisplayName = "PHP", EndOfLife = "php" },
                    ["nginx"] = new ProductMeta { DisplayName = "nginx", EndOfLife = "nginx" },
                    ["apache"] = new ProductMeta { DisplayName = "Apache", EndOfLife = "apache-http-server" },
                },
            },
            Cycles = new Dictionary<string, IReadOnlyList<EolCycle>>
            {
                ["php"] = Sources.ParseEndOfLife("""[{"cycle":"8.4","eol":"2028-12-31","latest":"8.4.27","support":"2026-12-31"}]"""),
                ["nginx"] = Sources.ParseEndOfLife("""
                    [{"cycle":"1.31","releaseDate":"2026-05-13","eol":false,"latest":"1.31.8"},
                     {"cycle":"1.30","releaseDate":"2026-04-14","eol":false,"latest":"1.30.6"},
                     {"cycle":"1.29","releaseDate":"2025-06-24","eol":"2026-05-13","latest":"1.29.8"}]
                    """),
                ["apache"] = Sources.ParseEndOfLife("""
                    [{"cycle":"2.4","releaseDate":"2012-02-21","eol":false,"latest":"2.4.69"},
                     {"cycle":"2.2","releaseDate":"2005-12-01","eol":"2017-07-11","latest":"2.2.34"}]
                    """),
            },
            PhpSrc = Sources.ParsePhpSrcAdvisories("""[{"ghsa_id":"GHSA-x","cve_id":"CVE-2024-4577","summary":"PHP-CGI","severity":"high","state":"published","vulnerabilities":[{"vulnerable_version_range":"< 8.4.10","patched_versions":"8.4.10"}]}]""", skipped),
            Nginx = Sources.ParseNginxAdvisories(nginx, skipped),
            Apache = Sources.ParseApacheAdvisories(apache, skipped),
            WebSkipped = skipped,
            Kev = new HashSet<string>([.. kev ?? [], .. Enumerable.Range(1, 150).Select(i => $"CVE-2020-{i:D5}")]),
            Now = Now,
        };
    }

    [Fact]
    public void ElFeed_LlevaNginxYApache_ConSusRamasYKev_YElEvaluadorLosLee()
    {
        var result = CatalogBuilder.Build(Inputs(NginxPage, ApachePage, kev: ["CVE-2021-41773"]));

        Assert.Empty(result.Errors);
        Assert.Empty(result.Held);
        var d = AuditDataset.Load(new MemoryStream(CatalogBuilder.Serialize(result.Dataset)));
        Assert.Equal("active", d.Products["nginx"].Branches["1.30"].Status);
        Assert.Equal("eol", d.Products["nginx"].Branches["1.29"].Status);
        Assert.Equal("2.4.69", d.Products["apache"].Branches["2.4"].Latest);
        Assert.Contains("nginx.org, httpd.apache.org", d.Source);
        Assert.True(d.Advisories.Single(a => a.Id == "CVE-2021-41773").Kev);

        var evaluator = new AuditEvaluator(d, DateOnly.FromDateTime(Now.UtcDateTime));
        AuditFinding Eval(ComponentKind kind, string version) => evaluator.Evaluate(new DetectedComponent
        {
            Kind = kind,
            Version = ComponentVersion.TryParse(version, out var v) ? v : default,
            ExecutablePath = @"C:\roost\bin\x",
            Source = "Roost",
        });
        Assert.Equal(AuditVerdict.Patched, Eval(ComponentKind.Nginx, "1.30.6").Verdict);   // la que instala Roost
        Assert.Equal(AuditVerdict.Patched, Eval(ComponentKind.Nginx, "1.31.8").Verdict);
        var old = Eval(ComponentKind.Nginx, "1.30.3");
        Assert.Equal(AuditVerdict.Vulnerable, old.Verdict);
        Assert.Contains("1.30.6", old.Headline);
        Assert.Equal(AuditVerdict.Eol, Eval(ComponentKind.Nginx, "1.29.8").Verdict);
        Assert.Equal(AuditVerdict.Patched, Eval(ComponentKind.Apache, "2.4.69").Verdict);
        var xampp = Eval(ComponentKind.Apache, "2.4.49");
        Assert.Equal(AuditVerdict.Vulnerable, xampp.Verdict);
        Assert.Contains("en KEV", xampp.Headline);
    }

    [Fact]
    public void UnAvisoQueDejariaEnRojoLaUltimaDeNginx_SeRetiene()
    {
        // Un aviso nuevo sin la 1.30 corregida todavía: afectaría a la 1.30.6, que es la última.
        var page = NginxPage.Replace("<ul>", """
            <ul><li><p>Algo nuevo<br>Severity: major<br><a href="https://www.cve.org/CVERecord?id=CVE-2026-99999">CVE-2026-99999</a><br>Not vulnerable: 1.31.9+<br>Vulnerable: 1.30.0-1.31.8</p></li>
            """);

        var result = CatalogBuilder.Build(Inputs(page, ApachePage));

        Assert.Empty(result.Errors);
        Assert.DoesNotContain(result.Dataset.Advisories, a => a.Id == "CVE-2026-99999");
        Assert.Contains(result.Held, h => h.StartsWith("CVE-2026-99999: afectaria a 1.30.6, la ultima de nginx 1.30", StringComparison.Ordinal));
    }

    [Fact]
    public void UnaPaginaQueDeRepenteNoDaNada_NoSePublica()
    {
        var result = CatalogBuilder.Build(Inputs("<html>mantenimiento</html>", ""));

        Assert.Contains(result.Errors, e => e.StartsWith("nginx.org no ha dado ningun aviso", StringComparison.Ordinal));
        Assert.Contains(result.Errors, e => e.StartsWith("httpd.apache.org no ha dado ningun aviso", StringComparison.Ordinal));
    }
}
