using Roost.Core.Git;

namespace Roost.Core.Tests;

/// <summary>
/// Los tokens de git: los targets (Roost:git:https://servidor/ruta), la busqueda del mas concreto y
/// el protocolo del helper roost git-credential, con entradas y salidas de texto. Siempre con el
/// almacen en memoria: nunca el Administrador de credenciales de verdad.
/// </summary>
public class GitCredentialsTests
{
    private readonly InMemoryCredentialStore _store = new();
    private GitCredentials Credentials => new(_store, GitCredentials.ProductionPrefix);

    private static string Run(GitCredentials credentials, string action, string input)
    {
        var output = new StringWriter { NewLine = "\n" };
        Assert.Equal(0, GitCredentialProtocol.Run(action, new StringReader(input), output, credentials));
        return output.ToString();
    }

    [Theory]
    [InlineData("github.com/acme/tienda", "https://github.com/acme/tienda")]
    [InlineData("https://GitHub.com/acme/tienda.git", "https://github.com/acme/tienda")]
    [InlineData("github.com/acme/tienda/", "https://github.com/acme/tienda")]
    [InlineData("github.com", "https://github.com")]
    [InlineData("https://git.acme.es:8443/web/tienda.git", "https://git.acme.es:8443/web/tienda")]
    [InlineData("http://localhost:3000/ana/tienda", "http://localhost:3000/ana/tienda")]
    public void Location_ComoLoEscribeElUsuario(string text, string key)
    {
        Assert.True(CredentialLocation.TryParse(text, out var location, out var error), error);
        Assert.Equal(key, location.Key);
    }

    [Theory]
    [InlineData("http://git.acme.es/web/tienda", "sin cifrar")]
    [InlineData("https://ana:secreto@github.com/acme/tienda", "Quita el usuario y la contrasena")]
    [InlineData("ftp://github.com/acme", "no vale")]
    public void Location_LoQueNoVale(string text, string why)
    {
        Assert.False(CredentialLocation.TryParse(text, out _, out var error));
        Assert.Contains(why, error);
    }

    [Fact]
    public void TargetYCandidatos_DelRepositorioAlServidor()
    {
        var url = GitUrl.Parse("https://github.com/acme/tienda.git");
        var location = CredentialLocation.For(url)!;

        Assert.Equal("Roost:git:https://github.com/acme/tienda", Credentials.TargetOf(location));
        Assert.Equal(["Roost:git:https://github.com/acme/tienda", "Roost:git:https://github.com/acme", "Roost:git:https://github.com"], Credentials.Candidates(location));
        Assert.Null(CredentialLocation.For(GitUrl.Parse("git@github.com:acme/tienda.git")));   // por SSH no hay token
    }

    [Fact]
    public void Find_GanaElMasConcreto()
    {
        var credentials = Credentials;
        credentials.Save(CredentialLocation.Create("https", "github.com", null), "ghp_servidor");
        Assert.Equal("ghp_servidor", credentials.Find(GitUrl.Parse("https://github.com/acme/tienda"))!.Secret);

        credentials.Save(CredentialLocation.Create("https", "github.com", "acme/tienda"), "github_pat_tienda");

        Assert.Equal("github_pat_tienda", credentials.Find(GitUrl.Parse("https://github.com/acme/tienda.git"))!.Secret);
        Assert.Equal("ghp_servidor", credentials.Find(GitUrl.Parse("https://github.com/acme/blog"))!.Secret);
    }

    [Fact]
    public void Save_UsuarioPorDefectoSegunElProveedor_YValidaElToken()
    {
        var credentials = Credentials;
        credentials.Save(CredentialLocation.Create("https", "github.com", "a/b"), "ghp_x");
        credentials.Save(CredentialLocation.Create("https", "gitlab.com", "a/b"), "glpat-x");
        credentials.Save(CredentialLocation.Create("https", "bitbucket.org", "a/b"), "ATCTTx");
        credentials.Save(CredentialLocation.Create("https", "git.acme.es", "a/b"), "x", "ana");

        Assert.Equal(["x-token-auth", "ana", "x-access-token", "oauth2"], credentials.List().Select(c => c.UserName));
        Assert.Throws<RoostException>(() => credentials.Save(CredentialLocation.Create("https", "github.com", "a/b"), "ghp_x\nusername=otro"));
        Assert.Throws<RoostException>(() => credentials.Save(CredentialLocation.Create("https", "github.com", "a/b"), "  "));
    }

    [Fact]
    public void List_SoloLosDeSuPrefijo_YNuncaElToken()
    {
        _store.Write(new StoredCredential("git:https://github.com", "ana", "el-de-gcm"));
        _store.Write(new StoredCredential("Roost-test:git:https://github.com/acme/tienda", "x", "el-de-pruebas"));
        Credentials.Save(CredentialLocation.Create("https", "github.com", "acme/tienda"), "ghp_bueno");

        var listed = Credentials.List();

        Assert.Equal(["Roost:git:https://github.com/acme/tienda"], listed.Select(c => c.Target));
        Assert.Equal("Roost:git:https://github.com/acme/tienda (x-access-token)", _store.Read(listed[0].Target)!.ToString());   // ToString sin token
        Assert.Equal("Roost-test:git:", new GitCredentials(_store, GitCredentials.TestPrefix).Prefix);
        Assert.Single(new GitCredentials(_store, GitCredentials.TestPrefix).List());
    }

    [Fact]
    public void Get_ContestaConElUsuarioYElToken()
    {
        Credentials.Save(CredentialLocation.Create("https", "github.com", "acme/tienda"), "github_pat_123");

        // Lo que manda git con credential.useHttpPath=true (y lo que no le hace falta a Roost).
        var output = Run(Credentials, "get", "capability[]=authtype\nprotocol=https\nhost=github.com\npath=acme/tienda.git\nwwwauth[]=Basic realm=\"GitHub\"\n\n");

        Assert.Equal("username=x-access-token\npassword=github_pat_123\n", output);
    }

    [Theory]
    [InlineData("protocol=https\nhost=github.com\npath=acme/otro.git\n\n")]   // otro repositorio
    [InlineData("protocol=http\nhost=github.com\npath=acme/tienda.git\n\n")]   // en claro, no
    [InlineData("protocol=ssh\nhost=github.com\n\n")]
    [InlineData("host=github.com\npath=acme/tienda.git\n\n")]                  // sin protocolo
    [InlineData("")]
    public void Get_SinTokenQueValga_NoContestaNada(string input)
    {
        Credentials.Save(CredentialLocation.Create("https", "github.com", "acme/tienda"), "github_pat_123");

        Assert.Equal("", Run(Credentials, "get", input));
    }

    [Fact]
    public void Get_DeUnServidorLocalPorHttp_Vale()
    {
        Credentials.Save(CredentialLocation.Create("http", "localhost:3000", "ana/tienda"), "tok");

        Assert.Equal("username=roost\npassword=tok\n", Run(Credentials, "get", "protocol=http\nhost=localhost:3000\npath=ana/tienda.git\n\n"));
    }

    [Fact]
    public void Erase_BorraSoloElDelRepositorioExacto_YSoloSiEsElMismo()
    {
        var credentials = Credentials;
        credentials.Save(CredentialLocation.Create("https", "github.com", null), "ghp_servidor");
        credentials.Save(CredentialLocation.Create("https", "github.com", "acme/tienda"), "github_pat_viejo");

        // Otro token (el usuario lo cambio entre medias): no se toca.
        Run(credentials, "erase", "protocol=https\nhost=github.com\npath=acme/tienda.git\nusername=x-access-token\npassword=otro\n\n");
        Assert.NotNull(_store.Read("Roost:git:https://github.com/acme/tienda"));

        Run(credentials, "erase", "protocol=https\nhost=github.com\npath=acme/tienda.git\nusername=x-access-token\npassword=github_pat_viejo\n\n");
        Assert.Null(_store.Read("Roost:git:https://github.com/acme/tienda"));

        // El de todo el servidor puede valer para otros repositorios: un rechazo en uno no lo borra.
        Run(credentials, "erase", "protocol=https\nhost=github.com\npath=acme/blog.git\nusername=x-access-token\npassword=ghp_servidor\n\n");
        Run(credentials, "erase", "protocol=https\nhost=github.com\nusername=x-access-token\npassword=ghp_servidor\n\n");
        Assert.NotNull(_store.Read("Roost:git:https://github.com"));
    }

    [Fact]
    public void Store_NoHaceNada()
    {
        Assert.Equal("", Run(Credentials, "store", "protocol=https\nhost=github.com\npath=acme/tienda.git\nusername=ana\npassword=lo-que-sea\n\n"));
        Assert.Empty(_store.Items);
    }

    [Fact]
    public void AccionDesconocida_Sale1()
    {
        Assert.Equal(1, GitCredentialProtocol.Run("fill", new StringReader(""), new StringWriter(), Credentials));
    }

    [Theory]
    [InlineData("github.com", "mi-contrasena-2024", true)]
    [InlineData("github.com", "github_pat_11ABC", false)]
    [InlineData("gitlab.com", "glpat-xyz", false)]
    [InlineData("git.acme.es", "lo-que-sea", false)]
    public void LooksLikePassword(string host, string token, bool warns)
    {
        Assert.Equal(warns, GitCredentials.LooksLikePassword(host, token) is not null);
    }
}
