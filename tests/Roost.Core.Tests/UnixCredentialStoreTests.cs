using Roost.Core.Git;
using Roost.Core.Platform;

namespace Roost.Core.Tests;

/// <summary>
/// Los tokens de git en Linux: el fichero 0600 (<see cref="FileCredentialStore"/>, sobre una raíz temporal)
/// y el Secret Service por <c>secret-tool</c> (<see cref="SecretServiceCredentialStore"/>, con un runner de
/// pega: nunca se habla con un llavero de verdad). Nada toca el almacén del usuario.
/// </summary>
public class UnixCredentialStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "roost-credfile-" + Guid.NewGuid().ToString("N"));
    private RoostPaths Paths => new(_root, Path.Combine(_root, "cache"));

    [Fact]
    public void Fichero_GuardaLeeListaYBorra_YSinTokensNoDejaFichero()
    {
        var store = new FileCredentialStore(Paths);
        var credentials = new GitCredentials(store, "Roost-test:git:");

        Assert.Null(store.Read("Roost-test:git:https://github.com/acme/tienda"));
        Assert.Empty(credentials.List());

        var target = credentials.Save(CredentialLocation.Create("https", "github.com", "acme/tienda"), "ghp_xxx");
        credentials.Save(CredentialLocation.Create("https", "gitlab.com", null), "glpat-yyy", "deploy");

        Assert.Equal(Path.Combine(Paths.GitState, "credentials.json"), store.File);
        Assert.True(File.Exists(store.File));
        var read = store.Read(target)!;
        Assert.Equal("ghp_xxx", read.Secret);
        Assert.Equal("x-access-token", read.UserName);
        Assert.Equal("ghp_xxx", credentials.Find(GitUrl.Parse("https://github.com/acme/tienda.git"))!.Secret);
        Assert.Equal(2, credentials.List().Count);
        Assert.Equal("deploy", credentials.List().Single(c => c.Target.Contains("gitlab")).UserName);
        Assert.DoesNotContain("ghp_xxx", store.Read("Roost-test:git:https://gitlab.com")!.Secret);

        // Sustituir: el mismo target, otro secreto.
        credentials.Save(CredentialLocation.Create("https", "github.com", "acme/tienda"), "ghp_zzz");
        Assert.Equal("ghp_zzz", store.Read(target)!.Secret);
        Assert.Equal(2, credentials.List().Count);

        Assert.True(store.Delete(target));
        Assert.False(store.Delete(target));
        Assert.Null(store.Read(target));
        Assert.True(store.Delete("Roost-test:git:https://gitlab.com"));
        Assert.False(File.Exists(store.File));   // sin tokens, sin fichero
    }

    [LinuxFact]
    public void Fichero_Nace0600_YSuCarpeta0700()
    {
        var store = new FileCredentialStore(Paths);
        new GitCredentials(store, "Roost-test:git:").Save(CredentialLocation.Create("https", "github.com", "acme/tienda"), "ghp_xxx");

        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(store.File));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(Path.GetDirectoryName(store.File)!));
    }

    [Fact]
    public void Fichero_RotoLoDice()
    {
        var store = new FileCredentialStore(Paths);
        Directory.CreateDirectory(Path.GetDirectoryName(store.File)!);
        File.WriteAllText(store.File, "{ esto no es");

        var ex = Assert.Throws<RoostException>(() => store.Read("x"));
        Assert.Contains("credentials.json", ex.Message);
    }

    [Fact]
    public void SecretTool_GuardaPorStdin_BuscaPorAtributos_YLeeLoViejoDelFichero()
    {
        var runner = new FakeCommandRunner();
        string? stored = null;
        runner.On("store", onRun: spec => stored = spec.Stdin)
            .On("lookup roost-app roost roost-target Roost:git:https://github.com/acme/tienda", stdout: "ghp_xxx\n")
            .On("lookup", exitCode: 1)
            .On("search", stdout: """
                [/org/freedesktop/secrets/collection/login/12]
                label = Roost: token de git de Roost:git:https://github.com/acme/tienda
                secret = ghp_xxx
                created = 2026-10-25 10:00:00
                modified = 1761386400
                schema = org.freedesktop.Secret.Generic
                attribute.roost-app = roost
                attribute.roost-target = Roost:git:https://github.com/acme/tienda
                attribute.roost-user = x-access-token

                """)
            .On("clear");
        var file = new FileCredentialStore(Paths);
        file.Write(new StoredCredential("Roost:git:https://gitlab.com", "oauth2", "glpat-viejo"));
        var store = new SecretServiceCredentialStore(runner, "/usr/bin/secret-tool", fallback: file);

        store.Write(new StoredCredential("Roost:git:https://github.com/acme/tienda", "x-access-token", "ghp_xxx"));

        Assert.Equal("ghp_xxx", stored);   // por stdin, nunca en los argumentos
        var call = runner.Calls.First(c => c.Arguments.Contains("store"));
        Assert.DoesNotContain("ghp_xxx", string.Join(' ', call.Arguments));
        Assert.Contains("roost-target", call.Arguments);
        Assert.Contains("roost-user", call.Arguments);

        var read = store.Read("Roost:git:https://github.com/acme/tienda")!;
        Assert.Equal("ghp_xxx", read.Secret);
        Assert.Equal("x-access-token", read.UserName);

        // Lo de antes del llavero, en el fichero, se sigue leyendo y sale en la lista.
        Assert.Equal("glpat-viejo", store.Read("Roost:git:https://gitlab.com")!.Secret);
        var list = store.List("Roost:git:");
        Assert.Equal(2, list.Count);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1761386400), list.Single(c => c.Target.Contains("github")).LastWritten);

        Assert.True(store.Delete("Roost:git:https://github.com/acme/tienda"));
        Assert.Contains(runner.Commands, c => c.StartsWith("clear roost-app roost roost-target Roost:git:https://github.com/acme/tienda", StringComparison.Ordinal));
        Assert.True(store.Delete("Roost:git:https://gitlab.com"));   // el del fichero
        Assert.Null(file.Read("Roost:git:https://gitlab.com"));
    }

    [Fact]
    public void SecretTool_ParseSearch_IgnoraElSecreto_YVariosBloques()
    {
        var found = SecretServiceCredentialStore.ParseSearch("[/a/1]\nlabel = x\nsecret = s1\nattribute.roost-target = T1\nattribute.roost-user = \n[/a/2]\nsecret = s2\nmodified = 10\nattribute.roost-target = T2\nattribute.roost-user = u2\n");

        Assert.Equal(2, found.Count);
        Assert.Equal("T1", found[0].Target);
        Assert.Null(found[0].User);
        Assert.Null(found[0].Modified);
        Assert.Equal("u2", found[1].User);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(10), found[1].Modified);
        Assert.Empty(SecretServiceCredentialStore.ParseSearch(""));
    }

    [Fact]
    public void SecretTool_Vivo_SoloConBusDeSesion_YSiLaBusquedaContesta()
    {
        var ok = new FakeCommandRunner().On("search", exitCode: 0);
        var fails = new FakeCommandRunner().On("search", exitCode: 1, stderr: "secret-tool: Cannot autolaunch D-Bus without X11 $DISPLAY\n");

        Assert.True(SecretServiceCredentialStore.IsAlive(ok, "/usr/bin/secret-tool", name => name == "DBUS_SESSION_BUS_ADDRESS" ? "unix:path=/run/user/1000/bus" : null));
        Assert.False(SecretServiceCredentialStore.IsAlive(fails, "/usr/bin/secret-tool", name => name == "DBUS_SESSION_BUS_ADDRESS" ? "unix:path=/run/user/1000/bus" : null));
        Assert.False(SecretServiceCredentialStore.IsAlive(ok, "/usr/bin/secret-tool", _ => null));   // sin bus: ni se intenta
        Assert.Empty(ok.Calls.Where(c => c.Arguments.Contains("search")).Skip(1));
    }

    [Fact]
    public void Describe_DiceDondeVa_YSiNoVaCifrado()
    {
        var file = new FileCredentialStore(Paths);
        Assert.Contains("sin cifrar", CredentialStores.Describe(file));
        Assert.Contains(file.File, CredentialStores.Describe(file));
        Assert.True(CredentialStores.IsUnencrypted(file));
        Assert.Contains("llavero", CredentialStores.Describe(new SecretServiceCredentialStore(new FakeCommandRunner(), "secret-tool")));
        Assert.False(CredentialStores.IsUnencrypted(NoCredentialStore.Instance));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
