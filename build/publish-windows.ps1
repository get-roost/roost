<#
.SYNOPSIS
    Publica Roost para Windows x64 en una sola carpeta: lo que lleva el zip de la release.

.DESCRIPTION
    La CLI (roost.exe, que también hace de daemon), el helper (roost-helper.exe), la app
    (roost-desktop.exe) y el shim (roost-shim.exe) van autocontenidos en la MISMA carpeta y
    comparten un solo runtime de .NET, así que la app encuentra a su lado roost.exe,
    roost-helper.exe y roost-shim.exe, como en desarrollo.

    No es single-file a propósito: son cuatro ejecutables, y cada uno llevaría dentro su propia
    copia del runtime (y la CLI y la app, también la de ASP.NET Core), con lo que la descarga
    pesaría varias veces más. Además, las extensiones de bin/extensions comparten los ensamblados
    de Roost, y el helper (lo único que corre elevado) no extrae nada a %TEMP%. Tampoco se
    recorta (trimming): Avalonia, Spectre y las extensiones necesitan el framework entero.

    El shim es la excepción: va con NativeAOT (un solo .exe que arranca en milisegundos), y por
    eso no lleva roost-shim.dll ni su deps.json al lado; si los llevara, ShimInstaller los
    copiaría también.

    Cada uno se publica en su carpeta y luego se juntan comprobando que un fichero que salga en
    dos publicaciones sea el mismo byte a byte: si dos ejecutables pidieran versiones distintas de
    una dll, el que perdiera fallaría en tiempo de ejecución; así falla aquí.

    Los pdb no van: los de Roost se incrustan en sus dll (DebugType=embedded, para que las trazas
    de los logs traigan líneas) y los nativos de SkiaSharp pesan unos 100 MB.

    Lo usa el workflow Release (.github/workflows/release.yml) y sirve igual para probar la
    release en local. Necesita las herramientas de C++ de Visual Studio para el NativeAOT.

.PARAMETER Output
    La carpeta de salida. Se vacía antes. Por defecto, artifacts/win-x64.

.EXAMPLE
    powershell -NoProfile -File build/publish-windows.ps1 -Output artifacts/win-x64
#>
param(
    [string]$Output = (Join-Path $PSScriptRoot '..\artifacts\win-x64')
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$stage = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($Output)
$work = Join-Path $root 'artifacts\.publish\win-x64'

# Nuestros binarios: los que se firman (y no los de terceros, que ya vienen firmados o no son nuestros).
$own = @('roost.exe', 'roost.dll', 'roost-desktop.exe', 'roost-desktop.dll', 'roost-helper.exe', 'roost-helper.dll',
    'roost-shim.exe', 'Roost.Core.dll', 'Roost.Daemon.dll', 'Roost.Extensibility.dll')

function Publish([string]$project, [string]$name, [string[]]$extra) {
    $dir = Join-Path $work $name
    Write-Host "==> dotnet publish $project"
    # Out-Host: que la salida de dotnet no acabe en lo que devuelve la función.
    & dotnet publish (Join-Path $root $project) -c Release -r win-x64 -o $dir -p:DebugType=embedded @extra | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish $project ha fallado (codigo $LASTEXITCODE)" }
    return $dir
}

foreach ($dir in @($work, $stage)) {
    if (Test-Path $dir) { Remove-Item $dir -Recurse -Force }
}

$parts = @(
    (Publish 'src\Roost.Cli\Roost.Cli.csproj' 'cli' @('--self-contained')),
    (Publish 'src\Roost.Helper\Roost.Helper.csproj' 'helper' @('--self-contained')),
    (Publish 'src\Roost.Desktop\Roost.Desktop.csproj' 'desktop' @('--self-contained'))
)

# NativeAOT (PublishAot va en el csproj del shim): el compilador busca Visual Studio con vswhere.
$vsInstaller = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer'
if (Test-Path $vsInstaller) { $env:PATH = "$vsInstaller;$env:PATH" }
$shim = Publish 'src\Roost.Shim\Roost.Shim.csproj' 'shim' @()

Write-Host "==> Juntando en $stage"
New-Item -ItemType Directory -Force $stage | Out-Null
foreach ($part in $parts) {
    foreach ($file in Get-ChildItem $part -Recurse -File) {
        if ($file.Extension -eq '.pdb') { continue }
        # La documentación XML de los paquetes NuGet (Roost.Core.xml...) no hace falta para ejecutar.
        if ($file.Extension -eq '.xml' -and (Test-Path ([IO.Path]::ChangeExtension($file.FullName, '.dll')))) { continue }
        # El shim que arrastran las referencias de proyecto es el normal (dll + runtimeconfig): va el de NativeAOT.
        if ($file.Name -like 'roost-shim.*') { continue }
        $relative = $file.FullName.Substring($part.Length).TrimStart('\', '/')
        $target = Join-Path $stage $relative
        if (Test-Path $target) {
            if ((Get-FileHash $target).Hash -ne (Get-FileHash $file.FullName).Hash) {
                throw "$relative sale distinto en dos publicaciones (la ultima, $part): dos ejecutables piden versiones distintas. Iguala las dependencias."
            }
            continue
        }
        New-Item -ItemType Directory -Force (Split-Path $target) | Out-Null
        Copy-Item $file.FullName $target
    }
}
Copy-Item (Join-Path $shim 'roost-shim.exe') $stage
Copy-Item (Join-Path $root 'LICENSE') (Join-Path $stage 'LICENSE.txt')

foreach ($name in $own) {
    if (-not (Test-Path (Join-Path $stage $name))) { throw "Falta $name en $stage" }
}
foreach ($name in @('roost-shim.dll', 'roost-shim.deps.json', 'roost-shim.runtimeconfig.json')) {
    if (Test-Path (Join-Path $stage $name)) { throw "Sobra ${name}: el shim tiene que ser el de NativeAOT, solo" }
}

# La lista de lo que se firma, fuera de la carpeta (no va en el zip): la lee el workflow.
$signList = "$stage.firmar.txt"
$own | ForEach-Object { Join-Path $stage $_ } | Set-Content -Encoding ascii $signList

$files = @(Get-ChildItem $stage -Recurse -File)
$size = ($files | Measure-Object Length -Sum).Sum / 1MB
Write-Host ("==> {0}: {1} ficheros, {2:N0} MB" -f $stage, $files.Count, $size)
