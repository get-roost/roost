using Roost.Core.Git;
using Roost.Core.Platform;

namespace Roost.Core.Tests;

/// <summary>El entorno aislado de git: su gitconfig propio, sin prompts y en ingles.</summary>
public class GitEnvironmentTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "roost-gitenv-" + Guid.NewGuid().ToString("N"));
    private RoostPaths Paths => new(_root, Path.Combine(_root, "cache"));

    [Fact]
    public void Build_EscribeElGitconfigDeRoost_YAislaDelDelUsuario()
    {
        var env = new GitEnvironment(Paths);

        var variables = env.Build();

        Assert.Equal(Path.Combine(Paths.GitState, "gitconfig"), env.ConfigFile);
        Assert.Equal(env.ConfigFile, variables["GIT_CONFIG_GLOBAL"]);
        Assert.Equal("1", variables["GIT_CONFIG_NOSYSTEM"]);
        Assert.Equal("0", variables["GIT_TERMINAL_PROMPT"]);
        Assert.Equal("C", variables["LC_ALL"]);
        Assert.Equal("never", variables["GCM_INTERACTIVE"]);
        // Las que se quitan del entorno heredado van con null.
        Assert.True(variables.ContainsKey("GIT_ASKPASS"));
        Assert.Null(variables["GIT_ASKPASS"]);
        Assert.Null(variables["GIT_DIR"]);
        Assert.Equal(GitEnvironment.DefaultSshCommand, variables["GIT_SSH_COMMAND"]);   // nunca una pregunta sin contestar

        Assert.Equal("never", variables["SSH_ASKPASS_REQUIRE"]);

        var config = File.ReadAllText(env.ConfigFile);
        Assert.Contains("autocrlf = false", config);
        Assert.Contains("useHttpPath = true", config);
        Assert.Contains("ff = only", config);
        if (OperatingSystem.IsWindows())
        {
            Assert.Contains("longpaths = true", config);
            Assert.Contains("sslBackend = schannel", config);
        }
        else
        {
            // Nada de Schannel ni de Windows: libcurl con la CA del sistema, y los enlaces como enlaces.
            Assert.DoesNotContain("schannel", config);
            Assert.DoesNotContain("longpaths", config);
            Assert.DoesNotContain("symlinks", config);
            if (GitEnvironment.SystemCaBundle() is { } bundle) Assert.Contains($"sslCAInfo = {bundle}", config);
        }
    }

    [Fact]
    public void Template_PorSistema_YElBundleDeCaDeLaDistro()
    {
        var windows = GitEnvironment.Template(windows: true, caBundle: null);
        Assert.Contains("sslBackend = schannel", windows);
        Assert.Contains("fscache = true", windows);
        Assert.DoesNotContain("sslCAInfo", windows);

        var linux = GitEnvironment.Template(windows: false, caBundle: "/etc/ssl/certs/ca-certificates.crt");
        Assert.DoesNotContain("schannel", linux);
        Assert.Contains("[http]\n\tsslCAInfo = /etc/ssl/certs/ca-certificates.crt\n", linux);
        Assert.Contains("[credential]\n\tuseHttpPath = true\n\thelper =\n", linux);
        Assert.DoesNotContain("\r", linux);

        var bare = GitEnvironment.Template(windows: false, caBundle: null);
        Assert.DoesNotContain("[http]", bare);

        // El bundle: el primero que exista de los de cada distro; sin ninguno, null.
        Assert.Equal("/etc/pki/tls/certs/ca-bundle.crt", GitEnvironment.SystemCaBundle(p => p == "/etc/pki/tls/certs/ca-bundle.crt"));
        Assert.Null(GitEnvironment.SystemCaBundle(_ => false));
    }

    [Fact]
    public void Build_RegeneraElGitconfigSiLoCambiaron()
    {
        var env = new GitEnvironment(Paths);
        env.Build();
        File.WriteAllText(env.ConfigFile, "[core]\n\tautocrlf = true\n");

        env.Build();

        Assert.Equal(GitEnvironment.ConfigTemplate, File.ReadAllText(env.ConfigFile));
    }

    [Fact]
    public void Build_LaConfiguracionPorInvocacionVaEnGitConfigCount()
    {
        var variables = new GitEnvironment(Paths).Build([("credential.helper", ""), ("credential.helper", "roost git-credential")], sshCommand: "ssh -i clave");

        Assert.Equal("2", variables["GIT_CONFIG_COUNT"]);
        Assert.Equal("credential.helper", variables["GIT_CONFIG_KEY_0"]);
        Assert.Equal("", variables["GIT_CONFIG_VALUE_0"]);
        Assert.Equal("roost git-credential", variables["GIT_CONFIG_VALUE_1"]);
        Assert.Equal("ssh -i clave", variables["GIT_SSH_COMMAND"]);
    }

    [Fact]
    public void Build_VariasOrdenesALaVez_NoSePisanAlEscribirElGitconfig()
    {
        // La app pone al día el git de un sitio mientras mira las novedades de otro: las dos primeras
        // órdenes de un ROOST_HOME nuevo escriben el gitconfig a la vez. Ninguna puede fallar por eso.
        for (var round = 0; round < 20; round++)
        {
            var paths = new RoostPaths(Path.Combine(_root, round.ToString(System.Globalization.CultureInfo.InvariantCulture)), Path.Combine(_root, "cache"));
            var errors = new System.Collections.Concurrent.ConcurrentBag<Exception>();
            using var start = new ManualResetEventSlim();
            var threads = Enumerable.Range(0, 8).Select(_ => new Thread(() =>
            {
                start.Wait();
                try { new GitEnvironment(paths).Build(); }
                catch (Exception ex) { errors.Add(ex); }
            })).ToList();
            threads.ForEach(t => t.Start());
            start.Set();
            threads.ForEach(t => t.Join());

            Assert.Empty(errors);
            var env = new GitEnvironment(paths);
            Assert.Equal(GitEnvironment.ConfigTemplate, File.ReadAllText(env.ConfigFile));
            Assert.Empty(Directory.EnumerateFiles(paths.GitState, "*.tmp"));
        }
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }
}
