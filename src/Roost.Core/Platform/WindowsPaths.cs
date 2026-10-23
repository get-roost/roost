namespace Roost.Core.Platform;

/// <summary>
/// Localizaciones típicas de Windows donde viven los stacks locales. Igual que el
/// script de referencia, barremos varias unidades (no solo C:) porque mucha gente
/// mete Laragon/XAMPP en D: o E:.
/// </summary>
public static class WindowsPaths
{
    /// <summary>Raíces de unidad a barrer: C:, D:, E: y la unidad del perfil del usuario.</summary>
    public static IReadOnlyCollection<string> Drives()
    {
        var drives = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            @"C:\", @"D:\", @"E:\",
        };

        try
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var root = Path.GetPathRoot(home);
            if (!string.IsNullOrEmpty(root)) drives.Add(root);
        }
        catch
        {
            // Sin perfil accesible nos quedamos con las unidades fijas.
        }

        return drives;
    }

    /// <summary>Perfil del usuario (<c>%USERPROFILE%</c>), o cadena vacía si no se puede leer.</summary>
    public static string UserProfile()
    {
        try { return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile); }
        catch { return string.Empty; }
    }

    /// <summary>Carpetas de "Archivos de programa" (64 y 32 bits), sin vacíos ni repetidos.</summary>
    public static IEnumerable<string> ProgramFiles()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var folder in new[] { Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86 })
        {
            try
            {
                var path = Environment.GetFolderPath(folder);
                if (!string.IsNullOrEmpty(path)) set.Add(path);
            }
            catch
            {
                // Ignorar: puede no existir la de 32 bits.
            }
        }

        return set;
    }
}
