using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Roost.Core.Platform;
using Roost.Core.Scanning;

namespace Roost.Core.Sites;

/// <summary>
/// Dónde se confía en la CA local (ROOST.md §5.8), según el sistema: el almacén raíz del usuario de
/// Windows (<see cref="WindowsRootStore"/>, el que leen Chrome y Edge), los NSS del usuario en Linux
/// (<see cref="NssTrust"/>: Chrome, Chromium, Edge, Brave y cada perfil de Firefox) o, desde un Linux de
/// WSL, el almacén del usuario de Windows por interop (<see cref="WslTrustStore"/>), porque el
/// navegador está allí. <see cref="LocalCa"/> habla con el que le toque (<see cref="TrustStores.Default"/>).
/// </summary>
public interface ITrustStore
{
    /// <summary>Qué es, para los mensajes: «los certificados de confianza de tu usuario».</summary>
    string Description { get; }

    /// <summary>true si esa CA (por su huella) está en el almacén principal.</summary>
    bool IsTrusted(X509Certificate2 ca);

    /// <summary>Añade la CA. Lanza <see cref="RoostException"/> si no ha podido en ninguno; lo que sí y lo que no, en el informe.</summary>
    TrustReport Trust(X509Certificate2 ca);

    /// <summary>Quita la CA de donde esté. Lanza <see cref="RoostException"/> si no ha podido.</summary>
    TrustReport Untrust(X509Certificate2 ca);
}

/// <summary>Dónde ha quedado (o de dónde se ha quitado) la CA, almacén por almacén, y lo que se ha saltado con el porqué.</summary>
public sealed record TrustReport(IReadOnlyList<string> Stores, IReadOnlyList<string> Skipped)
{
    public static readonly TrustReport Empty = new([], []);

    /// <summary>«Chrome, Chromium, Edge y Brave; Firefox (default-release)».</summary>
    public string Summary => Stores.Count == 0 ? "ninguno" : string.Join("; ", Stores);
}

public static class TrustStores
{
    /// <summary>El almacén de este sistema.</summary>
    public static ITrustStore Default() =>
        OperatingSystem.IsWindows() ? new WindowsRootStore()
        : Wsl.IsWsl ? new WslTrustStore(CommandRunner.Default)
        : new NssTrust(CommandRunner.Default);
}

/// <summary>
/// El almacén raíz del USUARIO de Windows (<c>CurrentUser\Root</c>, no el de la máquina: no hace
/// falta ser administrador). Windows enseña un aviso que el usuario tiene que aceptar; si lo
/// rechaza, la CA no entra. Chrome y Edge lo usan; Firefox lo lee con <see cref="FirefoxTrust"/>.
/// </summary>
public sealed class WindowsRootStore : ITrustStore
{
    public const string Label = "los certificados de confianza de tu usuario (Chrome y Edge)";

    public string Description => Label;

    public bool IsTrusted(X509Certificate2 ca)
    {
        using var store = new X509Store(StoreName.Root, StoreLocation.CurrentUser);
        store.Open(OpenFlags.ReadOnly);
        return store.Certificates.Find(X509FindType.FindByThumbprint, ca.Thumbprint, false).Count > 0;
    }

    public TrustReport Trust(X509Certificate2 ca)
    {
        using var publicOnly = X509Certificate2.CreateFromPem(ca.ExportCertificatePem());
        using var store = new X509Store(StoreName.Root, StoreLocation.CurrentUser);
        try
        {
            store.Open(OpenFlags.ReadWrite);
            store.Add(publicOnly);
        }
        catch (CryptographicException ex)
        {
            throw new RoostException($"No se ha anadido la CA de Roost a los certificados de confianza: {ex.Message}");
        }
        return new TrustReport([Label], []);
    }

    public TrustReport Untrust(X509Certificate2 ca)
    {
        using var store = new X509Store(StoreName.Root, StoreLocation.CurrentUser);
        try
        {
            store.Open(OpenFlags.ReadWrite);
            foreach (var found in store.Certificates.Find(X509FindType.FindByThumbprint, ca.Thumbprint, false))
                store.Remove(found);
        }
        catch (CryptographicException ex)
        {
            throw new RoostException($"No se ha quitado la CA de Roost de los certificados de confianza: {ex.Message}");
        }
        return new TrustReport([Label], []);
    }
}

/// <summary>
/// Desde un Linux de WSL, el almacén raíz del usuario de WINDOWS, con <c>certutil.exe</c> por interop:
/// <c>-user -addstore Root</c> (Windows enseña el mismo aviso que con Roost en Windows), <c>-user -store
/// Root &lt;huella&gt;</c> para saber si está y <c>-user -delstore Root &lt;huella&gt;</c> para quitarla.
/// certutil.exe lee el PEM por una ruta de Windows: la CA se copia a un temporal de este Linux y se
/// le da como <c>\\wsl.localhost\&lt;distro&gt;\…</c> (<see cref="WslInterop.ToWindowsPath"/>).
/// </summary>
public sealed class WslTrustStore(ICommandRunner runner, string? distro = null, Func<string, bool>? exists = null) : ITrustStore
{
    public const string Label = "los certificados de confianza de tu usuario de Windows (Chrome y Edge de Windows)";

    private readonly string? _distro = distro ?? WslInterop.Distro;
    private readonly Func<string, bool> _exists = exists ?? File.Exists;

    /// <summary>Cómo ve Windows un fichero de este Linux; los tests ponen el suyo.</summary>
    internal Func<string, string?>? WindowsPathOf { get; init; }

    public string Description => Label;

    public bool IsTrusted(X509Certificate2 ca)
    {
        if (!_exists(WslInterop.Certutil)) return false;
        try
        {
            return Run(["-user", "-store", "Root", ca.Thumbprint]).Succeeded;
        }
        catch (RoostException)
        {
            return false;
        }
    }

    public TrustReport Trust(X509Certificate2 ca)
    {
        if (!_exists(WslInterop.Certutil))
            throw new RoostException($"No se encuentra {WslInterop.Certutil}: sin el interop de Windows no se puede anadir la CA a Windows desde WSL.");
        var temp = Path.Combine(Path.GetTempPath(), $"roost-ca-{Guid.NewGuid():N}.crt");
        File.WriteAllText(temp, ca.ExportCertificatePem());
        try
        {
            var windowsPath = (WindowsPathOf ?? (path => WslInterop.ToWindowsPath(path, _distro)))(temp)
                ?? throw new RoostException($"No se sabe el nombre de esta distro ({WslInterop.DistroVariable}): copia {temp} a Windows y ejecuta alli: certutil -user -addstore Root <fichero>");
            var result = Run(["-user", "-addstore", "Root", windowsPath]);
            if (!result.Succeeded)
                throw new RoostException($"certutil.exe no ha anadido la CA al almacen de Windows (rechazaste el aviso de Windows?): {Trim(result)}");
            return new TrustReport([Label], []);
        }
        finally
        {
            try { File.Delete(temp); } catch { /* temporal */ }
        }
    }

    public TrustReport Untrust(X509Certificate2 ca)
    {
        if (!_exists(WslInterop.Certutil))
            throw new RoostException($"No se encuentra {WslInterop.Certutil}: sin el interop de Windows no se puede quitar la CA de Windows desde WSL.");
        var result = Run(["-user", "-delstore", "Root", ca.Thumbprint]);
        if (!result.Succeeded)
            throw new RoostException($"certutil.exe no ha quitado la CA del almacen de Windows: {Trim(result)}");
        return new TrustReport([Label], []);
    }

    private CommandResult Run(IReadOnlyList<string> arguments) =>
        runner.RunAsync(new CommandSpec(WslInterop.Certutil, arguments, Timeout: TimeSpan.FromMinutes(5)), null, CancellationToken.None).GetAwaiter().GetResult();

    private static string Trim(CommandResult result) => $"{result.Stderr.Trim()} {result.Stdout.Trim()}".Trim();
}
