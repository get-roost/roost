using System.Text.Json;
using System.Text.Json.Serialization;
using Roost.Core.Install;
using Roost.Core.Platform;

namespace Roost.Core.Stack;

/// <summary>
/// La contraseña de root de cada base de datos de Roost, en <c>state/db-passwords.json</c> por
/// instancia (<c>mariadb-11.4</c>, <c>mysql-8.4</c>: los datos van por rama, y la contraseña vive
/// en ellos). Roost la necesita para sus propios comandos (la parada limpia, crear y listar
/// bases de datos, la consola): la escribe en el <c>[client]</c> del my.ini que genera. Sin
/// entrada, root va sin contraseña, como el primer día.
/// </summary>
public sealed class DbPasswords(RoostPaths paths)
{
    public string FilePath => Path.Combine(paths.State, "db-passwords.json");

    /// <summary>La clave de una instalación: motor y rama, como su datadir.</summary>
    public static string InstanceKey(InstalledDb db) => $"{db.Engine.Key()}-{db.Branch}";

    /// <summary>La contraseña de root guardada, o null si va sin contraseña.</summary>
    public string? For(InstalledDb db) =>
        Load().TryGetValue(InstanceKey(db), out var password) && password.Length > 0 ? password : null;

    /// <summary>Guarda la contraseña de root (vacía o null = sin contraseña, y se quita la entrada).</summary>
    public void Set(InstalledDb db, string? password)
    {
        var all = Load();
        if (string.IsNullOrEmpty(password)) all.Remove(InstanceKey(db));
        else all[InstanceKey(db)] = password;
        Save(all);
    }

    private Dictionary<string, string> Load()
    {
        if (!File.Exists(FilePath)) return new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            return JsonSerializer.Deserialize(File.ReadAllText(FilePath), DbPasswordJson.Default.DictionaryStringString)
                   ?? new Dictionary<string, string>(StringComparer.Ordinal);
        }
        catch (JsonException ex)
        {
            throw new RoostException($"{FilePath} esta roto ({ex.Message}). Arreglalo o borralo y vuelve a poner la contrasena con roost db password.");
        }
    }

    private void Save(Dictionary<string, string> all)
    {
        Directory.CreateDirectory(paths.State);
        if (all.Count == 0)
        {
            File.Delete(FilePath);
            return;
        }
        // Escritura atómica, como sites.json: un corte a medias no deja el fichero roto.
        var temp = FilePath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(all, DbPasswordJson.Default.DictionaryStringString));
        File.Move(temp, FilePath, overwrite: true);
    }
}

/// <summary>El JSON de db-passwords.json, generado al compilar (Core también va dentro del shim, con NativeAOT).</summary>
[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(Dictionary<string, string>))]
internal sealed partial class DbPasswordJson : JsonSerializerContext;
