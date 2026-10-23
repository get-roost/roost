using Roost.Core.Audit;
using Roost.Core.Models;

namespace Roost.Catalog.Tests;

/// <summary>
/// El generador del feed, sin red: cada fuente con un trozo de su JSON real (de octubre de 2026),
/// y el montaje con sus reglas contra el falso rojo.
/// </summary>
public class CatalogBuilderTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 23, 7, 17, 0, TimeSpan.Zero);

    private const string PhpCycles = """
        [{"cycle":"8.5","eol":"2029-12-31","latest":"8.5.12","support":"2027-12-31"},
         {"cycle":"8.4","eol":"2028-12-31","latest":"8.4.27","support":"2026-12-31"},
         {"cycle":"8.3","eol":"2027-12-31","latest":"8.3.36","support":"2025-12-31"},
         {"cycle":"8.2","eol":"2026-12-31","latest":"8.2.35","support":"2024-12-31"},
         {"cycle":"8.1","eol":"2025-12-31","latest":"8.1.34","support":"2023-11-25"},
         {"cycle":"5","eol":true,"latest":"5.6.40"}]
        """;

    private const string MySqlCycles = """
        [{"cycle":"26.7","eol":false,"latest":"26.7.0","support":true},
         {"cycle":"8.4","eol":"2032-04-30","latest":"8.4.11","support":"2029-04-30","lts":true},
         {"cycle":"5.7","eol":"2023-10-31","latest":"5.7.44","support":"2020-10-21"}]
        """;

    private const string MariaDbCycles = """
        [{"cycle":"11.4","eol":"2029-05-29","latest":"11.4.13","lts":true},
         {"cycle":"10.6","eol":"2026-07-06","latest":"10.6.28"}]
        """;

    /// <summary>Un aviso de GHSA de php-src como los de PHP2Shell: una entrada por rama, con su cota superior.</summary>
    private static string Ghsa(string ghsa, string? cve, string summary, string severity, double? cvss3, params (string Range, string Patched)[] branches)
    {
        var vulns = string.Join(",", branches.Select(b =>
            $$"""{"package":{"ecosystem":"php","name":"php-src"},"vulnerable_version_range":"{{b.Range}}","patched_versions":"{{b.Patched}}"}"""));
        var v3 = cvss3 is { } s ? s.ToString(System.Globalization.CultureInfo.InvariantCulture) : "null";
        var score = "{\"cvss_v3\":{\"score\":" + v3 + "},\"cvss_v4\":{\"score\":null}}";
        var cveJson = cve is null ? "null" : $"\"{cve}\"";
        return $$"""
            {"ghsa_id":"{{ghsa}}","cve_id":{{cveJson}},"summary":"{{summary}}","severity":"{{severity}}","state":"published",
             "published_at":"2026-10-21T13:34:25Z","withdrawn_at":null,"html_url":"https://github.com/php/php-src/security/advisories/{{ghsa}}",
             "cvss_severities":{{score}},"vulnerabilities":[{{vulns}}]}
            """;
    }

    private static readonly (string, string)[] Php2ShellBranches =
        [("< 8.2.35", "8.2.35"), ("< 8.3.36", "8.3.36"), ("< 8.4.27", "8.4.27"), ("< 8.5.12", "8.5.12")];

    private static string GhsaPage(params string[] advisories) => "[" + string.Join(",", advisories) + "]";

    private static CatalogMeta Meta() => new()
    {
        Products = new()
        {
            ["php"] = new ProductMeta { DisplayName = "PHP", EndOfLife = "php" },
            ["mysql"] = new ProductMeta { DisplayName = "MySQL", EndOfLife = "mysql" },
            ["mariadb"] = new ProductMeta { DisplayName = "MariaDB", EndOfLife = "mariadb" },
        },
    };

    /// <summary>Un KEV verosímil: más de 100 entradas (con menos, el generador lo da por roto).</summary>
    private static HashSet<string> Kev(params string[] cves)
        => [.. cves, .. Enumerable.Range(1, 150).Select(i => $"CVE-2020-{i:D5}")];

    private static CatalogInputs Inputs(string ghsaPage, IReadOnlyList<ManualAdvisory>? manual = null, AuditDataset? previous = null, HashSet<string>? kev = null)
    {
        var skipped = new List<string>();
        return new CatalogInputs
        {
            Meta = Meta(),
            Cycles = new Dictionary<string, IReadOnlyList<EolCycle>>
            {
                ["php"] = Sources.ParseEndOfLife(PhpCycles),
                ["mysql"] = Sources.ParseEndOfLife(MySqlCycles),
                ["mariadb"] = Sources.ParseEndOfLife(MariaDbCycles),
            },
            PhpSrc = Sources.ParsePhpSrcAdvisories(ghsaPage, skipped),
            Skipped = skipped,
            Kev = kev ?? Kev(),
            KevVersion = "2026.10.21",
            Manual = manual ?? [],
            Previous = previous,
            Now = Now,
        };
    }

    private static DetectedComponent Php(string version) => new()
    {
        Kind = ComponentKind.Php,
        Version = ComponentVersion.TryParse(version, out var v) ? v : throw new ArgumentException(version),
        ExecutablePath = $@"C:\roost\bin\php\{version}-nts-x64\php.exe",
        Source = "Roost",
    };

    [Fact]
    public void EndOfLife_FechasYBanderas_DanElEstadoDeCadaRama()
    {
        var php = Sources.ParseEndOfLife(PhpCycles);
        var today = DateOnly.FromDateTime(Now.UtcDateTime);

        Assert.Null(php.Single(c => c.Cycle == "5").Branch);   // «5» no es una rama X.Y
        Assert.Equal("active", php.Single(c => c.Cycle == "8.5").ToBranch(today).Status);
        var b83 = php.Single(c => c.Cycle == "8.3").ToBranch(today);
        Assert.Equal("security", b83.Status);                    // el soporte activo acabó en 2025
        Assert.Equal(new DateOnly(2027, 12, 31), b83.SecurityUntil);
        Assert.Equal("eol", php.Single(c => c.Cycle == "8.1").ToBranch(today).Status);

        var mysql = Sources.ParseEndOfLife(MySqlCycles);
        Assert.Equal("active", mysql.Single(c => c.Cycle == "26.7").ToBranch(today).Status);   // eol: false, support: true
        Assert.Null(mysql.Single(c => c.Cycle == "26.7").ToBranch(today).Eol);
        var maria = Sources.ParseEndOfLife(MariaDbCycles);
        Assert.Equal("active", maria.Single(c => c.Cycle == "11.4").ToBranch(today).Status);   // sin «support»
        Assert.Equal("eol", maria.Single(c => c.Cycle == "10.6").ToBranch(today).Status);
    }

    [Fact]
    public void PhpNet_DaLaUltimaDeLaRama()
    {
        Assert.Equal("8.3.36", Sources.ParsePhpNetLatest("""{"announcement":true,"supported_versions":["8.2","8.3"],"version":"8.3.36"}"""));
        Assert.Null(Sources.ParsePhpNetLatest("""{"error":"Unknown version"}"""));
    }

    [Fact]
    public void Ghsa_CadaCotaSeAcotaASuRama_YUnParcheDeOtraRamaNoCae()
    {
        var skipped = new List<string>();
        var a = Assert.Single(Sources.ParsePhpSrcAdvisories(GhsaPage(Ghsa("GHSA-w8hw-w9fx-j79w", "CVE-2026-109618", "RCE via OOM corruption", "critical", null, Php2ShellBranches)), skipped)).Advisory;

        Assert.Empty(skipped);
        Assert.Equal("CVE-2026-109618", a.Id);
        Assert.Equal(["GHSA-w8hw-w9fx-j79w"], a.Aliases);
        Assert.Equal([">=8.2.0 <8.2.35", ">=8.3.0 <8.3.36", ">=8.4.0 <8.4.27", ">=8.5.0 <8.5.12"], a.Affected);
        Assert.Equal("8.3.36", a.FixedIn["8.3"]);
        Assert.Equal(0, a.Cvss);
        Assert.Equal("critical", a.Severity);

        bool Hit(string version) => ComponentVersion.TryParse(version, out var v) && a.Affected.Any(r => VersionRange.Satisfies(v, r));
        Assert.False(Hit("8.2.35"));   // con «< 8.3.36» tal cual, el 8.2.35 ya corregido saldría vulnerable
        Assert.True(Hit("8.2.34"));
        Assert.True(Hit("8.3.35"));
        Assert.False(Hit("8.3.36"));
        Assert.False(Hit("8.1.34"));   // la 8.1 no la nombra el aviso (es EOL: ya sale en rojo por eso)
    }

    [Theory]
    [InlineData(">=8.1.0, <=8.1.15", "8.1.16", ">=8.1.0 <=8.1.15")]
    [InlineData("< 8.3.5", "8.3.6", ">=8.3.0 <8.3.5")]          // manda el rango; el parche dice a qué subir
    [InlineData("8.2.12", "", "=8.2.12")]                         // versión suelta: esa versión (regla de GitHub)
    [InlineData("8.3.6", "8.1.29, 8.2.20, 8.3.8", ">=8.1.0 <8.1.29|>=8.2.0 <8.2.20|>=8.3.0 <8.3.8")]   // con parches, mandan ellos
    [InlineData(">= 8.1.0", "8.1.29", ">=8.1.0 <8.1.29")]        // sin cota superior, de los parches
    [InlineData("< 8.2.0", "", "<8.2.0")]                         // cota en X.Y.0: todo lo anterior, sin acotar
    public void Ghsa_RangosRaros_SeLeenSinAdivinar(string range, string patched, string expected)
    {
        var fixedIn = new Dictionary<string, string>();
        Assert.Equal(expected.Split('|'), Sources.NormalizeRange(range, patched, fixedIn));
    }

    [Theory]
    [InlineData(">= 8.1.0", "")]          // abierto por arriba y sin parche: dejaría en rojo hasta la última
    [InlineData("todas", "")]
    [InlineData("", "")]
    public void Ghsa_LoQueNoSePuedeLeer_NoSePublica(string range, string patched)
    {
        Assert.Empty(Sources.NormalizeRange(range, patched, new Dictionary<string, string>()));
        var skipped = new List<string>();
        Assert.Empty(Sources.ParsePhpSrcAdvisories(GhsaPage(Ghsa("GHSA-xxxx-yyyy-zzzz", null, "raro", "low", null, (range, patched))), skipped));
        Assert.Contains("GHSA-xxxx-yyyy-zzzz", Assert.Single(skipped));
    }

    [Fact]
    public void Ghsa_SinCve_UsaElGhsa_LeeElCvss_YSaltaLosRetirados()
    {
        var withdrawn = Ghsa("GHSA-retirado", "CVE-2025-1", "retirado", "high", 7.5, ("< 8.4.10", "8.4.10")).Replace("\"withdrawn_at\":null", "\"withdrawn_at\":\"2025-01-01T00:00:00Z\"");
        var list = Sources.ParsePhpSrcAdvisories(GhsaPage(Ghsa("GHSA-ch8v-r6jh-4vvr", null, "ext/filter", "low", 3.7, ("<8.4.25", "8.4.25")), withdrawn), []);

        var a = Assert.Single(list).Advisory;
        Assert.Equal("GHSA-ch8v-r6jh-4vvr", a.Id);
        Assert.Empty(a.Aliases);
        Assert.Equal(3.7, a.Cvss);
    }

    [Fact]
    public void Kev_MarcaLosAvisosQueCisaDaPorExplotados()
    {
        var (cves, version) = Sources.ParseKev("""{"catalogVersion":"2026.10.21","vulnerabilities":[{"cveID":"CVE-2024-4577"},{"cveID":"CVE-2019-11043"}]}""");
        Assert.Equal("2026.10.21", version);
        Assert.Contains("CVE-2024-4577", cves);

        var result = CatalogBuilder.Build(Inputs(
            GhsaPage(Ghsa("GHSA-3qgc-jrrr-25jv", "CVE-2024-4577", "PHP-CGI", "high", null, ("< 8.3.8", "8.3.8"))),
            kev: Kev("CVE-2024-4577")));

        Assert.Empty(result.Errors);
        Assert.True(Assert.Single(result.Dataset.Advisories).Kev);
    }

    [Fact]
    public void AMano_SustituyeSusCampos_SinMezclarDosAvisosConElMismoAlias()
    {
        var page = GhsaPage(
            Ghsa("GHSA-w8hw-w9fx-j79w", "CVE-2026-109618", "RCE via OOM corruption", "critical", null, Php2ShellBranches),
            Ghsa("GHSA-6wmf-4rv5-p3rp", "CVE-2026-109619", "RCE via stale global state", "critical", null, Php2ShellBranches));
        ManualAdvisory[] manual =
        [
            new() { Id = "CVE-2026-109618", Aliases = ["PHP2Shell"], Title = "PHP2Shell: memoria", Cvss = 9.2, Mitigations = [new Mitigation { Ini = "memory_limit", Value = "128M" }] },
            new() { Id = "CVE-2026-109619", Aliases = ["PHP2Shell"], Title = "PHP2Shell: upload_progress", Mitigations = [new Mitigation { Ini = "session.upload_progress.enabled", Value = "0" }] },
        ];

        var result = CatalogBuilder.Build(Inputs(page, manual));

        Assert.Empty(result.Errors);
        var first = result.Dataset.Advisories.Single(a => a.Id == "CVE-2026-109618");
        var second = result.Dataset.Advisories.Single(a => a.Id == "CVE-2026-109619");
        Assert.Equal("PHP2Shell: memoria", first.Title);
        Assert.Equal("memory_limit", Assert.Single(first.Mitigations).Ini);
        Assert.Equal(["PHP2Shell", "GHSA-w8hw-w9fx-j79w"], first.Aliases);
        Assert.Equal(9.2, first.Cvss);
        Assert.Equal("PHP2Shell: upload_progress", second.Title);
        Assert.Equal("session.upload_progress.enabled", Assert.Single(second.Mitigations).Ini);
        Assert.Equal(first.Affected, second.Affected);   // lo que no se pone, se queda como viene
    }

    [Fact]
    public void AMano_IgnorarQuita_YUnoNuevoSinProductoOConRangoMalo_NoSePublica()
    {
        var page = GhsaPage(Ghsa("GHSA-3qgc-jrrr-25jv", "CVE-2024-4577", "PHP-CGI", "high", null, ("< 8.3.8", "8.3.8")));

        var ignored = CatalogBuilder.Build(Inputs(page, [new ManualAdvisory { Id = "GHSA-3qgc-jrrr-25jv", Ignore = true, File = "x.json" }]));
        Assert.Empty(ignored.Errors);
        Assert.Empty(ignored.Dataset.Advisories);   // por el GHSA también se encuentra

        var bad = CatalogBuilder.Build(Inputs(page,
        [
            new ManualAdvisory { Id = "CVE-2026-1", Affected = [">=8.4.0 <8.4.30"], File = "sin-producto.json" },
            new ManualAdvisory { Id = "CVE-2026-2", Product = "php", Affected = ["<8.4"], File = "rango-malo.json" },
            new ManualAdvisory { Id = "CVE-2026-3", Product = "redis", Affected = ["<7.0.0"], File = "producto-raro.json" },
        ]));
        Assert.Contains(bad.Errors, e => e.StartsWith("sin-producto.json"));
        Assert.Contains(bad.Errors, e => e.StartsWith("rango-malo.json"));
        Assert.Contains(bad.Errors, e => e.StartsWith("producto-raro.json"));
    }

    [Fact]
    public void UnAvisoQueDejariaEnRojoLaUltima_SeRetiene_HastaQueLoConfirmaUnFichero()
    {
        // Sin parche aún (o mal leído): afecta a la 8.4.27, que es la última de la 8.4.
        var page = GhsaPage(Ghsa("GHSA-cero-dia", "CVE-2026-200000", "zero day", "critical", 9.8, ("< 8.4.28", "8.4.28")));

        var held = CatalogBuilder.Build(Inputs(page));
        Assert.Empty(held.Errors);
        Assert.Empty(held.Dataset.Advisories);
        Assert.Contains("CVE-2026-200000", Assert.Single(held.Held));

        var confirmed = CatalogBuilder.Build(Inputs(page, [new ManualAdvisory { Id = "CVE-2026-200000", Note = "revisado" }]));
        Assert.Empty(confirmed.Held);
        Assert.Single(confirmed.Dataset.Advisories);
    }

    [Fact]
    public void ContraElPublicado_NoSePublicaSiPierdeDatosDeGolpe()
    {
        var page = GhsaPage(Ghsa("GHSA-3qgc-jrrr-25jv", "CVE-2024-4577", "PHP-CGI", "high", null, ("< 8.3.8", "8.3.8")));
        var good = CatalogBuilder.Build(Inputs(page)).Dataset;

        var previous = new AuditDataset
        {
            SchemaVersion = "1.0.0",
            Generated = Now.AddDays(-1),
            Products = new(good.Products) { ["nginx"] = new ProductInfo { Branches = new() { ["1.30"] = new BranchInfo() } } },
            Advisories = [.. Enumerable.Range(1, 10).Select(i => new Advisory { Id = $"CVE-2025-{i}", Product = "php" })],
        };
        var result = CatalogBuilder.Build(Inputs(page, previous: previous));

        Assert.Contains(result.Errors, e => e.Contains("perdido el producto nginx"));
        Assert.Contains(result.Errors, e => e.Contains("demasiados de golpe"));
        Assert.Equal(["CVE-2024-4577"], result.New);

        var same = CatalogBuilder.Build(Inputs(page, previous: new AuditDataset { Generated = Now }));
        Assert.Contains(same.Errors, e => e.Contains("no es posterior"));
    }

    [Fact]
    public void ElFeed_LoLeeRoost_YElEvaluadorDaElVeredictoDeHoy()
    {
        var page = GhsaPage(
            Ghsa("GHSA-6wmf-4rv5-p3rp", "CVE-2026-109619", "RCE via stale global state", "critical", null, Php2ShellBranches),
            Ghsa("GHSA-3qgc-jrrr-25jv", "CVE-2024-4577", "PHP-CGI", "high", null, ("< 8.3.8", "8.3.8"), ("< 8.2.20", "8.2.20")));
        var result = CatalogBuilder.Build(Inputs(page, kev: Kev("CVE-2024-4577")));
        Assert.Empty(result.Errors);

        var bytes = CatalogBuilder.Serialize(result.Dataset);
        Assert.DoesNotContain((byte)'\r', bytes);
        var dataset = AuditDataset.Load(new MemoryStream(bytes));
        Assert.Equal("1.0.0", dataset.SchemaVersion);
        Assert.Equal(Now, dataset.Generated);
        Assert.Equal("Actualiza a la ultima de tu rama: 8.2.35 / 8.3.36 / 8.4.27 / 8.5.12 (o posterior).", dataset.Remediation.RealFix);

        var evaluator = new AuditEvaluator(dataset, DateOnly.FromDateTime(Now.UtcDateTime));
        var vulnerable = evaluator.Evaluate(Php("8.3.35"));
        Assert.Equal(AuditVerdict.Vulnerable, vulnerable.Verdict);
        Assert.Contains("gravedad critica", vulnerable.Headline);   // GHSA no trae CVSS
        Assert.Contains("8.3.36", vulnerable.Headline);
        var cgi = evaluator.Evaluate(Php("8.3.7"));
        Assert.Contains("en KEV", cgi.Headline);
        Assert.Equal(AuditVerdict.Patched, evaluator.Evaluate(Php("8.3.36")).Verdict);
        Assert.Equal(AuditVerdict.Eol, evaluator.Evaluate(Php("8.1.34")).Verdict);
    }

    [Fact]
    public void LosDatosDeCatalog_SeLeenYSonValidos()
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "catalog");
        var meta = ManualData.LoadMeta(dir);
        var manual = ManualData.LoadAdvisories(dir);

        Assert.Equal(["php", "mysql", "mariadb", "nginx"], meta.Products.Keys);
        Assert.NotEmpty(manual);
        var page = GhsaPage(
            Ghsa("GHSA-w8hw-w9fx-j79w", "CVE-2026-109618", "RCE via OOM corruption", "critical", null, Php2ShellBranches),
            Ghsa("GHSA-6wmf-4rv5-p3rp", "CVE-2026-109619", "RCE via stale global state", "critical", null, Php2ShellBranches));
        var result = CatalogBuilder.Build(Inputs(page, manual) with
        {
            Meta = new CatalogMeta { Products = new(meta.Products.Where(p => p.Key != "nginx")), Remediation = meta.Remediation },
        });

        Assert.Empty(result.Errors);
        Assert.All(result.Dataset.Advisories, a => Assert.Contains("PHP2Shell", a.Aliases));
    }
}
