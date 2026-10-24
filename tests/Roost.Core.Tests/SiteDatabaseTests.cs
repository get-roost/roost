using Roost.Core.Platform;
using Roost.Core.Sites;
using Roost.Core.Stack;

namespace Roost.Core.Tests;

/// <summary>La base de datos de un sitio: el nombre que sale del suyo y la que se fija a mano.</summary>
public class SiteDatabaseTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "roost-sitedb-" + Guid.NewGuid().ToString("N"));
    private RoostPaths Paths => new(_root, Path.Combine(_root, "cache"), WwwDir: Path.Combine(_root, "www"));

    [Theory]
    [InlineData("tienda", "tienda")]
    [InlineData("mi-tienda", "mi_tienda")]
    [InlineData(" Blog-2 ", "blog_2")]
    [InlineData("a.b c", "a_b_c")]
    [InlineData("", "db")]
    public void NameFor_SaneaANombresQueValen(string site, string expected)
    {
        var name = DbAdmin.NameFor(site);
        Assert.Equal(expected, name);
        Assert.True(DbAdmin.IsValidName(name));
    }

    [Fact]
    public void NameFor_CortaA64()
    {
        Assert.Equal(64, DbAdmin.NameFor(new string('x', 70)).Length);
    }

    [Fact]
    public void SetDatabase_SeGuarda_LaConservaAdd_YNullVuelveALaDelNombre()
    {
        var sites = new SiteManager(Paths);
        sites.Add("mi-blog");
        Assert.Equal("mi_blog", SiteManager.DatabaseOf(sites.Find("mi-blog")!));

        sites.SetDatabase("mi-blog.test", "wp_blog");
        Assert.Equal("wp_blog", SiteManager.DatabaseOf(sites.Find("mi-blog")!));

        // Repetir el alta (otro PHP, otra raíz…) no la pierde.
        sites.Add("mi-blog", php: "global");
        Assert.Equal("wp_blog", sites.Find("mi-blog")!.Database);

        Assert.Throws<RoostException>(() => sites.SetDatabase("mi-blog", "con-guion"));
        Assert.Throws<RoostException>(() => sites.SetDatabase("mi-blog", "mysql"));
        Assert.Throws<RoostException>(() => sites.SetDatabase("no-existe", "x"));

        sites.SetDatabase("mi-blog", null);
        Assert.Null(sites.Find("mi-blog")!.Database);
        Assert.Equal("mi_blog", SiteManager.DatabaseOf(sites.Find("mi-blog")!));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }
}
