using Roost.Core.Platform;
using Xunit;

namespace Roost.Core.Tests;

/// <summary>Qué descriptores heredados tapa el daemon al desligarse de quien lo lanzó.</summary>
public class PosixTests
{
    [Theory]
    [InlineData("pipe:[51690]", true)]      // roost up | tee up.log
    [InlineData("socket:[47499]", true)]    // una sesión de ssh
    [InlineData("/dev/pts/3", true)]        // la terminal
    [InlineData("/dev/tty1", true)]
    [InlineData("/dev/null", false)]        // no retiene nada
    [InlineData("/home/ana/up.txt", false)] // roost up > up.txt: un fichero no hace esperar a nadie
    [InlineData(null, false)]
    public void IsSharedStream_SoloTuberiasSocketsYTerminales(string? target, bool expected) =>
        Assert.Equal(expected, Posix.IsSharedStream(target));
}
