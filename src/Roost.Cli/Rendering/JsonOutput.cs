using System.Text.Json;
using System.Text.Json.Serialization;

namespace Roost.Cli.Rendering;

/// <summary>Opciones comunes de serializacion JSON para la salida <c>--json</c>.</summary>
internal static class JsonOutput
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
}
