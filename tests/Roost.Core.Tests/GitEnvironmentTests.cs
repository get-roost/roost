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

        var config = File.ReadAllText(env.ConfigFile);
        Assert.Contains("longpaths = true", config);
        Assert.Contains("autocrlf = false", config);
        Assert.Contains("sslBackend = schannel", config);
        Assert.Contains("useHttpPath = true", config);
        Assert.Contains("ff = only", config);
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

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }
}
