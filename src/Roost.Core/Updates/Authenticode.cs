using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Roost.Core.Updates;

/// <summary>
/// La firma Authenticode de un fichero, con <c>WinVerifyTrust</c> (lo mismo que mira Windows al
/// abrirlo). Hoy las releases de Roost van sin firmar, así que <see cref="SignatureStatus.Unsigned"/>
/// no bloquea nada; lo que sí se rechaza es una firma que no vale: un fichero firmado y luego
/// alterado, o firmado por quien no es de confianza. Cuando haya certificado, bastará exigir
/// <see cref="SignatureStatus.Valid"/>.
/// </summary>
public static class Authenticode
{
    /// <summary>Nunca lanza: lo que no se puede comprobar se da por no firmado.</summary>
    public static SignatureStatus Check(string file)
    {
        if (!OperatingSystem.IsWindows()) return SignatureStatus.NotChecked;
        try
        {
            return CheckWindows(file);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return SignatureStatus.NotChecked;
        }
    }

    /// <summary>PURO: qué significa lo que devuelve <c>WinVerifyTrust</c> (y, si procede, el último error).</summary>
    public static SignatureStatus Interpret(int result, int lastError)
    {
        if (result == 0) return SignatureStatus.Valid;
        return (uint)result switch
        {
            // No se pudo ni leer el fichero: no se sabe nada de su firma.
            CRYPT_E_FILE_ERROR or E_FILE_NOT_FOUND => SignatureStatus.NotChecked,
            // «No hay firma»: la propia respuesta, o un formato que Windows no sabe firmar (un txt, un zip).
            TRUST_E_SUBJECT_FORM_UNKNOWN or TRUST_E_PROVIDER_UNKNOWN => SignatureStatus.Unsigned,
            TRUST_E_NOSIGNATURE => (uint)lastError is TRUST_E_NOSIGNATURE or TRUST_E_SUBJECT_FORM_UNKNOWN or TRUST_E_PROVIDER_UNKNOWN or 0
                ? SignatureStatus.Unsigned
                : SignatureStatus.Invalid,
            _ => SignatureStatus.Invalid,
        };
    }

    private const uint TRUST_E_PROVIDER_UNKNOWN = 0x800B0001;
    private const uint TRUST_E_SUBJECT_FORM_UNKNOWN = 0x800B0003;
    private const uint TRUST_E_NOSIGNATURE = 0x800B0100;
    private const uint CRYPT_E_FILE_ERROR = 0x80092003;
    private const uint E_FILE_NOT_FOUND = 0x80070002;

    private const uint WTD_UI_NONE = 2;
    private const uint WTD_REVOKE_NONE = 0;
    private const uint WTD_CHOICE_FILE = 1;
    private const uint WTD_STATEACTION_VERIFY = 1;
    private const uint WTD_STATEACTION_CLOSE = 2;
    private const uint WTD_REVOCATION_CHECK_NONE = 0x10;
    private const uint WTD_CACHE_ONLY_URL_RETRIEVAL = 0x1000;

    private static readonly Guid WINTRUST_ACTION_GENERIC_VERIFY_V2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

    [SupportedOSPlatform("windows")]
    private static SignatureStatus CheckWindows(string file)
    {
        var path = Marshal.StringToCoTaskMemUni(Path.GetFullPath(file));
        var fileInfo = Marshal.AllocCoTaskMem(Marshal.SizeOf<WINTRUST_FILE_INFO>());
        try
        {
            Marshal.StructureToPtr(new WINTRUST_FILE_INFO
            {
                cbStruct = (uint)Marshal.SizeOf<WINTRUST_FILE_INFO>(),
                pcwszFilePath = path,
            }, fileInfo, fDeleteOld: false);

            var data = new WINTRUST_DATA
            {
                cbStruct = (uint)Marshal.SizeOf<WINTRUST_DATA>(),
                dwUIChoice = WTD_UI_NONE,
                fdwRevocationChecks = WTD_REVOKE_NONE,
                dwUnionChoice = WTD_CHOICE_FILE,
                pFile = fileInfo,
                dwStateAction = WTD_STATEACTION_VERIFY,
                // Sin ir a la red: una máquina sin conexión tiene que poder actualizar igual.
                dwProvFlags = WTD_REVOCATION_CHECK_NONE | WTD_CACHE_ONLY_URL_RETRIEVAL,
            };
            var action = WINTRUST_ACTION_GENERIC_VERIFY_V2;
            var result = WinVerifyTrust(new IntPtr(-1), ref action, ref data);
            var lastError = Marshal.GetLastPInvokeError();

            data.dwStateAction = WTD_STATEACTION_CLOSE;
            WinVerifyTrust(new IntPtr(-1), ref action, ref data);

            return Interpret(result, lastError);
        }
        finally
        {
            Marshal.FreeCoTaskMem(fileInfo);
            Marshal.FreeCoTaskMem(path);
        }
    }

    [DllImport("wintrust.dll", ExactSpelling = true, SetLastError = true)]
    private static extern int WinVerifyTrust(IntPtr hwnd, ref Guid pgActionID, ref WINTRUST_DATA pWVTData);

    [StructLayout(LayoutKind.Sequential)]
    private struct WINTRUST_FILE_INFO
    {
        public uint cbStruct;
        public IntPtr pcwszFilePath;
        public IntPtr hFile;
        public IntPtr pgKnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WINTRUST_DATA
    {
        public uint cbStruct;
        public IntPtr pPolicyCallbackData;
        public IntPtr pSIPClientData;
        public uint dwUIChoice;
        public uint fdwRevocationChecks;
        public uint dwUnionChoice;
        public IntPtr pFile;
        public uint dwStateAction;
        public IntPtr hWVTStateData;
        public IntPtr pwszURLReference;
        public uint dwProvFlags;
        public uint dwUIContext;
        public IntPtr pSignatureSettings;
    }
}
