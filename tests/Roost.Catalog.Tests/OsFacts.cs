namespace Roost.Catalog.Tests;

// Tests de un solo sistema: los de Windows (zips de Windows, el registro, uniones, ACL…) se saltan en
// Linux y macOS, y los de Linux al revés. Así la misma suite corre en los dos runners.

/// <summary>Un test que solo vale en Windows: en Linux y macOS se salta.</summary>
public sealed class WindowsFactAttribute : FactAttribute
{
    public WindowsFactAttribute()
    {
        if (!OperatingSystem.IsWindows()) Skip = "Solo en Windows.";
    }
}

/// <summary>Una teoría que solo vale en Windows: en Linux y macOS se salta.</summary>
public sealed class WindowsTheoryAttribute : TheoryAttribute
{
    public WindowsTheoryAttribute()
    {
        if (!OperatingSystem.IsWindows()) Skip = "Solo en Windows.";
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
