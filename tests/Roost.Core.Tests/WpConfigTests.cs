using Roost.Core.Deploy;

namespace Roost.Core.Tests;

/// <summary>El wp-config.php desde el wp-config-sample.php: la base de datos de Roost y las claves nuevas, sin tocar nada más.</summary>
public class WpConfigTests
{
    // El wp-config-sample.php de WordPress 7.1, recortado (los define y lo que va entre medias).
    private const string Sample = """
        <?php
        /**
         * The base configuration for WordPress
         */

        // ** Database settings - You can get this info from your web host ** //
        /** The name of the database for WordPress */
        define( 'DB_NAME', 'database_name_here' );

        /** Database username */
        define( 'DB_USER', 'username_here' );

        /** Database password */
        define( 'DB_PASSWORD', 'password_here' );

        /** Database hostname */
        define( 'DB_HOST', 'localhost' );

        /** Database charset to use in creating database tables. */
        define( 'DB_CHARSET', 'utf8mb4' );

        /** The database collate type. Don't change this if in doubt. */
        define( 'DB_COLLATE', '' );

        /**#@+
         * Authentication unique keys and salts.
         */
        define( 'AUTH_KEY',         'put your unique phrase here' );
        define( 'SECURE_AUTH_KEY',  'put your unique phrase here' );
        define( 'LOGGED_IN_KEY',    'put your unique phrase here' );
        define( 'NONCE_KEY',        'put your unique phrase here' );
        define( 'AUTH_SALT',        'put your unique phrase here' );
        define( 'SECURE_AUTH_SALT', 'put your unique phrase here' );
        define( 'LOGGED_IN_SALT',   'put your unique phrase here' );
        define( 'NONCE_SALT',       'put your unique phrase here' );

        /**#@-*/

        $table_prefix = 'wp_';

        define( 'WP_DEBUG', false );

        /* That's all, stop editing! Happy publishing. */

        /** Absolute path to the WordPress directory. */
        if ( ! defined( 'ABSPATH' ) ) {
        	define( 'ABSPATH', __DIR__ . '/' );
        }

        /** Sets up WordPress vars and included files. */
        require_once ABSPATH . 'wp-settings.php';

        """;

    private static readonly WpDatabase Db = new("blog", "root", "", "127.0.0.1:3409");

    [Fact]
    public void FromSample_PoneLaBaseDeDatos_YDejaLoDemasIgual()
    {
        var config = WpConfig.FromSample(Sample, Db);

        Assert.Equal("blog", WpConfig.Read(config, "DB_NAME"));
        Assert.Equal("root", WpConfig.Read(config, "DB_USER"));
        Assert.Equal("", WpConfig.Read(config, "DB_PASSWORD"));
        Assert.Equal("127.0.0.1:3409", WpConfig.Read(config, "DB_HOST"));
        Assert.Contains("define( 'DB_NAME', 'blog' );", config);   // el define con sus espacios de siempre
        Assert.Contains("define( 'DB_CHARSET', 'utf8mb4' );", config);
        Assert.Contains("$table_prefix = 'wp_';", config);
        Assert.Contains("define( 'WP_DEBUG', false );", config);
        Assert.Contains("require_once ABSPATH . 'wp-settings.php';", config);
        Assert.Equal(Sample.Split('\n').Length, config.Split('\n').Length);
    }

    [Fact]
    public void FromSample_LasOchoSalts_NuevasDistintasYSinLaFraseDelEjemplo()
    {
        var config = WpConfig.FromSample(Sample, Db);

        Assert.DoesNotContain(WpConfig.SamplePhrase, config);
        var salts = WpConfig.SaltNames.Select(n => WpConfig.Read(config, n)!).ToList();
        Assert.Equal(8, salts.Distinct().Count());
        Assert.All(salts, s =>
        {
            Assert.Equal(WpConfig.SaltLength, s.Length);
            Assert.All(s, c => Assert.Contains(c, WpConfig.SaltCharacters));
            Assert.DoesNotContain('\'', s);
            Assert.DoesNotContain('\\', s);
        });
        // Otra vez, otras: nunca las mismas en dos sitios.
        Assert.NotEqual(WpConfig.Read(config, "AUTH_KEY"), WpConfig.Read(WpConfig.FromSample(Sample, Db), "AUTH_KEY"));
    }

    [Fact]
    public void FromSample_ConLasSaltsDadas_UsaEsas_YAlineadasComoEstaban()
    {
        var salts = WpConfig.SaltNames.ToDictionary(n => n, n => n.ToLowerInvariant() + "-salt");
        var config = WpConfig.FromSample(Sample, Db, salts);
        Assert.Contains("define( 'AUTH_KEY',         'auth_key-salt' );", config);
        Assert.Contains("define( 'NONCE_SALT',       'nonce_salt-salt' );", config);
    }

    [Fact]
    public void FromSample_ConCrlfEspaciosRarosYComillasDobles()
    {
        var sample = "<?php\r\ndefine('DB_NAME','x');\r\ndefine (\"DB_USER\" , \"username_here\") ;\r\ndefine(  'DB_PASSWORD',  'password_here'  );\r\ndefine( 'DB_HOST', \"localhost\" );\r\n";
        var config = WpConfig.FromSample(sample, new WpDatabase("tienda", "root", "s3cr$t", "127.0.0.1"));

        Assert.Equal("<?php\r\ndefine('DB_NAME','tienda');\r\ndefine (\"DB_USER\" , 'root') ;\r\ndefine(  'DB_PASSWORD',  's3cr$t'  );\r\ndefine( 'DB_HOST', '127.0.0.1' );\r\n", config);
    }

    [Fact]
    public void FromSample_EscapaComillasYBarras_EnLosValores()
    {
        var config = WpConfig.FromSample(Sample, new WpDatabase("blog", "root", "a'b\\c", "127.0.0.1"));
        Assert.Contains(@"define( 'DB_PASSWORD', 'a\'b\\c' );", config);
        Assert.Equal("a'b\\c", WpConfig.Read(config, "DB_PASSWORD"));
    }

    [Fact]
    public void FromSample_UnValorConComillaEscapadaEnElEjemplo_NoRompeElDefine()
    {
        var sample = "define( 'DB_NAME', 'it\\'s here' );\ndefine( 'DB_USER', 'u' );\ndefine( 'DB_PASSWORD', 'p' );\ndefine( 'DB_HOST', 'h' );\n";
        var config = WpConfig.FromSample(sample, Db);
        Assert.Equal("define( 'DB_NAME', 'blog' );\ndefine( 'DB_USER', 'root' );\ndefine( 'DB_PASSWORD', '' );\ndefine( 'DB_HOST', '127.0.0.1:3409' );\n", config);
    }

    [Fact]
    public void FromSample_SinLosDefinesDeLaBaseDeDatos_NoAdivina()
    {
        var ex = Assert.Throws<RoostException>(() => WpConfig.FromSample("<?php\ndefine( 'DB_NAME', 'x' );\n", Db));
        Assert.Contains("DB_USER, DB_PASSWORD, DB_HOST", ex.Message);
        Assert.Contains("wp-config-sample.php", ex.Message);
    }

    [Fact]
    public void Host_LlevaElPuertoSoloSiNoEsElDeSiempre()
    {
        Assert.Equal("127.0.0.1", WpConfig.Host(3306));
        Assert.Equal("127.0.0.1:3409", WpConfig.Host(3409));
    }

    [Fact]
    public void ElMuPluginDelLoopback_SoloTocaLosPuntoTest_YNuncaApagaLaComprobacion()
    {
        Assert.StartsWith("<?php", WpConfig.LoopbackPlugin);
        Assert.Contains("sslcertificates", WpConfig.LoopbackPlugin);
        Assert.Contains(@"\.test$", WpConfig.LoopbackPlugin);
        Assert.Contains("curl.cainfo", WpConfig.LoopbackPlugin);
        Assert.DoesNotContain("sslverify", WpConfig.LoopbackPlugin);
        Assert.DoesNotContain("https_local_ssl_verify", WpConfig.LoopbackPlugin);
    }
}
