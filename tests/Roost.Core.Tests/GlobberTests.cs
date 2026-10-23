using Roost.Core.Platform;

namespace Roost.Core.Tests;

/// <summary>
/// El globber sustituye al <c>glob.glob</c> del script Python. Debe encontrar ficheros
/// a través de comodines y, sobre todo, NUNCA lanzar ante rutas que no existen.
/// </summary>
public class GlobberTests : IDisposable
{
    private readonly string _root;

    public GlobberTests()
    {
        // Estructura temporal estilo Laragon: <root>/bin/php/<version>/php(.exe)
        _root = Path.Combine(Path.GetTempPath(), "roost-glob-" + Guid.NewGuid().ToString("N"));
        var exe = OperatingSystem.IsWindows() ? "php.exe" : "php";
        var dir = Path.Combine(_root, "bin", "php", "php-8.3.14");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, exe), string.Empty);
    }

    [Fact]
    public void Expand_EncuentraElFicheroConComodinEnElMedio()
    {
        var exe = OperatingSystem.IsWindows() ? "php.exe" : "php";
        var patron = Path.Combine(_root, "bin", "php", "*", exe);

        var encontrados = Globber.Expand(patron).ToList();

        Assert.Single(encontrados);
        Assert.EndsWith(exe, encontrados[0]);
    }

    [Fact]
    public void Expand_RutaInexistente_DevuelveVacioSinLanzar()
    {
        var patron = Path.Combine(_root, "no", "existe", "*", "php.exe");
        Assert.Empty(Globber.Expand(patron));
    }

    [Fact]
    public void Expand_PatronVacio_DevuelveVacio()
    {
        Assert.Empty(Globber.Expand(""));
        Assert.Empty(Globber.Expand("   "));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }
}
