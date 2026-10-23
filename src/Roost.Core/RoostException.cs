namespace Roost.Core;

/// <summary>
/// Fallo de una operación de Roost (instalar, leer la configuración, arrancar el stack) con
/// un mensaje pensado para enseñarlo tal cual al usuario: qué ha pasado y qué puede hacer.
/// Quien la lanza garantiza que no deja nada a medias.
/// </summary>
public sealed class RoostException(string message, Exception? inner = null)
    : Exception(message, inner);
