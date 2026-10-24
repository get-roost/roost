namespace Roost.Core.Tests;

// Tests de un solo sistema: los de Windows (zips de Windows, el registro, uniones, ACL…) se saltan en
// Linux y macOS, y los de Linux al revés. Así la misma suite corre en los dos runners.

/// <summary>Un test que solo vale en Windows: en Linux y macOS se salta.</summary>
public sealed class WindowsFactAttribute : FactAttribute
{
    /// <param name="skip">Por qué se salta fuera de Windows: «Solo en Windows: …», o «Pendiente en Linux: &lt;agente&gt;
    /// (&lt;oleada&gt;)» si es de los que tiene que reactivar quien lleve esa parte a Linux.</param>
    public WindowsFactAttribute(string skip = "Solo en Windows.")
    {
        if (!OperatingSystem.IsWindows()) Skip = skip;
    }
}

/// <summary>Una teoría que solo vale en Windows: en Linux y macOS se salta.</summary>
public sealed class WindowsTheoryAttribute : TheoryAttribute
{
    /// <param name="skip">Por qué se salta fuera de Windows: «Solo en Windows: …», o «Pendiente en Linux: &lt;agente&gt;
    /// (&lt;oleada&gt;)» si es de los que tiene que reactivar quien lleve esa parte a Linux.</param>
    public WindowsTheoryAttribute(string skip = "Solo en Windows.")
    {
        if (!OperatingSystem.IsWindows()) Skip = skip;
    }
}

/// <summary>Un test que solo vale en Linux: en Windows y macOS se salta.</summary>
public sealed class LinuxFactAttribute : FactAttribute
{
    public LinuxFactAttribute()
    {
        if (!OperatingSystem.IsLinux()) Skip = "Solo en Linux.";
    }
}

/// <summary>Una teoría que solo vale en Linux: en Windows y macOS se salta.</summary>
public sealed class LinuxTheoryAttribute : TheoryAttribute
{
    public LinuxTheoryAttribute()
    {
        if (!OperatingSystem.IsLinux()) Skip = "Solo en Linux.";
    }
}
