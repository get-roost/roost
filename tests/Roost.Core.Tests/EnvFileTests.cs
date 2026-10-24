using System.Text;
using Roost.Core.Deploy;

namespace Roost.Core.Tests;

/// <summary>
/// El .env leído y fusionado sin perder nada: comentarios, orden, comillas, valores con = y #,
/// export, valores de varias líneas, CRLF, el BOM y las claves comentadas del .env.example de Laravel.
/// </summary>
public class EnvFileTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "roost-envfile-" + Guid.NewGuid().ToString("N"));

    // El .env.example de Laravel 13, recortado: la base de datos va comentada (sqlite por defecto).
    private const string LaravelExample = """
        APP_NAME=Laravel
        APP_ENV=local
        APP_KEY=
        APP_DEBUG=true
        APP_URL=http://localhost

        DB_CONNECTION=sqlite
        # DB_HOST=127.0.0.1
        # DB_PORT=3306
        # DB_DATABASE=laravel
        # DB_USERNAME=root
        # DB_PASSWORD=

        REDIS_CLIENT=phpredis
        REDIS_HOST=127.0.0.1
        REDIS_PASSWORD=null
        REDIS_PORT=6379

        MAIL_MAILER=log
        MAIL_SCHEME=null
        MAIL_HOST=127.0.0.1
        MAIL_PORT=2525
        MAIL_FROM_ADDRESS="hello@example.com"
        MAIL_FROM_NAME="${APP_NAME}"

        VITE_APP_NAME="${APP_NAME}"

        """;

    private static string Lf(string text) => text.Replace("\r\n", "\n");

    private static string Crlf(string text) => Lf(text).Replace("\n", "\r\n");

    [Fact]
    public void SinCambios_SaleByteAByteComoEntro()
    {
        var text = Lf(LaravelExample) + "# un comentario al final sin salto de linea";
        Assert.Equal(text, EnvFile.Parse(text).ToString());
        Assert.Equal(Crlf(LaravelExample), EnvFile.Parse(Crlf(LaravelExample)).ToString());
        Assert.Equal("", EnvFile.Parse("").ToString());
    }

    [Fact]
    public void Get_LeeSinComillasConComillasYConComentarioDetras()
    {
        var env = EnvFile.Parse(Lf("""
            A=sin comillas
            B="con dobles"
            C='con simples'
            D=valor # un comentario
            E=valor#pegado
            F="hash # dentro"
            G=a=b=c
            H=
            export I=exportada
            J = con espacios
            K="escapes \"comillas\" y \\barra y \$dolar"
            L='literal \n sin escapes'
            """));

        Assert.Equal("sin comillas", env.Get("A"));
        Assert.Equal("con dobles", env.Get("B"));
        Assert.Equal("con simples", env.Get("C"));
        Assert.Equal("valor", env.Get("D"));
        Assert.Equal("valor#pegado", env.Get("E"));   // un # pegado es parte del valor
        Assert.Equal("hash # dentro", env.Get("F"));
        Assert.Equal("a=b=c", env.Get("G"));
        Assert.Equal("", env.Get("H"));
        Assert.Equal("exportada", env.Get("I"));
        Assert.Equal("con espacios", env.Get("J"));
        Assert.Equal("escapes \"comillas\" y \\barra y $dolar", env.Get("K"));
        Assert.Equal("literal \\n sin escapes", env.Get("L"));
        Assert.Null(env.Get("NO_ESTA"));
        Assert.True(env.Has("H"));
    }

    [Fact]
    public void Get_DeLaPrimeraCuandoSeRepite_YLasComentadasNoCuentan()
    {
        var env = EnvFile.Parse("APP_KEY=primera\nAPP_KEY=segunda\n# DB_HOST=127.0.0.1\n");
        Assert.Equal("primera", env.Get("APP_KEY"));
        Assert.Null(env.Get("DB_HOST"));
        Assert.False(env.Has("DB_HOST"));
        Assert.True(env.IsCommented("DB_HOST"));
        Assert.Equal(["APP_KEY"], env.Keys);
    }

    [Fact]
    public void Set_CambiaEnSuSitio_ConservaExportYElComentarioDeDetras()
    {
        var env = EnvFile.Parse("# cabecera\nexport APP_URL=http://localhost # la url\nAPP_ENV=local\n");
        Assert.Equal(EnvChange.Changed, env.Set("APP_URL", "https://tienda.test:8445"));
        Assert.Equal("# cabecera\nexport APP_URL=https://tienda.test:8445 # la url\nAPP_ENV=local\n", env.ToString());
    }

    [Fact]
    public void Set_ConElMismoValor_NoTocaLaLinea_NiSusComillas()
    {
        var text = "MAIL_FROM_NAME=\"${APP_NAME}\"\nAPP_NAME='Mi tienda'\n";
        var env = EnvFile.Parse(text);
        Assert.Equal(EnvChange.Same, env.Set("APP_NAME", "Mi tienda"));
        Assert.Equal(text, env.ToString());
    }

    [Fact]
    public void Set_DescomentaLaClaveEnSuSitio_ComoLosDbDelEjemploDeLaravel()
    {
        var env = EnvFile.Parse(Lf(LaravelExample));
        env.Set("DB_CONNECTION", "mysql");
        env.Set("DB_HOST", "127.0.0.1");
        env.Set("DB_PORT", "3409");
        env.Set("DB_DATABASE", "tienda");
        env.Set("DB_USERNAME", "root");
        env.Set("DB_PASSWORD", "");

        var text = env.ToString();
        Assert.Contains("DB_CONNECTION=mysql\nDB_HOST=127.0.0.1\nDB_PORT=3409\nDB_DATABASE=tienda\nDB_USERNAME=root\nDB_PASSWORD=\n\nREDIS_CLIENT", text);
        Assert.DoesNotContain("# DB_", text);
        Assert.Equal("3409", EnvFile.Parse(text).Get("DB_PORT"));
    }

    [Fact]
    public void Set_UnaClaveNueva_VaTrasLaUltimaDeSuGrupo_OAlFinalSeparada()
    {
        var env = EnvFile.Parse(Lf(LaravelExample));
        Assert.Equal(EnvChange.Added, env.Set("MAIL_ENCRYPTION", "null"));
        Assert.Equal(EnvChange.Added, env.Set("ROOST_NUEVA", "si"));
        var text = env.ToString();

        Assert.Contains("MAIL_FROM_NAME=\"${APP_NAME}\"\nMAIL_ENCRYPTION=null\n\nVITE_APP_NAME", text);
        Assert.EndsWith("VITE_APP_NAME=\"${APP_NAME}\"\n\nROOST_NUEVA=si\n", text);
    }

    [Fact]
    public void Set_EnUnFicheroSinSaltoDeLineaAlFinal_LoPoneAntes()
    {
        var env = EnvFile.Parse("APP_ENV=local");
        env.Set("APP_DEBUG", "true");
        Assert.Equal("APP_ENV=local\nAPP_DEBUG=true\n", env.ToString());

        var empty = EnvFile.Parse("");
        empty.Set("APP_URL", "https://a.test");
        Assert.Equal("APP_URL=https://a.test\n", empty.ToString());
    }

    [Fact]
    public void ConCrlf_LoNuevoVaConCrlf_YLoDemasNoCambia()
    {
        var env = EnvFile.Parse(Crlf(LaravelExample));
        Assert.Equal("\r\n", env.NewLine);
        env.Set("DB_CONNECTION", "mysql");
        env.Set("DB_HOST", "127.0.0.1");
        env.Set("APP_URL", "https://tienda.test");
        env.Set("MAIL_ENCRYPTION", "null");

        var text = env.ToString();
        Assert.DoesNotContain("\n", text.Replace("\r\n", ""));   // ni un LF suelto
        Assert.Contains("APP_URL=https://tienda.test\r\n", text);
        Assert.Contains("DB_HOST=127.0.0.1\r\n# DB_PORT=3306\r\n", text);
        Assert.Contains("MAIL_FROM_NAME=\"${APP_NAME}\"\r\nMAIL_ENCRYPTION=null\r\n", text);
    }

    [Fact]
    public void FinalesDeLineaMezclados_SeConservanLineaALinea()
    {
        var text = "A=1\r\nB=2\nC=3\r\n";
        var env = EnvFile.Parse(text);
        env.Set("B", "dos");
        Assert.Equal("A=1\r\nB=dos\nC=3\r\n", env.ToString());
    }

    [Fact]
    public void ValorDeVariasLineas_SeLeeEntero_YSeConserva()
    {
        var text = "ANTES=1\nPRIVATE_KEY=\"-----BEGIN KEY-----\nabc\n-----END KEY-----\"\nDESPUES=2\n";
        var env = EnvFile.Parse(text);
        Assert.Equal("-----BEGIN KEY-----\nabc\n-----END KEY-----", env.Get("PRIVATE_KEY"));
        Assert.Equal("2", env.Get("DESPUES"));
        Assert.Equal(text, env.ToString());

        env.Set("DESPUES", "3");
        Assert.Equal(text.Replace("DESPUES=2", "DESPUES=3"), env.ToString());
    }

    [Fact]
    public void UnaComillaQueNoSeCierra_NoSeComeElResto()
    {
        var text = "ROTA=\"sin cerrar\nOTRA=1\n";
        var env = EnvFile.Parse(text);
        Assert.Equal("1", env.Get("OTRA"));
        Assert.Equal(text, env.ToString());
    }

    [Theory]
    [InlineData("", "")]
    [InlineData("127.0.0.1", "127.0.0.1")]
    [InlineData("https://tienda.test:8445", "https://tienda.test:8445")]
    [InlineData("a=b,c%d@e+f", "a=b,c%d@e+f")]
    [InlineData("con espacio", "\"con espacio\"")]
    [InlineData("pa#ss", "\"pa#ss\"")]
    [InlineData("mysql://root@127.0.0.1:3409/api?serverVersion=11.4.13-MariaDB&charset=utf8mb4", "\"mysql://root@127.0.0.1:3409/api?serverVersion=11.4.13-MariaDB&charset=utf8mb4\"")]
    [InlineData("pa$word", "'pa$word'")]
    [InlineData("con\\barra", "'con\\barra'")]
    [InlineData("comilla\"doble", "'comilla\"doble'")]
    [InlineData("it's", "\"it's\"")]
    [InlineData("it's $x", "\"it's \\$x\"")]
    public void Quote_SoloEntrecomillaLoQueHaceFalta(string value, string expected)
    {
        Assert.Equal(expected, EnvFile.Quote(value));
        // Y lo que se escribe se vuelve a leer igual.
        Assert.Equal(value, EnvFile.Parse($"K={EnvFile.Quote(value)}\n").Get("K"));
    }

    [Fact]
    public void Merge_SinPisar_SoloAnadeLoQueFalta_YDiceQueSeQuedaLoSuyo()
    {
        var env = EnvFile.Parse("DB_CONNECTION=sqlite\nDB_DATABASE=la_mia\nDB_PASSWORD=\n# DB_HOST=localhost\n");
        var changes = env.Merge(
        [
            new("DB_CONNECTION", "mysql"),
            new("DB_HOST", "127.0.0.1"),
            new("DB_DATABASE", "tienda"),
            new("DB_PASSWORD", ""),
            new("APP_URL", "https://tienda.test"),
        ], overwrite: false);

        Assert.Equal(
        [
            ("DB_CONNECTION", EnvChange.Kept),
            ("DB_HOST", EnvChange.Added),
            ("DB_DATABASE", EnvChange.Kept),
            ("DB_PASSWORD", EnvChange.Same),
            ("APP_URL", EnvChange.Added),
        ], changes);
        Assert.Equal("sqlite", env.Get("DB_CONNECTION"));
        Assert.Equal("la_mia", env.Get("DB_DATABASE"));
        Assert.Equal("127.0.0.1", env.Get("DB_HOST"));   // estaba comentada: cuenta como que no está
    }

    [Fact]
    public void Merge_Pisando_PoneLoDeRoostEncimaDelEjemplo()
    {
        var (text, changes) = EnvFile.Apply(Lf(LaravelExample),
        [
            new("APP_URL", "https://tienda.test:8445"),
            new("DB_CONNECTION", "mysql"),
            new("REDIS_CLIENT", "predis"),
            new("MAIL_MAILER", "smtp"),
        ], overwrite: true);

        Assert.All(changes, c => Assert.Equal(EnvChange.Changed, c.Change));
        var env = EnvFile.Parse(text);
        Assert.Equal("https://tienda.test:8445", env.Get("APP_URL"));
        Assert.Equal("mysql", env.Get("DB_CONNECTION"));
        Assert.Equal("predis", env.Get("REDIS_CLIENT"));
        Assert.Equal("smtp", env.Get("MAIL_MAILER"));
        Assert.Equal("Laravel", env.Get("APP_NAME"));   // lo demás, igual
        Assert.Equal(Lf(LaravelExample).Split('\n').Length, text.Split('\n').Length);   // ni una línea de más
    }

    [Fact]
    public void Set_EnTodasLasVecesQueAparece()
    {
        var env = EnvFile.Parse("APP_URL=a\nOTRA=x\nAPP_URL=b\n");
        env.Set("APP_URL", "c");
        Assert.Equal("APP_URL=c\nOTRA=x\nAPP_URL=c\n", env.ToString());
    }

    [Fact]
    public void LoQueNoEsUnaAsignacion_SeDejaTalCual()
    {
        var text = "esto no es nada\n=sin clave\n# Esto es una nota=con igual\n  # DB_HOST=sangrado\n123=numero\n";
        var env = EnvFile.Parse(text);
        Assert.Empty(env.Keys);
        Assert.True(env.IsCommented("DB_HOST"));
        Assert.False(env.IsCommented("Esto"));
        Assert.Equal(text, env.ToString());
    }

    [Fact]
    public void Set_RechazaUnaClaveQueNoVale()
    {
        var env = EnvFile.Parse("");
        Assert.Throws<ArgumentException>(() => env.Set("CON ESPACIO", "x"));
        Assert.Throws<ArgumentException>(() => env.Set("1EMPIEZA", "x"));
    }

    [Fact]
    public void LoadYSave_ConservanElBom_YEscribenSinDejarTemporales()
    {
        Directory.CreateDirectory(_root);
        var conBom = Path.Combine(_root, "con-bom.env");
        File.WriteAllBytes(conBom, [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes("APP_NAME=Tienda\r\nAPP_URL=http://localhost\r\n")]);
        var (env, bom) = EnvFile.Load(conBom);
        Assert.True(bom);
        Assert.Equal("Tienda", env.Get("APP_NAME"));   // el BOM no se mete en la primera clave
        env.Set("APP_URL", "https://tienda.test");
        env.Save(conBom, bom);
        var bytes = File.ReadAllBytes(conBom);
        Assert.Equal([0xEF, 0xBB, 0xBF], bytes[..3]);
        Assert.Equal("APP_NAME=Tienda\r\nAPP_URL=https://tienda.test\r\n", Encoding.UTF8.GetString(bytes[3..]));

        var sinBom = Path.Combine(_root, "sin-bom.env");
        File.WriteAllText(sinBom, "A=1\n", new UTF8Encoding(false));
        var (plain, noBom) = EnvFile.Load(sinBom);
        Assert.False(noBom);
        plain.Set("A_B", "dos");
        plain.Save(sinBom, noBom);
        Assert.Equal("A=1\n\nA_B=dos\n", File.ReadAllText(sinBom));   // otro grupo: separado
        Assert.NotEqual(0xEF, File.ReadAllBytes(sinBom)[0]);
        Assert.Equal(2, Directory.GetFiles(_root).Length);   // sin .roost-tmp
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }
}
