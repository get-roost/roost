using Roost.Core.Install;

namespace Roost.Core.Tests;

/// <summary>
/// Testea el parseo del releases.json de windows.php.net y la elección de build, con un
/// recorte del fichero real del 22-oct-2026 (sin red).
/// </summary>
public class WindowsPhpCatalogTests
{
    private static readonly Uri Base = new("https://downloads.php.net/~windows/releases/releases.json");

    // Recorte real: dos ramas, variantes NTS/TS y entradas que no son zips de PHP (source, test_pack, debug_pack).
    private const string Json = """
        {
          "8.3": {
            "version": "8.3.36",
            "nts-vs16-x64": {
              "mtime": "2026-10-21T13:49:08+00:00",
              "zip": { "path": "php-8.3.36-nts-Win32-vs16-x64.zip", "size": "32.52MB",
                       "sha256": "3C9242622C7402B47C38D2EC2ED779CEF3A290FD6F2BDE6C35F9C482699E7900" },
              "debug_pack": { "path": "php-debug-pack-8.3.36-nts-Win32-vs16-x64.zip", "size": "31.02MB",
                              "sha256": "fb440abefce2ba87119dce55f7c4d6f3f709765624dd5e1c3b1365af1605b983" }
            },
            "ts-vs16-x64": {
              "zip": { "path": "php-8.3.36-Win32-vs16-x64.zip", "size": "32.6MB",
                       "sha256": "1111111111111111111111111111111111111111111111111111111111111111" }
            },
            "source": { "path": "php-8.3.36-src.zip", "size": "25MB",
                        "sha256": "2222222222222222222222222222222222222222222222222222222222222222" },
            "test_pack": { "path": "php-test-pack-8.3.36.zip", "size": "15MB",
                           "sha256": "3333333333333333333333333333333333333333333333333333333333333333" }
          },
          "8.4": {
            "version": "8.4.27",
            "nts-vs17-x64": {
              "zip": { "path": "php-8.4.27-nts-Win32-vs17-x64.zip", "size": "33MB",
                       "sha256": "4444444444444444444444444444444444444444444444444444444444444444" }
            }
          }
        }
        """;

    private static IReadOnlyList<PhpBuild> Catalog() => WindowsPhpCatalog.Parse(Json, Base);

    [Fact]
    public void Parse_LeeVersionVarianteUrlYSha()
    {
        var nts = Assert.Single(Catalog(), b => b.Branch == "8.3" && !b.ThreadSafe);

        Assert.Equal("8.3.36", nts.Version.ToString());
        Assert.Equal("vs16", nts.Compiler);
        Assert.Equal("x64", nts.Architecture);
        Assert.Equal("8.3.36-nts-x64", nts.InstallName);
        Assert.Equal("https://downloads.php.net/~windows/releases/php-8.3.36-nts-Win32-vs16-x64.zip", nts.Url.ToString());
        Assert.Equal("3c9242622c7402b47c38d2ec2ed779cef3a290fd6f2bde6c35f9c482699e7900", nts.Sha256); // a minusculas
    }

    [Fact]
    public void Parse_IgnoraFuentesTestPackYDebugPack()
    {
        var builds = Catalog();

        Assert.Equal(3, builds.Count);
        Assert.DoesNotContain(builds, b => b.FileName.Contains("src") || b.FileName.Contains("pack"));
    }

    [Fact]
    public void Resolve_PorRama_DaLaUltimaNtsX64()
    {
        var build = WindowsPhpCatalog.Resolve(Catalog(), "8.3");

        Assert.Equal("php-8.3.36-nts-Win32-vs16-x64.zip", build.FileName);
    }

    [Fact]
    public void Resolve_ConTs_DaElBuildThreadSafe()
    {
        var build = WindowsPhpCatalog.Resolve(Catalog(), "8.3.36", threadSafe: true);

        Assert.True(build.ThreadSafe);
        Assert.Equal("php-8.3.36-Win32-vs16-x64.zip", build.FileName);
    }

    [Fact]
    public void Resolve_VersionQueNoEsLaUltima_FallaDiciendoCualHay()
    {
        var ex = Assert.Throws<RoostException>(() => WindowsPhpCatalog.Resolve(Catalog(), "8.3.18"));

        Assert.Contains("8.3.36", ex.Message);
    }

    [Theory]
    [InlineData("9.9")]      // rama que no esta
    [InlineData("latest")]   // no es una version
    public void Resolve_PeticionImposible_Falla(string spec)
    {
        Assert.Throws<RoostException>(() => WindowsPhpCatalog.Resolve(Catalog(), spec));
    }

    [Fact]
    public void Resolve_VarianteQueNoExiste_Falla()
    {
        // La 8.4 del recorte solo trae NTS x64.
        Assert.Throws<RoostException>(() => WindowsPhpCatalog.Resolve(Catalog(), "8.4", architecture: "x86"));
    }
}
