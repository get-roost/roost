<#
.SYNOPSIS
Exporta los iconos PNG e ICO de Roost a partir del maestro transparente.
.DESCRIPTION
No modifica el maestro. Las rutas relativas se resuelven desde la carpeta
de este script. Los archivos exportados se pueden regenerar ejecutándolo de nuevo.
.EXAMPLE
.\export-icons.ps1
.EXAMPLE
.\export-icons.ps1 -SourcePath icons\roost-icon.png -OutputDirectory icons
#>
[CmdletBinding()]
param(
    [string] $SourcePath = 'icons\roost-icon.png',
    [string] $OutputDirectory = 'icons'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

function Get-AbsolutePath {
    param([Parameter(Mandatory = $true)][string] $Path)

    if (-not [System.IO.Path]::IsPathRooted($Path)) {
        $Path = Join-Path -Path $PSScriptRoot -ChildPath $Path
    }
    return [System.IO.Path]::GetFullPath($Path)
}

$sourceFile = Get-Item -LiteralPath (Get-AbsolutePath $SourcePath) -ErrorAction Stop
if ($sourceFile.PSIsContainer) {
    throw "El maestro debe ser un archivo PNG: $($sourceFile.FullName)"
}
$sourceFullPath = $sourceFile.FullName
$outputFullPath = Get-AbsolutePath $OutputDirectory
$pngDirectory = Join-Path -Path $outputFullPath -ChildPath 'png'
$icoPath = Join-Path -Path $outputFullPath -ChildPath 'roost.ico'
$pngSizes = @(16, 24, 32, 48, 64, 128, 256, 512)
$icoSizes = @(16, 24, 32, 48, 64, 128, 256)

# Las API de System.IO y System.Drawing reciben rutas literales, sin comodines.
# Se comprueba cada destino antes de escribir para que nunca se pise el maestro.
$destinationPaths = @($icoPath) + @($pngSizes | ForEach-Object {
    Join-Path -Path $pngDirectory -ChildPath "roost-$_.png"
})
foreach ($destinationPath in $destinationPaths) {
    if ([string]::Equals($sourceFullPath, $destinationPath, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "El maestro coincide con un destino de exportación: $destinationPath"
    }
}

$sourceImage = $null
try {
    $sourceImage = [System.Drawing.Bitmap]::new($sourceFullPath)
    if ($sourceImage.RawFormat.Guid -ne [System.Drawing.Imaging.ImageFormat]::Png.Guid) {
        throw 'El maestro debe estar codificado como PNG.'
    }
    if ($sourceImage.Width -ne $sourceImage.Height) {
        throw "El maestro debe ser cuadrado; mide $($sourceImage.Width) x $($sourceImage.Height) píxeles."
    }
    if (-not [System.Drawing.Image]::IsAlphaPixelFormat($sourceImage.PixelFormat)) {
        throw 'El maestro debe ser un PNG RGBA con canal alfa.'
    }

    $hasTransparency = $false
    for ($y = 0; $y -lt $sourceImage.Height -and -not $hasTransparency; $y++) {
        for ($x = 0; $x -lt $sourceImage.Width; $x++) {
            if ($sourceImage.GetPixel($x, $y).A -lt 255) {
                $hasTransparency = $true
                break
            }
        }
    }
    if (-not $hasTransparency) {
        throw 'El maestro tiene canal alfa, pero todos sus píxeles son opacos.'
    }

    [void][System.IO.Directory]::CreateDirectory($pngDirectory)
    $icoEntries = [System.Collections.Generic.List[object]]::new()

    foreach ($size in $pngSizes) {
        $bitmap = $null
        $graphics = $null
        $attributes = $null
        $memory = $null
        try {
            $bitmap = [System.Drawing.Bitmap]::new($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
            $bitmap.SetResolution(96, 96)
            $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
            $graphics.Clear([System.Drawing.Color]::Transparent)
            $graphics.CompositingMode = [System.Drawing.Drawing2D.CompositingMode]::SourceCopy
            $graphics.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
            $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
            $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
            $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
            $attributes = [System.Drawing.Imaging.ImageAttributes]::new()
            $attributes.SetWrapMode([System.Drawing.Drawing2D.WrapMode]::TileFlipXY)
            $rectangle = [System.Drawing.Rectangle]::new(0, 0, $size, $size)
            $graphics.DrawImage($sourceImage, $rectangle, 0, 0, $sourceImage.Width, $sourceImage.Height,
                [System.Drawing.GraphicsUnit]::Pixel, $attributes)
            $graphics.Dispose()
            $graphics = $null

            $pngPath = Join-Path -Path $pngDirectory -ChildPath "roost-$size.png"
            $bitmap.Save($pngPath, [System.Drawing.Imaging.ImageFormat]::Png)

            if ($icoSizes -contains $size) {
                $memory = [System.IO.MemoryStream]::new()
                $bitmap.Save($memory, [System.Drawing.Imaging.ImageFormat]::Png)
                $icoEntries.Add([pscustomobject]@{ Size = $size; Bytes = $memory.ToArray() })
            }
        }
        finally {
            if ($null -ne $memory) { $memory.Dispose() }
            if ($null -ne $attributes) { $attributes.Dispose() }
            if ($null -ne $graphics) { $graphics.Dispose() }
            if ($null -ne $bitmap) { $bitmap.Dispose() }
        }
    }

    $icoStream = $null
    $writer = $null
    try {
        $icoStream = [System.IO.File]::Open($icoPath, [System.IO.FileMode]::Create,
            [System.IO.FileAccess]::Write, [System.IO.FileShare]::None)
        $writer = [System.IO.BinaryWriter]::new($icoStream)
        $writer.Write([uint16]0)                 # Reservado.
        $writer.Write([uint16]1)                 # Tipo: icono.
        $writer.Write([uint16]$icoEntries.Count)
        $offset = [uint32](6 + 16 * $icoEntries.Count)
        foreach ($entry in $icoEntries) {
            $dimension = if ($entry.Size -eq 256) { 0 } else { $entry.Size }
            $writer.Write([byte]$dimension)      # 0 representa 256 píxeles.
            $writer.Write([byte]$dimension)
            $writer.Write([byte]0)               # Sin paleta.
            $writer.Write([byte]0)               # Reservado.
            $writer.Write([uint16]1)             # Planos de color.
            $writer.Write([uint16]32)            # RGBA: 32 bits por píxel.
            $writer.Write([uint32]$entry.Bytes.Length)
            $writer.Write($offset)
            $offset = [uint32]($offset + $entry.Bytes.Length)
        }
        foreach ($entry in $icoEntries) {
            $writer.Write([byte[]]$entry.Bytes)
        }
        $writer.Flush()
    }
    finally {
        if ($null -ne $writer) { $writer.Dispose() }
        if ($null -ne $icoStream) { $icoStream.Dispose() }
    }

    Write-Output "PNG exportados en: $pngDirectory"
    Write-Output "ICO multirresolución exportado en: $icoPath"
}
finally {
    if ($null -ne $sourceImage) { $sourceImage.Dispose() }
}
