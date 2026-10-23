using Roost.Core.Audit;
using Roost.Core.Models;

namespace Roost.Core.Tests;

/// <summary>
/// Tests del EVALUADOR de auditoria aislado del escaneo: se construyen
/// <see cref="DetectedComponent"/> a mano y se evaluan contra el dataset
/// empaquetado (la semilla PHP2Shell). Fecha de referencia fija para que el
/// veredicto de EOL sea determinista.
/// </summary>
public class AuditEvaluatorTests
{
    // Dia del incendio PHP2Shell. Fijo para que el EOL no dependa de "hoy".
    private static readonly DateOnly Today = new(2026, 10, 22);

    private static AuditEvaluator CreateEvaluator()
        => new(AuditDataset.LoadEmbedded(), Today);

    private static DetectedComponent Php(int major, int minor, int patch, string? raw = null,
        string source = "Laragon")
    {
        var text = raw ?? $"{major}.{minor}.{patch}";
        return new DetectedComponent
        {
            Kind = ComponentKind.Php,
            Version = new ComponentVersion(major, minor, patch, text),
            ExecutablePath = $@"C:\laragon\bin\php\php-{major}.{minor}.{patch}\php.exe",
            Source = source,
            Sapi = "cli",
            IsOnPath = false,
        };
    }

    [Fact]
    public void Dataset_Empaquetado_Carga_Y_Tiene_PHP2Shell()
    {
        var dataset = AuditDataset.LoadEmbedded();

        Assert.False(string.IsNullOrWhiteSpace(dataset.SchemaVersion));
        Assert.Contains("php", dataset.Products.Keys);
        Assert.Contains(dataset.Advisories, a => a.Id == "CVE-2026-109618");
        Assert.Contains(dataset.Advisories, a => a.Id == "CVE-2026-109619");
    }

    [Fact]
    public void Php_8_3_18_Es_Vulnerable()
    {
        var finding = CreateEvaluator().Evaluate(Php(8, 3, 18));

        Assert.Equal(AuditVerdict.Vulnerable, finding.Verdict);
        Assert.Equal(AuditColor.Red, finding.Color);
        Assert.False(finding.Uncertain);
        Assert.True(finding.IsBlocking);
        Assert.Contains(finding.MatchedAdvisories, a => a.Id == "CVE-2026-109618");
        Assert.Contains(finding.MatchedAdvisories, a => a.Id == "CVE-2026-109619");
        Assert.NotEmpty(finding.Mitigations);
        Assert.Equal("8.3.36", finding.BranchLatest);
    }

    [Fact]
    public void Php_8_3_36_Esta_Parcheada()
    {
        var finding = CreateEvaluator().Evaluate(Php(8, 3, 36));

        Assert.Equal(AuditVerdict.Patched, finding.Verdict);
        Assert.Equal(AuditColor.Green, finding.Color);
        Assert.False(finding.Uncertain);
        Assert.False(finding.IsBlocking);
        Assert.Empty(finding.MatchedAdvisories);
    }

    [Fact]
    public void Php_7_4_9_Es_Eol()
    {
        var finding = CreateEvaluator().Evaluate(Php(7, 4, 9));

        Assert.Equal(AuditVerdict.Eol, finding.Verdict);
        Assert.Equal(AuditColor.Red, finding.Color);
        Assert.True(finding.IsBlocking);
        Assert.Equal(new DateOnly(2022, 11, 28), finding.Eol);
    }

    [Fact]
    public void Php_Con_Sufijo_De_Distro_Es_Ambar_Incierto()
    {
        // 8.3.9 "por numero" seria VULNERABLE, pero el backport +esm puede estar
        // ya parcheado: ambar honesto, no rojo, y no bloquea CI.
        var component = Php(8, 3, 9, raw: "8.3.9-1ubuntu3.1+esm2", source: "system");

        var finding = CreateEvaluator().Evaluate(component);

        Assert.True(finding.Uncertain);
        Assert.Equal(AuditColor.Amber, finding.Color);
        Assert.False(finding.IsBlocking);
        Assert.Contains(finding.Notes, n => n.Contains("backport", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Php_Con_Sufijo_De_Distro_En_Raw_Tambien_Es_Incierto_Aunque_El_Source_No_Lo_Sea()
    {
        // El marcador de distro viene solo en el texto de la version, no en el source.
        var component = Php(8, 3, 9, raw: "8.3.9+deb12u1", source: "PATH");

        var finding = CreateEvaluator().Evaluate(component);

        Assert.True(finding.Uncertain);
        Assert.Equal(AuditColor.Amber, finding.Color);
    }

    [Fact]
    public void Php_8_1_Es_Eol_Aunque_Numericamente_Tambien_Sea_Vulnerable()
    {
        // 8.1 esta EOL (sin parche) y ademas le afecta PHP2Shell; EOL manda.
        var finding = CreateEvaluator().Evaluate(Php(8, 1, 30));

        Assert.Equal(AuditVerdict.Eol, finding.Verdict);
        Assert.Equal(AuditColor.Red, finding.Color);
        Assert.Contains(finding.MatchedAdvisories, a => a.Id == "CVE-2026-109618");
    }

    [Fact]
    public void Rama_Futura_No_Reconocida_Es_Desconocida_Ambar()
    {
        var finding = CreateEvaluator().Evaluate(Php(8, 9, 0));

        Assert.Equal(AuditVerdict.Unknown, finding.Verdict);
        Assert.Equal(AuditColor.Amber, finding.Color);
        Assert.False(finding.IsBlocking);
    }

    [Fact]
    public void Php_Parcheada_Pero_No_La_Ultima_Avisa_Sin_Bloquear()
    {
        // 8.3.20 no tiene aviso abierto (>= 8.3.36 corrige), pero no es la ultima.
        var finding = CreateEvaluator().Evaluate(Php(8, 4, 27));

        Assert.Equal(AuditVerdict.Patched, finding.Verdict);
        Assert.Equal(AuditColor.Green, finding.Color);
        Assert.False(finding.IsBlocking);
    }

    [Fact]
    public void MariaDb_Eol_Por_Fecha_Es_Best_Effort()
    {
        var component = new DetectedComponent
        {
            Kind = ComponentKind.MariaDb,
            Version = new ComponentVersion(10, 4, 10, "10.4.10"),
            ExecutablePath = @"C:\maria\bin\mariadbd.exe",
            Source = "Laragon", // no "system": que no lo marque incierto por origen
            IsOnPath = false,
        };

        var finding = CreateEvaluator().Evaluate(component);

        Assert.Equal(AuditVerdict.Eol, finding.Verdict);
        Assert.Contains(finding.Notes, n => n.Contains("best-effort", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void MySql_Con_Soporte_Sin_Feed_De_Cve_Es_Soportada_No_Parcheada()
    {
        var component = new DetectedComponent
        {
            Kind = ComponentKind.MySql,
            Version = new ComponentVersion(8, 4, 3, "8.4.3"),
            ExecutablePath = @"C:\laragon\bin\mysql\mysql-8.4.3-winx64\bin\mysqld.exe",
            Source = "Laragon",
            IsOnPath = true,
        };

        var finding = CreateEvaluator().Evaluate(component);

        Assert.Equal(AuditVerdict.Supported, finding.Verdict);
        Assert.Equal("SOPORTADA", finding.VerdictLabel);
        Assert.False(finding.IsBlocking);
    }

    [Fact]
    public void MariaDb_De_Program_Files_En_Windows_No_Es_Incierto()
    {
        var component = new DetectedComponent
        {
            Kind = ComponentKind.MariaDb,
            Version = new ComponentVersion(10, 6, 28, "10.6.28"),
            ExecutablePath = @"C:\Program Files\MariaDB 10.6\bin\mariadbd.exe",
            Source = "Program Files",
            IsOnPath = false,
        };

        var finding = CreateEvaluator().Evaluate(component);

        Assert.Equal(AuditVerdict.Eol, finding.Verdict);
        Assert.False(finding.Uncertain);
        Assert.True(finding.IsBlocking);
    }

    [Fact]
    public void ConMuchosAvisos_ElTitularDiceLosTresPeores_YCuantosMas()
    {
        // Con el feed entero, un PHP viejo casa con decenas de avisos: el titular no puede listarlos todos.
        var dataset = new AuditDataset
        {
            Products = new() { ["php"] = new ProductInfo { Branches = new() { ["8.3"] = new BranchInfo { Latest = "8.3.36", Status = "security" } } } },
            Advisories =
            [
                new Advisory { Id = "CVE-BAJO", Product = "php", Affected = [">=8.3.0 <8.3.20"], Severity = "low" },
                new Advisory { Id = "CVE-MEDIO", Product = "php", Affected = [">=8.3.0 <8.3.20"], Cvss = 5.3 },
                new Advisory { Id = "CVE-KEV", Product = "php", Affected = [">=8.3.0 <8.3.20"], Cvss = 4.0, Kev = true },
                new Advisory { Id = "CVE-CRITICO", Product = "php", Affected = [">=8.3.0 <8.3.20"], Severity = "critical" },
                new Advisory { Id = "CVE-ALTO", Product = "php", Affected = [">=8.3.0 <8.3.20"], Cvss = 7.5, FixedIn = new() { ["8.3"] = "8.3.20" } },
            ],
        };

        var finding = new AuditEvaluator(dataset, Today).Evaluate(Php(8, 3, 10));

        Assert.Equal(AuditVerdict.Vulnerable, finding.Verdict);
        Assert.StartsWith("Afectada por CVE-KEV, CVE-CRITICO, CVE-ALTO y 2 mas (CVSS 7.5, en KEV", finding.Headline);
        Assert.EndsWith("actualiza a 8.3.20.", finding.Headline);
        Assert.Equal(5, finding.MatchedAdvisories.Count);   // el detalle sigue trayendo todos
    }

    [Fact]
    public void SinCvss_ElTitularDiceLaGravedad()
    {
        var dataset = new AuditDataset
        {
            Products = new() { ["php"] = new ProductInfo { Branches = new() { ["8.4"] = new BranchInfo { Latest = "8.4.27", Status = "active" } } } },
            Advisories = [new Advisory { Id = "CVE-2026-109618", Product = "php", Affected = [">=8.4.0 <8.4.27"], Severity = "critical" }],
        };

        var finding = new AuditEvaluator(dataset, Today).Evaluate(Php(8, 4, 20));

        Assert.Contains("(gravedad critica)", finding.Headline);
    }
}
