using Roost.Core.Updates;

namespace Roost.Core.Tests;

/// <summary>CHANGELOG.md: lo que enseña Novedades y lo que llevan las notas de cada release.</summary>
public class ChangelogTests
{
    [Fact]
    public void Parse_VersionFechaYPuntos_ConLineasQueSiguen()
    {
        var entries = Changelog.Parse("""
            # Novedades de Roost

            Texto de introduccion.

            ## 0.1.3 — en preparación

            - **Sitios:** cambiar la raíz web.
            - **Bases de datos:** contraseña de root,
              borrar y usuarios.

            ## v0.1.2 – 23-oct-2026
            - Apache.
            """);

        Assert.Equal(["0.1.3", "0.1.2"], entries.Select(e => e.Version));
        Assert.Equal("en preparación", entries[0].Date);
        Assert.Equal("23-oct-2026", entries[1].Date);
        Assert.Equal(["**Sitios:** cambiar la raíz web.", "**Bases de datos:** contraseña de root, borrar y usuarios."], entries[0].Items);
        Assert.Equal("- Apache.", entries[1].Markdown);
        Assert.Same(entries[1], Changelog.For(entries, "v0.1.2+cefcc0d"));
        Assert.Null(Changelog.For(entries, "9.9.9"));
    }

    [Fact]
    public void LaVersionQueSeCompila_TieneSuEntradaEnElChangelog()
    {
        // Cada versión nueva (Directory.Build.props) tiene que contar en CHANGELOG.md qué cambia:
        // es lo que ve el usuario en Novedades y lo que sale en las notas de la release.
        var entries = Changelog.Embedded();
        Assert.True(Changelog.For(entries, RoostVersion.Current) is not null,
            $"Falta la sección «## {RoostVersion.Current}» en CHANGELOG.md, con lo que cambia en esta versión.");
        Assert.All(entries, e => Assert.NotEmpty(e.Items));
    }

    [Fact]
    public void ElChangelog_VaDeLaMasNuevaALaMasVieja()
    {
        var versions = Changelog.Embedded()
            .Select(e => SemanticVersion.TryParse(e.Version, out var v) ? v : throw new Xunit.Sdk.XunitException($"«{e.Version}» no es una versión"))
            .ToList();
        Assert.Equal(versions.OrderByDescending(v => v).ToList(), versions);
    }
}
