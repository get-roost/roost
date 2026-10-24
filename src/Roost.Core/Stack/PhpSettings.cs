using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Roost.Core.Stack;

/// <summary>
/// Los ajustes rápidos de una instalación (los que más se tocan), que van en el php.ini que genera
/// Roost. Se guardan en <c>etc/php/&lt;instalación&gt;/ajustes.json</c>, junto a las extensiones, y solo
/// lo que se aparta de lo de serie (<see cref="Default"/>): si Roost cambia sus valores, entran solos.
/// Lo que pone el usuario en conf.d va después y manda sobre esto.
/// </summary>
/// <param name="MemoryLimit">Tamaño (<c>512M</c>, <c>1G</c>) o <c>-1</c>, sin límite.</param>
/// <param name="UploadMaxFilesize">El fichero más grande que se puede subir.</param>
/// <param name="PostMaxSize">El cuerpo más grande de una petición; tiene que caber la subida (o <c>0</c>, sin límite).</param>
/// <param name="MaxExecutionTime">Segundos; 0 es sin límite. En la terminal PHP no lo aplica nunca.</param>
public sealed partial record PhpSettings(string MemoryLimit, string UploadMaxFilesize, string PostMaxSize, int MaxExecutionTime)
{
    public static readonly PhpSettings Default = new("512M", "128M", "128M", 120);

    public const string FileName = "ajustes.json";

    /// <summary>Lo que se guarda: null en lo que va como el de serie.</summary>
    internal sealed record Stored(
        [property: JsonPropertyName("memory_limit")] string? MemoryLimit,
        [property: JsonPropertyName("upload_max_filesize")] string? UploadMaxFilesize,
        [property: JsonPropertyName("post_max_size")] string? PostMaxSize,
        [property: JsonPropertyName("max_execution_time")] int? MaxExecutionTime);

    /// <summary>true si no se aparta en nada de lo de serie.</summary>
    public bool IsDefault => this == Default;

    /// <summary>Los guardados; los de serie si no hay fichero, o si está roto o no valida (nunca un php.ini que no arranca).</summary>
    public static PhpSettings Load(string file)
    {
        try
        {
            if (!File.Exists(file)) return Default;
            var stored = JsonSerializer.Deserialize(File.ReadAllText(file), PhpExtensionJson.Default.SettingsStored);
            if (stored is null) return Default;
            var settings = new PhpSettings(
                stored.MemoryLimit ?? Default.MemoryLimit,
                stored.UploadMaxFilesize ?? Default.UploadMaxFilesize,
                stored.PostMaxSize ?? Default.PostMaxSize,
                stored.MaxExecutionTime ?? Default.MaxExecutionTime);
            return settings.Validate() is null ? settings.Normalized() : Default;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return Default;
        }
    }

    /// <summary>Lo guarda (normalizado), o borra el fichero si todo va como el de serie. Valida antes: lanza si no vale.</summary>
    public void Save(string file)
    {
        if (Validate() is { } error) throw new RoostException(error);
        var s = Normalized();
        if (s.IsDefault)
        {
            if (File.Exists(file)) File.Delete(file);
            return;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var stored = new Stored(
            s.MemoryLimit == Default.MemoryLimit ? null : s.MemoryLimit,
            s.UploadMaxFilesize == Default.UploadMaxFilesize ? null : s.UploadMaxFilesize,
            s.PostMaxSize == Default.PostMaxSize ? null : s.PostMaxSize,
            s.MaxExecutionTime == Default.MaxExecutionTime ? null : s.MaxExecutionTime);
        File.WriteAllText(file, JsonSerializer.Serialize(stored, PhpExtensionJson.Default.SettingsStored));
    }

    /// <summary>Los tamaños como los escribe Roost: sin espacios y con la unidad en mayúscula (<c>1g</c> → <c>1G</c>).</summary>
    public PhpSettings Normalized() => this with
    {
        MemoryLimit = NormalizeSize(MemoryLimit),
        UploadMaxFilesize = NormalizeSize(UploadMaxFilesize),
        PostMaxSize = NormalizeSize(PostMaxSize),
    };

    /// <summary>
    /// PURO: por qué no valen (el primer problema, para enseñarlo tal cual), o null si valen.
    /// <c>post_max_size</c> tiene que ser al menos <c>upload_max_filesize</c>: si no, PHP descarta el
    /// formulario entero sin decir nada.
    /// </summary>
    public string? Validate()
    {
        var memory = MemoryLimit.Trim();
        if (memory != "-1")
        {
            if (Bytes(memory) is not { } m)
                return $"memory_limit '{MemoryLimit}' no vale: pon un tamano como 512M o 1G, o -1 para quitar el limite.";
            if (m < 32L * 1024 * 1024)
                return $"memory_limit '{MemoryLimit}' es muy poco: con menos de 32M ni Composer arranca.";
        }

        if (Bytes(UploadMaxFilesize) is not { } upload || upload == 0)
            return $"upload_max_filesize '{UploadMaxFilesize}' no vale: pon un tamano como 128M o 2G.";

        if (Bytes(PostMaxSize) is not { } post)
            return $"post_max_size '{PostMaxSize}' no vale: pon un tamano como 128M o 2G (0 quita el limite).";
        if (post != 0 && post < upload)
            return $"post_max_size ({PostMaxSize.Trim()}) tiene que ser al menos upload_max_filesize ({UploadMaxFilesize.Trim()}): si no, PHP descarta el formulario entero.";

        if (MaxExecutionTime is < 0 or > 86_400)
            return $"max_execution_time {MaxExecutionTime} no vale: pon segundos, de 0 (sin limite) a 86400.";
        return null;
    }

    /// <summary>PURO: un tamaño de php.ini en bytes (<c>128M</c>, <c>1g</c>, <c>4096</c>), o null si no lo es.</summary>
    public static long? Bytes(string? size)
    {
        if (size is null || SizePattern().Match(size.Trim()) is not { Success: true } m) return null;
        var number = long.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        return char.ToUpperInvariant(m.Groups[2].Value.FirstOrDefault()) switch
        {
            'K' => number * 1024,
            'M' => number * 1024 * 1024,
            'G' => number * 1024 * 1024 * 1024,
            _ => number,
        };
    }

    private static string NormalizeSize(string size) => size.Trim().ToUpperInvariant();

    // Hasta 7 cifras: 9999999G no desborda un long.
    [GeneratedRegex("^([0-9]{1,7})([KMGkmg]?)$")]
    private static partial Regex SizePattern();
}
