using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using Roost.Core.Platform;

namespace Roost.Core.Updates;

/// <summary>El <c>SHA256SUMS.txt</c> de una release: el formato de <c>sha256sum</c> (<c>&lt;hash&gt;  &lt;fichero&gt;</c>). PURO.</summary>
public static class Sha256Sums
{
    public const string FileName = "SHA256SUMS.txt";

    /// <summary>
    /// Fichero → SHA-256 en minúsculas. Admite el <c>*</c> del modo binario, CRLF, BOM y rutas
    /// (se queda con el nombre); lo que no es una línea del formato se ignora.
    /// </summary>
    public static IReadOnlyDictionary<string, string> Parse(string? text)
    {
        var sums = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(text)) return sums;
        foreach (var raw in text.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.Trim().TrimStart('﻿').Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            var space = line.IndexOfAny([' ', '\t']);
            if (space < 0) continue;
            var hash = line[..space];
            var name = line[space..].Trim().TrimStart('*').Replace('\\', '/');
            name = name[(name.LastIndexOf('/') + 1)..];
            if (hash.Length != 64 || !hash.All(char.IsAsciiHexDigit) || name.Length == 0) continue;
            sums[name] = hash.ToLowerInvariant();
        }
        return sums;
    }
}

/// <summary>Lo que dice la firma Authenticode de un fichero.</summary>
public enum SignatureStatus
{
    /// <summary>No se ha mirado (fuera de Windows).</summary>
    NotChecked,

    /// <summary>No lleva firma (hoy, las releases de Roost: no hay certificado aún).</summary>
    Unsigned,

    /// <summary>Firmado y la firma es válida (cadena de confianza y hash correctos).</summary>
    Valid,

    /// <summary>Firmado pero la firma no vale: el fichero se ha alterado o el certificado no es de confianza.</summary>
    Invalid,
}

/// <summary>Un fichero de la release ya descargado, con su SHA-256 comprobado y su firma mirada.</summary>
public sealed record VerifiedDownload(string Path, long Size, string Sha256, SignatureStatus Signature);

/// <summary>
/// Trae e instala una Roost nueva (ROOST.md, decisión 33: se actualiza desde la app). Es la
/// parte sin ventana: elegir el fichero de la release que toca a cada canal, bajar
/// <c>SHA256SUMS.txt</c> de <b>la misma release</b>, descargar con reanudación a la caché de
/// descargas, verificar el SHA-256 y la firma antes de ejecutar nada, y componer la línea con la
/// que se lanza el instalador. Nunca ejecuta un fichero que no haya verificado.
/// </summary>
public sealed class UpdateInstaller
{
    public const int Attempts = 3;
    public static readonly TimeSpan DefaultRetryDelay = TimeSpan.FromSeconds(2);

    /// <summary>Lo que recibe el instalador de Inno Setup para actualizar sin preguntar (la barra de progreso sí se ve).</summary>
    public const string SilentArguments = "/SILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS";

    /// <summary>Al terminar, el instalador vuelve a abrir la app (<c>roost-desktop.exe --updated</c>) como el usuario original.</summary>
    public const string RelaunchParameter = "/ROOSTRELAUNCH=1";

    /// <summary>El stack estaba en marcha: la app vuelve con <c>--start</c> (o, sin app, <c>roost up</c>).</summary>
    public const string StartStackParameter = "/ROOSTSTART=1";

    private readonly RoostPaths _paths;
    private readonly HttpClient _http;
    private readonly Func<string, SignatureStatus> _signature;
    private readonly TimeSpan _retryDelay;

    /// <param name="signatureCheck">Para los tests: qué dice la firma de un fichero; por defecto, <see cref="Authenticode.Check"/>.</param>
    /// <param name="retryDelay">La espera entre reintentos de una descarga cortada.</param>
    public UpdateInstaller(RoostPaths paths, HttpClient http, Func<string, SignatureStatus>? signatureCheck = null, TimeSpan? retryDelay = null)
    {
        _paths = paths;
        _http = http;
        _signature = signatureCheck ?? Authenticode.Check;
        _retryDelay = retryDelay ?? DefaultRetryDelay;
    }

    /// <summary>Donde se dejan los ficheros descargados (la misma caché que los zips de PHP).</summary>
    public string DownloadsDir => _paths.Downloads;

    /// <summary>El registro que escribe el instalador (<c>/LOG</c>), junto a los demás de <c>logs/</c>.</summary>
    public string SetupLogFile => Path.Combine(_paths.Logs, "roost-setup.log");

    /// <summary>
    /// El fichero de la release que hay que bajar para este canal: el instalador si Roost se
    /// instaló con él, el zip si se descomprimió a mano, y nada con Scoop, winget o compilada
    /// (esos se actualizan con su herramienta). Null también si la release no lo trae.
    /// </summary>
    public static ReleaseAsset? AssetFor(ReleaseInfo release, InstallChannel channel, bool windows) => channel switch
    {
        InstallChannel.Installer => release.Installer,
        InstallChannel.Manual => release.PackageFor(windows),
        _ => null,
    };

    /// <summary>Solo el canal del instalador se actualiza entero desde la app (descargar, verificar y ejecutar).</summary>
    public static bool InstallsFromApp(InstallChannel channel) => channel == InstallChannel.Installer;

    /// <summary>
    /// La línea de órdenes del setup: silencioso (con su barra de progreso y un solo UAC), sin
    /// reiniciar, cerrando lo que tenga ficheros abiertos, y con lo que el instalador necesita
    /// saber para volver: si relanza la app y si arranca el stack. PURO.
    /// </summary>
    public static string SetupArguments(bool relaunchApp, bool startStack, string? logFile = null)
    {
        var parts = new List<string> { SilentArguments };
        if (relaunchApp) parts.Add(RelaunchParameter);
        if (startStack) parts.Add(StartStackParameter);
        if (!string.IsNullOrWhiteSpace(logFile)) parts.Add($"/LOG=\"{logFile}\"");
        return string.Join(' ', parts);
    }

    /// <summary>
    /// Baja y parsea el <c>SHA256SUMS.txt</c> de <paramref name="release"/>. Sin él no se verifica
    /// nada, y sin verificar no se instala nada: lanza <see cref="RoostException"/>.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, string>> FetchSumsAsync(ReleaseInfo release, CancellationToken cancellation = default)
    {
        var asset = release.Assets.FirstOrDefault(a => a.Name.Equals(Sha256Sums.FileName, StringComparison.OrdinalIgnoreCase))
            ?? throw new RoostException($"La release {release.Name} no trae {Sha256Sums.FileName}: sin el no se puede verificar la descarga, asi que no se instala nada.");
        string text;
        try
        {
            using var response = await _http.GetAsync(asset.Url, cancellation);
            response.EnsureSuccessStatusCode();
            text = await response.Content.ReadAsStringAsync(cancellation);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            throw new RoostException($"No se pudo descargar {Sha256Sums.FileName} de {asset.Url.Host}: {ex.Message}", ex);
        }
        var sums = Sha256Sums.Parse(text);
        if (sums.Count == 0)
            throw new RoostException($"El {Sha256Sums.FileName} de la release {release.Name} no tiene el formato esperado: no se instala nada.");
        return sums;
    }

    /// <summary>El SHA-256 que la release declara para <paramref name="asset"/>; lanza si no está.</summary>
    public async Task<string> ExpectedSha256Async(ReleaseInfo release, ReleaseAsset asset, CancellationToken cancellation = default)
    {
        var sums = await FetchSumsAsync(release, cancellation);
        return sums.TryGetValue(asset.Name, out var sha)
            ? sha
            : throw new RoostException($"El {Sha256Sums.FileName} de la release {release.Name} no trae la suma de {asset.Name}: no se instala nada.");
    }

    /// <summary>
    /// Descarga <paramref name="asset"/> a la caché, verifica su SHA-256 contra
    /// <paramref name="expectedSha256"/> y mira su firma. Si ya estaba y cuadra, no baja nada.
    /// Una descarga cortada se reanuda desde donde se quedó (<c>Range</c>) hasta
    /// <see cref="Attempts"/> veces; si aun así falla, el <c>.part</c> se queda para el siguiente
    /// intento. Un hash que no cuadra o una firma inválida borran el fichero y lanzan: <b>no se
    /// ejecuta nada</b>.
    /// </summary>
    public async Task<VerifiedDownload> DownloadAsync(ReleaseAsset asset, string expectedSha256, IProgress<long>? progress = null, CancellationToken cancellation = default)
    {
        var expected = expectedSha256.Trim().ToLowerInvariant();
        if (expected.Length != 64 || !expected.All(char.IsAsciiHexDigit))
            throw new RoostException($"La suma declarada para {asset.Name} no es un SHA-256 ({expectedSha256}): no se instala nada.");

        Directory.CreateDirectory(DownloadsDir);
        // GetFileName: el nombre viene de la release y no debe poder escribir fuera de la caché.
        var final = Path.Combine(DownloadsDir, Path.GetFileName(asset.Name));
        if (File.Exists(final))
        {
            if (await Sha256OfAsync(final, cancellation) == expected)
            {
                progress?.Report(new FileInfo(final).Length);
                return Verified(asset, final, expected);
            }
            TryDelete(final);
        }

        var part = final + ".part";
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await FetchAsync(asset.Url, part, progress, cancellation);
                break;
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException)
            {
                if (attempt >= Attempts)
                    throw new RoostException($"No se pudo descargar {asset.Name} de {asset.Url.Host}: {ex.Message} Vuelve a intentarlo: la descarga se reanuda donde se quedo.", ex);
                await Task.Delay(_retryDelay, cancellation);
            }
        }

        var actual = await Sha256OfAsync(part, cancellation);
        if (actual != expected)
        {
            TryDelete(part);
            throw new RoostException($"El SHA-256 de {asset.Name} no coincide con el de {Sha256Sums.FileName} "
                + $"(esperado {expected}, obtenido {actual}). No se ejecuta nada.");
        }
        File.Move(part, final, overwrite: true);
        return Verified(asset, final, expected);
    }

    /// <summary><see cref="ExpectedSha256Async"/> y <see cref="DownloadAsync"/> de una vez.</summary>
    public async Task<VerifiedDownload> DownloadVerifiedAsync(ReleaseInfo release, ReleaseAsset asset, IProgress<long>? progress = null, CancellationToken cancellation = default)
    {
        var expected = await ExpectedSha256Async(release, asset, cancellation);
        return await DownloadAsync(asset, expected, progress, cancellation);
    }

    /// <summary>La firma Authenticode de un fichero (la de los tests la inyecta el constructor).</summary>
    public SignatureStatus CheckSignature(string file) => _signature(file);

    private VerifiedDownload Verified(ReleaseAsset asset, string file, string sha256)
    {
        var signature = _signature(file);
        if (signature == SignatureStatus.Invalid)
        {
            TryDelete(file);
            throw new RoostException($"La firma digital de {asset.Name} no es valida: el fichero no es el que publico Roost o se ha alterado. No se ejecuta nada.");
        }
        return new VerifiedDownload(file, new FileInfo(file).Length, sha256, signature);
    }

    // Una petición: si hay un .part, pide lo que falta (Range). 206 → se añade; 200 → el servidor no
    // reanuda y se empieza de cero; 416 → el .part no vale y se repite sin Range.
    private async Task FetchAsync(Uri url, string part, IProgress<long>? progress, CancellationToken cancellation)
    {
        var have = File.Exists(part) ? new FileInfo(part).Length : 0;
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (have > 0) request.Headers.Range = new RangeHeaderValue(have, null);

        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellation);
        if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
        {
            TryDelete(part);
            await FetchAsync(url, part, progress, cancellation);
            return;
        }
        var resume = have > 0 && response.StatusCode == HttpStatusCode.PartialContent;
        if (!resume)
        {
            response.EnsureSuccessStatusCode();
            have = 0;
        }

        await using var source = await response.Content.ReadAsStreamAsync(cancellation);
        await using var file = new FileStream(part, resume ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None);
        var buffer = new byte[81920];
        var total = have;
        progress?.Report(total);
        int read;
        while ((read = await source.ReadAsync(buffer, cancellation)) > 0)
        {
            await file.WriteAsync(buffer.AsMemory(0, read), cancellation);
            total += read;
            progress?.Report(total);
        }
    }

    private static async Task<string> Sha256OfAsync(string path, CancellationToken cancellation)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellation));
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch { /* best effort */ }
    }
}
