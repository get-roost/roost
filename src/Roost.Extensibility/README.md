# Roost.Extensibility

El contrato de las extensiones de [Roost](https://github.com/get-roost/roost), el entorno de desarrollo PHP para Windows y Linux que audita lo que instala. Una extensión es una dll que Roost carga desde `bin/extensions/<Nombre>/<Nombre>.dll` y que puede aportar:

- servicios que arrancan con el stack, después de la base de datos y antes de PHP (`IStackExtension`);
- avisos que se mezclan con los de Roost y scanners nuevos para `roost scan` y `roost audit` (`IAuditExtension`);
- comandos nuevos para la CLI, con Spectre.Console.Cli (`ICliExtension`).

Este paquete trae como dependencia [Roost.Core](https://www.nuget.org/packages/Roost.Core). Las pantallas de la app de escritorio (`IDesktopExtension`) viven en la app y todavía no tienen paquete.

## Una extensión mínima

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <AssemblyName>MiExtension</AssemblyName>
  </PropertyGroup>
  <ItemGroup>
    <!-- Roost ya lleva estos ensamblados y los comparte con la extensión: no hace falta copiarlos. -->
    <PackageReference Include="Roost.Extensibility" Version="0.1.0" ExcludeAssets="runtime" />
  </ItemGroup>
</Project>
```

```csharp
using Roost.Extensibility;
using Spectre.Console;
using Spectre.Console.Cli;

public sealed class MiExtension : ICliExtension
{
    public string Id => "mi-extension";
    public string Name => "Mi extensión";
    public string Version => "1.0.0";

    public void AddCommands(IConfigurator config) => config.AddCommand<HolaCommand>("hola");
}

public sealed class HolaCommand : Command<EmptyCommandSettings>
{
    public override int Execute(CommandContext context, EmptyCommandSettings settings, CancellationToken cancellationToken)
    {
        AnsiConsole.MarkupLine("Hola desde una extensión");
        return 0;
    }
}
```

Copia la salida del build a `bin/extensions/MiExtension/` de la raíz de Roost y comprueba con `roost extensions` que carga.

## Reglas

- Cada extensión va en su propio `AssemblyLoadContext`, pero comparte con Roost los ensamblados que Roost ya tiene (Roost.Core, Spectre…), para que los tipos del contrato sean los mismos.
- Una extensión que falla (no carga, lanza, pide un contrato más nuevo que `ExtensionApi.Current`) se apunta como error y Roost sigue sin ella.
- Los ids son minúsculas, números y guiones, y no se pueden repetir.

Más detalle en la [extensión de muestra](https://github.com/get-roost/roost/tree/main/tests/Roost.Extensibility.Sample) que usan los tests.

Licencia MIT.
