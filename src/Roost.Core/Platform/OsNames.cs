namespace Roost.Core.Platform;

/// <summary>
/// El nombre de un ejecutable en este sistema: con <c>.exe</c> en Windows y tal cual en Linux y macOS.
/// Es para los binarios que instala Roost (PHP, las bases de datos, nginx, Redis, Mailpit, Node,
/// cloudflared); los tests (<c>FakeRoost</c>) crean sus ficheros de pega con el mismo nombre.
/// </summary>
public static class OsNames
{
    public static string Exe(string name) => OperatingSystem.IsWindows() ? name + ".exe" : name;
}
