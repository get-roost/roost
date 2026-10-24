using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace Roost.Core.Git;

/// <summary>Una credencial guardada: el target, el usuario y el secreto (el token). Solo vive en memoria el rato que hace falta.</summary>
public sealed record StoredCredential(string Target, string? UserName, string Secret)
{
    // Que un ToString (un log, una excepción) nunca saque el token.
    public override string ToString() => $"{Target} ({UserName ?? "sin usuario"})";
}

/// <summary>Lo que se puede enseñar de una credencial guardada: nunca el secreto.</summary>
public sealed record CredentialInfo(string Target, string? UserName, DateTimeOffset? LastWritten);

/// <summary>
/// Dónde guarda Roost los tokens de git. El de verdad es <see cref="WindowsCredentialStore"/>; los
/// tests usan uno en memoria, y nunca el Administrador de credenciales real.
/// </summary>
public interface ICredentialStore
{
    /// <summary>La credencial de ese target, o null si no hay.</summary>
    StoredCredential? Read(string target);

    /// <summary>La guarda (sustituye la que hubiera con el mismo target).</summary>
    void Write(StoredCredential credential);

    /// <summary>La borra. true si existía.</summary>
    bool Delete(string target);

    /// <summary>Las que empiezan por <paramref name="prefix"/>, sin secretos.</summary>
    IReadOnlyList<CredentialInfo> List(string prefix);
}

/// <summary>Sin almacén (Linux, macOS de momento): no hay nada guardado y guardar falla con un mensaje.</summary>
public sealed class NoCredentialStore : ICredentialStore
{
    public static readonly NoCredentialStore Instance = new();

    public StoredCredential? Read(string target) => null;

    public void Write(StoredCredential credential) =>
        throw new RoostException("Guardar tokens de git solo funciona en Windows por ahora (en el Administrador de credenciales). Usa una clave SSH.");

    public bool Delete(string target) => false;

    public IReadOnlyList<CredentialInfo> List(string prefix) => [];
}

/// <summary>
/// El Administrador de credenciales de Windows (<c>advapi32</c>: <c>CredWriteW</c>,
/// <c>CredReadW</c>, <c>CredDeleteW</c>, <c>CredEnumerateW</c>), con credenciales genéricas
/// (<c>CRED_TYPE_GENERIC</c>). Windows las cifra con DPAPI para el usuario; es lo mismo que usan
/// git-credential-manager y wincred.
/// <para>
/// Persistencia <c>CRED_PERSIST_LOCAL_MACHINE</c>: sobrevive a cerrar sesión y a reiniciar, pero
/// se queda en este equipo. <c>ENTERPRISE</c> viajaría con el perfil móvil del dominio a cualquier
/// equipo donde entre el usuario, y un token de un sitio local de este equipo no tiene por qué ir
/// a ningún otro (también es lo que hace git-credential-manager).
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsCredentialStore : ICredentialStore
{
    /// <summary>Lo más que admite el blob de una credencial genérica (<c>CRED_MAX_CREDENTIAL_BLOB_SIZE</c>): 1280 caracteres en UTF-16.</summary>
    public const int MaxSecretBytes = 5 * 512;

    private const uint CredTypeGeneric = 1;
    private const uint CredPersistLocalMachine = 2;
    private const int ErrorNotFound = 1168;

    public StoredCredential? Read(string target)
    {
        if (!CredReadW(target, CredTypeGeneric, 0, out var handle))
        {
            var error = Marshal.GetLastWin32Error();
            if (error == ErrorNotFound) return null;
            throw Failure("leer", error);
        }
        try
        {
            var native = Marshal.PtrToStructure<NativeCredential>(handle);
            var secret = native.CredentialBlobSize == 0 || native.CredentialBlob == IntPtr.Zero
                ? ""
                : Marshal.PtrToStringUni(native.CredentialBlob, (int)native.CredentialBlobSize / 2);
            return new StoredCredential(Marshal.PtrToStringUni(native.TargetName) ?? target, Marshal.PtrToStringUni(native.UserName), secret);
        }
        finally
        {
            CredFree(handle);
        }
    }

    public void Write(StoredCredential credential)
    {
        var blob = Encoding.Unicode.GetBytes(credential.Secret);
        if (blob.Length > MaxSecretBytes)
            throw new RoostException($"El token es demasiado largo para el Administrador de credenciales de Windows (mas de {MaxSecretBytes / 2} caracteres).");

        var target = Marshal.StringToCoTaskMemUni(credential.Target);
        var user = credential.UserName is null ? IntPtr.Zero : Marshal.StringToCoTaskMemUni(credential.UserName);
        var comment = Marshal.StringToCoTaskMemUni("Lo guarda Roost: el token de git de un sitio. Gestionalo con roost git token.");
        var secret = Marshal.AllocCoTaskMem(Math.Max(1, blob.Length));
        try
        {
            Marshal.Copy(blob, 0, secret, blob.Length);
            var native = new NativeCredential
            {
                Type = CredTypeGeneric,
                TargetName = target,
                Comment = comment,
                CredentialBlobSize = (uint)blob.Length,
                CredentialBlob = secret,
                Persist = CredPersistLocalMachine,
                UserName = user,
            };
            if (!CredWriteW(ref native, 0)) throw Failure("guardar", Marshal.GetLastWin32Error());
        }
        finally
        {
            // El token no se queda en memoria nativa más de lo necesario.
            Marshal.Copy(new byte[blob.Length], 0, secret, blob.Length);
            Array.Clear(blob);
            Marshal.FreeCoTaskMem(secret);
            Marshal.FreeCoTaskMem(target);
            Marshal.FreeCoTaskMem(comment);
            if (user != IntPtr.Zero) Marshal.FreeCoTaskMem(user);
        }
    }

    public bool Delete(string target)
    {
        if (CredDeleteW(target, CredTypeGeneric, 0)) return true;
        var error = Marshal.GetLastWin32Error();
        if (error == ErrorNotFound) return false;
        throw Failure("borrar", error);
    }

    public IReadOnlyList<CredentialInfo> List(string prefix)
    {
        if (!CredEnumerateW(prefix + "*", 0, out var count, out var array))
        {
            var error = Marshal.GetLastWin32Error();
            if (error == ErrorNotFound) return [];
            throw Failure("listar", error);
        }
        try
        {
            var list = new List<CredentialInfo>();
            for (var i = 0; i < count; i++)
            {
                var native = Marshal.PtrToStructure<NativeCredential>(Marshal.ReadIntPtr(array, i * IntPtr.Size));
                var target = Marshal.PtrToStringUni(native.TargetName);
                // El filtro de Windows no distingue mayúsculas; aquí sí, y solo las genéricas.
                if (target is null || native.Type != CredTypeGeneric || !target.StartsWith(prefix, StringComparison.Ordinal)) continue;
                var written = DateTimeOffset.FromFileTime(((long)native.LastWritten.dwHighDateTime << 32) | (uint)native.LastWritten.dwLowDateTime);
                list.Add(new CredentialInfo(target, Marshal.PtrToStringUni(native.UserName), written));
            }
            return list.OrderBy(c => c.Target, StringComparer.OrdinalIgnoreCase).ToList();
        }
        finally
        {
            CredFree(array);
        }
    }

    private static RoostException Failure(string what, int error) =>
        new($"No se pudo {what} el token en el Administrador de credenciales de Windows: {new Win32Exception(error).Message} (error {error}).");

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeCredential
    {
        public uint Flags;
        public uint Type;
        public IntPtr TargetName;
        public IntPtr Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        public IntPtr TargetAlias;
        public IntPtr UserName;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern bool CredReadW(string target, uint type, uint flags, out IntPtr credential);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern bool CredWriteW(ref NativeCredential credential, uint flags);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern bool CredDeleteW(string target, uint type, uint flags);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern bool CredEnumerateW(string? filter, uint flags, out int count, out IntPtr credentials);

    [DllImport("advapi32.dll", ExactSpelling = true)]
    private static extern void CredFree(IntPtr buffer);
}
