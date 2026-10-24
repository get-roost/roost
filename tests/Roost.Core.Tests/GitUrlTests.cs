using Roost.Core.Git;
using Roost.Core.Sites;

namespace Roost.Core.Tests;

/// <summary>GitUrl.Parse (puro): https, ssh, la forma corta, carpetas locales, proveedores, y lo que se rechaza.</summary>
public class GitUrlTests
{
    [Fact]
    public void Parse_HttpsDeGitHub_SacaProveedorNombreYSitio()
    {
        var url = GitUrl.Parse("https://github.com/octocat/Hello-World.git");

        Assert.Equal(GitScheme.Https, url.Scheme);
        Assert.Equal("github.com", url.Host);
        Assert.Null(url.Port);
        Assert.Null(url.User);
        Assert.Equal("octocat/Hello-World.git", url.Path);
        Assert.Equal(GitProvider.GitHub, url.Provider);
        Assert.Equal("GitHub", url.ProviderName);
        Assert.Equal("https://github.com/octocat/Hello-World.git", url.Normalized);
        Assert.Equal("octocat/Hello-World", url.Label);
        Assert.Equal("Hello-World", url.RepoName);
        Assert.Equal("hello-world", url.SuggestedSiteName);
        Assert.False(url.IsLocal);
    }

    [Theory]
    [InlineData("https://github.com/acme/tienda", "https://github.com/acme/tienda", "tienda")]
    [InlineData("HTTPS://GitHub.com/acme/tienda/", "https://github.com/acme/tienda", "tienda")]
    [InlineData("https://github.com/acme/tienda.git/", "https://github.com/acme/tienda.git", "tienda")]
    [InlineData("http://gitea.lan:3000/equipo/Mi_App.v2.git", "http://gitea.lan:3000/equipo/Mi_App.v2.git", "Mi_App.v2")]
    [InlineData("https://ana@gitlab.com/grupo/sub/proyecto.git", "https://gitlab.com/grupo/sub/proyecto.git", "proyecto")]
    public void Parse_Https_NormalizaSinUsuarioNiBarraFinal(string text, string normalized, string repo)
    {
        var url = GitUrl.Parse(text);

        Assert.Equal(GitScheme.Https, url.Scheme);
        Assert.Equal(normalized, url.Normalized);
        Assert.Equal(repo, url.RepoName);
        Assert.True(Site.IsValidName(url.SuggestedSiteName));
    }

    [Fact]
    public void Parse_HttpsConUsuario_LoGuardaAparte_YSacaElGrupoEnLaEtiqueta()
    {
        var url = GitUrl.Parse("https://ana@gitlab.com/grupo/sub/proyecto.git");

        Assert.Equal("ana", url.User);
        Assert.Equal(GitProvider.GitLab, url.Provider);
        Assert.Equal("grupo/sub/proyecto", url.Label);
    }

    [Fact]
    public void Parse_FormaCorta_EsSshConElUsuarioDentro()
    {
        var url = GitUrl.Parse("git@github.com:acme/tienda.git");

        Assert.Equal(GitScheme.Ssh, url.Scheme);
        Assert.Equal("git", url.User);
        Assert.Equal("github.com", url.Host);
        Assert.Equal("acme/tienda.git", url.Path);
        Assert.Equal("git@github.com:acme/tienda.git", url.Normalized);
        Assert.Equal(GitProvider.GitHub, url.Provider);
        Assert.Equal("acme/tienda", url.Label);
        Assert.Equal("tienda", url.SuggestedSiteName);
    }

    [Fact]
    public void Parse_SshConPuerto_LoConserva()
    {
        var url = GitUrl.Parse("ssh://git@git.acme.es:2222/grupo/tienda.git");

        Assert.Equal(GitScheme.Ssh, url.Scheme);
        Assert.Equal(2222, url.Port);
        Assert.Equal("git.acme.es", url.Host);
        Assert.Equal(GitProvider.Other, url.Provider);
        Assert.Equal("git.acme.es", url.ProviderName);
        Assert.Equal("ssh://git@git.acme.es:2222/grupo/tienda.git", url.Normalized);
    }

    [Theory]
    [InlineData("git@bitbucket.org:equipo/repo.git", GitProvider.Bitbucket)]
    [InlineData("https://codeberg.org/x/y", GitProvider.Codeberg)]
    [InlineData("https://dev.azure.com/org/proj/_git/repo", GitProvider.AzureDevOps)]
    [InlineData("https://org.visualstudio.com/proj/_git/repo", GitProvider.AzureDevOps)]
    [InlineData("https://gitlab.com/a/b.git", GitProvider.GitLab)]
    [InlineData("https://git.acme.es/a/b.git", GitProvider.Other)]
    public void Parse_ReconoceElProveedorPorElHost(string text, GitProvider provider)
    {
        var url = GitUrl.Parse(text);

        Assert.Equal(provider, url.Provider);
        Assert.Equal("repo", GitUrl.Parse("https://dev.azure.com/org/proj/_git/repo").RepoName);
    }

    [Fact]
    public void Parse_CarpetaLocal_YFileUrl_DanLoMismo()
    {
        var folder = OperatingSystem.IsWindows() ? @"C:\repos\Tienda.git" : "/srv/git/Tienda.git";
        var fileUrl = OperatingSystem.IsWindows() ? "file:///C:/repos/Tienda.git" : "file:///srv/git/Tienda.git";

        var local = GitUrl.Parse(folder);
        var viaFile = GitUrl.Parse(fileUrl);

        Assert.Equal(GitScheme.Local, local.Scheme);
        Assert.True(local.IsLocal);
        Assert.Null(local.Host);
        Assert.Equal(GitProvider.Local, local.Provider);
        Assert.Equal(Path.GetFullPath(folder), local.Normalized);
        Assert.Equal("Tienda", local.RepoName);
        Assert.Equal("tienda", local.SuggestedSiteName);
        Assert.Equal(local.Normalized, viaFile.Normalized);
        Assert.Equal(GitScheme.Local, viaFile.Scheme);
    }

    [Fact]
    public void Parse_UnaCarpetaConBarraFinalODotGit_SacaElNombreBueno()
    {
        var folder = OperatingSystem.IsWindows() ? @"C:\repos\tienda\.git" : "/srv/git/tienda/.git";

        Assert.Equal("tienda", GitUrl.Parse(folder).RepoName);
        Assert.Equal("tienda", GitUrl.Parse(folder.TrimEnd('/', '\\') + Path.DirectorySeparatorChar).RepoName);
    }

    [Theory]
    [InlineData("https://ana:secreto@github.com/acme/tienda.git")]
    [InlineData("ana:secreto@github.com:acme/tienda.git")]
    [InlineData("ssh://git:clave@servidor/x.git")]
    public void Parse_ConUsuarioYContrasenaDentro_LoRechazaYDicePorQue(string text)
    {
        var ex = Assert.Throws<RoostException>(() => GitUrl.Parse(text));

        Assert.Equal(GitUrl.CredentialsInsideMessage, ex.Message);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("tienda")]
    [InlineData("acme/tienda")]
    [InlineData("https://github.com")]
    [InlineData("https://github.com/")]
    [InlineData("https://")]
    [InlineData("ftp://servidor/repo.git")]
    [InlineData("github.com:acme/tienda.git")]
    public void Parse_LoQueNoEsUnaDireccion_LoRechaza(string text)
    {
        var ex = Assert.Throws<RoostException>(() => GitUrl.Parse(text));

        Assert.Equal(GitUrl.InvalidMessage, ex.Message);
        Assert.False(GitUrl.TryParse(text, out _, out var error));
        Assert.Equal(GitUrl.InvalidMessage, error);
    }

    [Fact]
    public void Parse_GitProtocolo_LoRechazaConSuMotivo()
    {
        var ex = Assert.Throws<RoostException>(() => GitUrl.Parse("git://github.com/acme/tienda.git"));

        Assert.Equal(GitUrl.GitProtocolMessage, ex.Message);
    }

    [Theory]
    [InlineData("Hello-World", "hello-world")]
    [InlineData("Mi_App.v2", "mi-app-v2")]
    [InlineData("--raro--", "raro")]
    [InlineData("___", "sitio")]
    [InlineData("ÑandúTienda", "and-tienda")]
    public void SiteNameFor_DaUnaEtiquetaDnsValida(string repo, string expected)
    {
        var name = GitUrl.SiteNameFor(repo);

        Assert.Equal(expected, name);
        Assert.True(Site.IsValidName(name));
    }

    [Fact]
    public void SiteNameFor_RecortaA63()
    {
        var name = GitUrl.SiteNameFor(new string('a', 70) + "-b");

        Assert.Equal(63, name.Length);
        Assert.True(Site.IsValidName(name));
    }

    [WindowsTheory("Pendiente en Linux: Terminal y herramientas (L2).")]
    [InlineData("git@github.com:acme/tienda.git", "https://github.com/acme/tienda/settings/keys")]
    [InlineData("https://gitlab.com/acme/web/tienda", "https://gitlab.com/acme/web/tienda/-/settings/repository#js-deploy-keys-settings")]
    [InlineData("git@bitbucket.org:acme/tienda.git", "https://bitbucket.org/acme/tienda/admin/access-keys/")]
    [InlineData("git@git.acme.es:web/tienda.git", null)]
    [InlineData(@"C:\repos\tienda.git", null)]
    public void DeployKeysUrl_LaPaginaDeEseRepositorio(string text, string? expected)
    {
        Assert.Equal(expected, GitUrl.Parse(text).DeployKeysUrl);
    }

    [Fact]
    public void NewTokenUrl_ConElPermisoJusto()
    {
        var github = GitUrl.Parse("https://github.com/acme/tienda");
        var gitlab = GitUrl.Parse("https://gitlab.com/acme/tienda");

        Assert.Equal("https://github.com/settings/personal-access-tokens/new", github.NewTokenUrl);
        Assert.Equal("Contents: Read", github.TokenPermission);
        Assert.Contains("scopes=read_repository", gitlab.NewTokenUrl);
        Assert.Null(GitUrl.Parse("https://git.acme.es/web/tienda").NewTokenUrl);
        Assert.Equal("acme", github.Owner);
    }

    [Fact]
    public void ToSshYToHttps_SoloConProveedoresConocidos()
    {
        Assert.Equal("git@github.com:acme/tienda.git", GitUrl.Parse("https://github.com/acme/tienda").ToSsh()!.Normalized);
        Assert.Equal("https://gitlab.com/acme/web/tienda.git", GitUrl.Parse("git@gitlab.com:acme/web/tienda.git").ToHttps()!.Normalized);
        Assert.Null(GitUrl.Parse("https://git.acme.es/web/tienda").ToSsh());
        Assert.Null(GitUrl.Parse("ssh://git@github.com:2222/acme/tienda.git").ToHttps());   // con su puerto, no se adivina
        var ssh = GitUrl.Parse("git@github.com:acme/tienda.git");
        Assert.Same(ssh, ssh.ToSsh());
    }
}
