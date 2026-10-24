using Roost.Core.Sites;

namespace Roost.Core.Tests;

/// <summary>Qué proyecto hay en una carpeta, por sus ficheros, sobre carpetas temporales.</summary>
public class ProjectKindTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "roost-project-" + Guid.NewGuid().ToString("N"));

    private string Project(params string[] files)
    {
        var dir = Path.Combine(_root, Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        foreach (var file in files)
        {
            var path = Path.Combine(dir, file.Replace('/', Path.DirectorySeparatorChar));
            if (file.EndsWith('/'))
            {
                Directory.CreateDirectory(path);
                continue;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "");
        }
        return dir;
    }

    [Fact]
    public void Laravel_PorSuArtisan_ConRaizPublic_YSinVendor()
    {
        var kind = ProjectKind.Detect(Project("artisan", "composer.json", "composer.lock", "public/index.php"));

        Assert.Equal(ProjectType.Laravel, kind.Type);
        Assert.Equal("Laravel", kind.Framework);
        Assert.Equal("public", kind.WebRoot);
        Assert.True(kind.HasComposerLock);
        Assert.True(kind.NeedsComposerInstall);

        Assert.False(ProjectKind.Detect(Project("artisan", "composer.json", "public/", "vendor/")).NeedsComposerInstall);
    }

    [Fact]
    public void Symfony_ConBinConsoleYSymfonyLock()
    {
        var kind = ProjectKind.Detect(Project("bin/console", "symfony.lock", "composer.json", "public/index.php"));
        Assert.Equal(ProjectType.Symfony, kind.Type);
        Assert.Equal("public", kind.WebRoot);

        // Sin symfony.lock, un bin/console cualquiera no hace un Symfony.
        Assert.Equal(ProjectType.Composer, ProjectKind.Detect(Project("bin/console", "composer.json")).Type);
    }

    [Theory]
    [InlineData("wp-config.php")]
    [InlineData("wp-load.php")]
    public void WordPress_SeSirveDesdeSuCarpeta(string file)
    {
        var kind = ProjectKind.Detect(Project(file, "wp-includes/version.php", "public/"));

        Assert.Equal(ProjectType.WordPress, kind.Type);
        Assert.Equal("WordPress", kind.Framework);
        Assert.Null(kind.WebRoot);   // aunque haya una public/: WordPress va en su carpeta
        Assert.False(kind.NeedsComposerInstall);
    }

    [Fact]
    public void Composer_YNada_YUnaCarpetaQueNoExiste()
    {
        var composer = ProjectKind.Detect(Project("composer.json", "public/"));
        Assert.Equal(ProjectType.Composer, composer.Type);
        Assert.Null(composer.Framework);
        Assert.Equal("public", composer.WebRoot);
        Assert.True(composer.NeedsComposerInstall);

        var plain = ProjectKind.Detect(Project("index.php"));
        Assert.Equal(ProjectType.Plain, plain.Type);
        Assert.Null(plain.WebRoot);
        Assert.False(plain.HasComposerJson);

        Assert.Equal(ProjectType.Plain, ProjectKind.Detect(Path.Combine(_root, "no-existe")).Type);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }
}
