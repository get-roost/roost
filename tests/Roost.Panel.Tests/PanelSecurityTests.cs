using Roost.WebPanel;

namespace Roost.Panel.Tests;

/// <summary>Las reglas puras de seguridad del panel (ROOST.md, decisión 42), sin servidor.</summary>
public class PanelSecurityTests
{
    [Theory]
    [InlineData("127.0.0.1:5556", true)]
    [InlineData("localhost:5556", true)]
    [InlineData("LOCALHOST:5556", true)]
    [InlineData("[::1]:5556", true)]
    [InlineData("127.0.0.1:5557", false)]          // otro puerto
    [InlineData("127.0.0.1", false)]               // sin puerto
    [InlineData("evil.example", false)]
    [InlineData("evil.example:5556", false)]       // DNS rebinding
    [InlineData("127.0.0.1.evil.example:5556", false)]
    [InlineData("192.168.1.20:5556", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Host_SoloElPropio(string? host, bool expected) =>
        Assert.Equal(expected, PanelSecurity.IsAllowedHost(host, 5556));

    [Fact]
    public void Token_SoloElDeLaSesion_EnTiempoConstante()
    {
        var token = PanelState.NewToken();
        Assert.True(PanelSecurity.TokenMatches(token, token));
        Assert.False(PanelSecurity.TokenMatches(token + "x", token));
        Assert.False(PanelSecurity.TokenMatches(token[..^1], token));
        Assert.False(PanelSecurity.TokenMatches("", token));
        Assert.False(PanelSecurity.TokenMatches(null, token));
    }

    [Fact]
    public void Token_256Bits_Base64Url_YDistintoCadaVez()
    {
        var a = PanelState.NewToken();
        var b = PanelState.NewToken();
        Assert.NotEqual(a, b);
        Assert.Equal(43, a.Length);   // 32 bytes en base64 sin relleno
        Assert.Matches("^[A-Za-z0-9_-]+$", a);
    }

    [Theory]
    [InlineData("GET", "127.0.0.1:5556", null, null, true)]
    [InlineData("HEAD", "127.0.0.1:5556", null, null, true)]
    [InlineData("POST", "127.0.0.1:5556", "http://127.0.0.1:5556", "1", true)]
    [InlineData("DELETE", "localhost:5556", "http://localhost:5556", "1", true)]
    [InlineData("POST", "127.0.0.1:5556", null, "1", false)]                          // sin Origin
    [InlineData("POST", "127.0.0.1:5556", "http://evil.example", "1", false)]         // Origin ajeno
    [InlineData("POST", "127.0.0.1:5556", "http://localhost:5556", "1", false)]       // Origin de otro nombre que el Host
    [InlineData("POST", "127.0.0.1:5556", "http://127.0.0.1:5556", null, false)]      // sin la cabecera anti-CSRF
    [InlineData("POST", "127.0.0.1:5556", "null", "1", false)]                         // Origin null (un formulario desde file://)
    public void Escrituras_SoloDesdeLaPropiaPagina(string method, string host, string? origin, string? csrf, bool expected) =>
        Assert.Equal(expected, PanelSecurity.IsSameOriginWrite(method, host, origin, csrf));

    [Fact]
    public void Csp_EstrictaYSinScriptsEnLinea()
    {
        var csp = PanelSecurity.ContentSecurityPolicy;
        Assert.Contains("default-src 'self'", csp);
        Assert.Contains("script-src 'self'", csp);
        Assert.Contains("frame-ancestors 'none'", csp);
        Assert.DoesNotContain("unsafe-inline", csp);
        Assert.DoesNotContain("unsafe-eval", csp);
    }

    [Fact]
    public void Cookie_HttpOnly_SameSiteStrict()
    {
        var options = PanelSecurity.CookieOptions();
        Assert.True(options.HttpOnly);
        Assert.Equal(Microsoft.AspNetCore.Http.SameSiteMode.Strict, options.SameSite);
        Assert.Equal("/", options.Path);
    }
}
