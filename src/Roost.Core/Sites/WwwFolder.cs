using Roost.Core.Platform;

namespace Roost.Core.Sites;

/// <summary>
/// La carpeta de proyectos compartida de Linux, <c>/var/www/roost</c> (decisión del plan de Linux):
/// la crea una vez <c>roost-helper www-init</c> con privilegios, con el usuario que lo pide como dueño
/// (0755), y a partir de ahí Roost escribe en ella sin pedir nada. Si no existe o no es del usuario,
/// Roost usa <c>~/.local/share/roost/www</c> (eso lo decide <c>RoostPaths</c>) y <c>roost up</c> sugiere
/// <c>roost www init</c>. Es <c>roost www init</c>.
/// </summary>
public static class WwwFolder
{
    public const string Path = "/var/www/roost";

    /// <summary>true si <see cref="Path"/> existe y este usuario puede crear cosas dentro.</summary>
    public static bool IsUsable()
    {
        if (!Directory.Exists(Path)) return false;
        try
        {
            var probe = System.IO.Path.Combine(Path, $".roost-probe-{Guid.NewGuid():N}");
            Directory.CreateDirectory(probe);
            Directory.Delete(probe);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Pide al helper que la cree. Lanza <see cref="RoostException"/> si se cancela, si ya existe y es de otro usuario, o si falla.</summary>
    public static void Init(string helperExe, Elevation? elevation = null)
    {
        var run = (elevation ?? Elevation.Default).Run(helperExe, ["www-init"]);
        if (run.Status == ElevationStatus.Cancelled)
            throw new RoostException($"Has cancelado el permiso de administrador: {Path} no se ha creado. Los proyectos siguen en la www de tu usuario.");
        switch (run.ExitCode)
        {
            case HelperExit.Ok:
                return;
            case HelperExit.Exists:
                throw new RoostException($"{Path} ya existe y es de otro usuario: en esta maquina usa otra carpeta de proyectos con [[paths]] www en roost.toml.");
            default:
                throw new RoostException($"roost-helper no pudo crear {Path} (codigo {run.ExitCode}). Detalle en {HostsUpdater.HelperLog}.{(run.Output.Length == 0 ? "" : $" ({run.Output})")}");
        }
    }
}
