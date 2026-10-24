using Roost.Core.Updates;

namespace Roost.Desktop.Services;

/// <summary>
/// Con qué se abrió la app: <c>--minimized</c> (al iniciar sesión), <c>--updated</c> y
/// <c>--start</c> (el instalador la relanza tras actualizar; con <c>--start</c> si el stack
/// estaba en marcha) y <c>--update-failed=&lt;código&gt;</c> (el vigilante, si el instalador no
/// llegó a actualizar). PURO.
/// </summary>
public sealed record StartupArgs(bool Minimized, bool Updated, bool StartStack, int? UpdateFailedCode)
{
    public static readonly StartupArgs None = new(false, false, false, null);

    public static StartupArgs Parse(IEnumerable<string>? args)
    {
        var result = None;
        foreach (var arg in args ?? [])
        {
            switch (arg)
            {
                case "--minimized": result = result with { Minimized = true }; break;
                case "--updated": result = result with { Updated = true }; break;
                case "--start": result = result with { StartStack = true }; break;
                default:
                    if (UpdateLauncher.ParseFailedArgument(arg) is { } code) result = result with { UpdateFailedCode = code };
                    break;
            }
        }
        return result;
    }
}
