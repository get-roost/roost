using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Roost.Core.Platform;

namespace Roost.WebPanel;

/// <summary>
/// Lo que apunta el panel en marcha: su pid, su puerto, el token de esta sesión y cuándo arrancó.
/// Va en <c>run/panel.json</c>, a 0600 (solo lo lee el usuario): el token abre el panel, así que
/// nunca sale en los registros ni en ningún otro fichero.
/// </summary>
public sealed record PanelInfo(int Pid, int Port, string Token, DateTimeOffset Started)
{
    /// <summary>La URL con el token, la que dan <c>roost panel up</c> y <c>roost panel url</c>.</summary>
    [JsonIgnore]
    public string Url => $"http://127.0.0.1:{Port}/?t={Token}";

    /// <summary>La URL sin el token, para los registros y para enseñarla.</summary>
    [JsonIgnore]
    public string PlainUrl => $"http://127.0.0.1:{Port}/";
}

/// <summary>Lee y escribe <c>run/panel.json</c> y dice si el panel que apunta sigue vivo.</summary>
public static class PanelState
{
    /// <summary>El puerto por defecto del panel (<c>roost panel up</c> sin puerto).</summary>
    public const int DefaultPort = 5556;

    public static string File(RoostPaths paths) => Path.Combine(paths.Run, "panel.json");

    /// <summary>Un token nuevo por arranque: 256 bits aleatorios en base64url.</summary>
    public static string NewToken() => Base64Url(RandomNumberGenerator.GetBytes(32));

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static PanelInfo? Read(RoostPaths paths)
    {
        var file = File(paths);
        if (!System.IO.File.Exists(file)) return null;
        try
        {
            return JsonSerializer.Deserialize(System.IO.File.ReadAllText(file), PanelJson.Default.PanelInfo);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static void Write(RoostPaths paths, PanelInfo info)
    {
        Directory.CreateDirectory(paths.Run);
        PrivateFile.Write(File(paths), JsonSerializer.Serialize(info, PanelJson.Default.PanelInfo));
    }

    public static void Delete(RoostPaths paths)
    {
        try { System.IO.File.Delete(File(paths)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>
    /// El panel que apunta el fichero, si su proceso sigue vivo y es el mismo (arrancó cuando dice el
    /// fichero, con un margen: un pid reutilizado por otro programa no cuenta). Si no, null.
    /// </summary>
    public static PanelInfo? ReadAlive(RoostPaths paths)
    {
        var info = Read(paths);
        return info is not null && IsAlive(info) ? info : null;
    }

    public static bool IsAlive(PanelInfo info)
    {
        try
        {
            using var process = Process.GetProcessById(info.Pid);
            if (process.HasExited) return false;
            DateTime started;
            try { started = process.StartTime.ToUniversalTime(); }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or UnauthorizedAccessException)
            {
                // Sin permiso para saber cuándo arrancó: vale con que exista.
                return true;
            }
            return Math.Abs((started - info.Started.UtcDateTime).TotalSeconds) < 10;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[JsonSerializable(typeof(PanelInfo))]
internal sealed partial class PanelJson : JsonSerializerContext;
