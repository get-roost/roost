namespace Roost.Core.Scanning;

/// <summary>
/// Ejecuta un binario externo para leerle la versión. Se abstrae en interfaz para
/// poder separar la EJECUCIÓN (no testeable sin binarios reales) del PARSEO (puro
/// y testeable): los tests inyectan un runner falso con salidas de ejemplo.
/// </summary>
public interface IProcessRunner
{
    /// <summary>
    /// Ejecuta <paramref name="executablePath"/> con <paramref name="arguments"/> y
    /// devuelve stdout+stderr juntos. NUNCA lanza: ante cualquier fallo (ruta mala,
    /// binario que no responde, timeout) devuelve <c>null</c>.
    /// </summary>
    string? Run(string executablePath, string arguments, TimeSpan timeout);
}
