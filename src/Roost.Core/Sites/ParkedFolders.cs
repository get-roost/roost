using System.Text.Json;
using System.Text.Json.Serialization;
using Roost.Core.Platform;

namespace Roost.Core.Sites;

/// <summary>
/// Sitios avanzados (C3): las carpetas aparcadas (<c>roost park</c>, como el <c>valet park</c> de
/// Valet o la www de Laragon), en <c>state/parked.json</c>. Cada subcarpeta de una aparcada es un
/// sitio <c>&lt;subcarpeta&gt;.test</c> (<see cref="SiteManager.SyncParked"/>); la app y el daemon las
/// vigilan (<see cref="ParkWatcher"/>). Lo escribe Roost; no es configuración del usuario.
/// </summary>
public sealed class ParkedFolders(RoostPaths paths)
{
    public string FilePath => Path.Combine(paths.State, "parked.json");

    /// <summary>Las carpetas, rutas completas, en el orden en que se aparcaron. Un fichero roto cuenta como ninguna.</summary>
    public IReadOnlyList<string> Load()
    {
        if (!File.Exists(FilePath)) return [];
        try
        {
            return JsonSerializer.Deserialize(File.ReadAllText(FilePath), ParkedJson.Default.ListString) ?? [];
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            return [];
        }
    }

    /// <summary>Añade la carpeta si no estaba. true si la ha añadido.</summary>
    public bool Add(string folder)
    {
        var current = Load();
        if (current.Any(f => RoostPaths.SameFolder(f, folder))) return false;
        Save([.. current, Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder))]);
        return true;
    }

    /// <summary>Quita la carpeta. true si estaba.</summary>
    public bool Remove(string folder)
    {
        var current = Load();
        var rest = current.Where(f => !RoostPaths.SameFolder(f, folder)).ToList();
        if (rest.Count == current.Count) return false;
        Save(rest);
        return true;
    }

    private void Save(List<string> folders)
    {
        Directory.CreateDirectory(paths.State);
        var temp = $"{FilePath}.tmp-{Guid.NewGuid():N}";
        try
        {
            File.WriteAllText(temp, JsonSerializer.Serialize(folders, ParkedJson.Default.ListString));
            File.Move(temp, FilePath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp)) Install.InstallFiles.TryDeleteFile(temp);
        }
    }
}

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, WriteIndented = true)]
[JsonSerializable(typeof(List<string>))]
internal sealed partial class ParkedJson : JsonSerializerContext;
