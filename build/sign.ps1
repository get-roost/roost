<#
.SYNOPSIS
    Firma un fichero con el método de firma que use el workflow Release: el setup de Inno y su
    desinstalador, que se compilan DESPUÉS de firmar los exe y las dll de dentro.

.DESCRIPTION
    ISCC lo ejecuta una vez por fichero (el desinstalador al compilar y el setup al final) a través
    de un sign tool de línea de órdenes:

        ISCC "/Ssign=pwsh -NoProfile -NonInteractive -ExecutionPolicy Bypass -File $qbuild\sign.ps1$q -Method azure -Work $qartifacts\signing$q -File $f" /DSignTool=sign ...

    ($f lo sustituye ISCC por el fichero entre comillas; $q es una comilla). Con -Prepare, antes de
    compilar, deja en -Work lo que necesita cada método:

      - azure (Azure Artifact Signing, antes Trusted Signing): el cliente para signtool
        (Azure.CodeSigning.Dlib.dll, del paquete NuGet Microsoft.Trusted.Signing.Client) y su
        metadata.json con el endpoint, la cuenta y el perfil (variables AZURE_SIGNING_ENDPOINT,
        AZURE_SIGNING_ACCOUNT y AZURE_CERTIFICATE_PROFILE). Las credenciales son las de azure/login
        (OIDC), que la dlib recoge sola. Es el mismo camino que usa azure/artifact-signing-action.

      - signpath: el módulo de PowerShell SignPath; cada fichero es una solicitud de firma
        (Submit-SigningRequest) con una configuración de artefacto de UN solo fichero PE (variable
        SIGNPATH_PE_ARTIFACT_CONFIGURATION_SLUG; por defecto, pe-file), que hay que tener creada en
        el proyecto de SignPath además de la del zip. Con SignPath Foundation, cada solicitud la
        aprueba una persona: el setup y el desinstalador son dos aprobaciones más por release.

    Al acabar comprueba que el fichero ha quedado con una firma válida (Get-AuthenticodeSignature).
    Sin secretos configurados el workflow no lo llama: el instalador compila sin firma, como antes.
#>
param(
    [Parameter(Mandatory)][ValidateSet('azure', 'signpath')][string]$Method,
    [string]$Work = 'artifacts/signing',
    [string]$File,
    [switch]$Prepare
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Require-Env([string[]]$names) {
    foreach ($name in $names) {
        if (-not [Environment]::GetEnvironmentVariable($name)) { throw "Falta la variable de entorno $name para firmar con $Method" }
    }
}

function Find-SignTool {
    $kits = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\bin'
    $exe = Get-ChildItem $kits -Recurse -Filter signtool.exe -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -like '*\x64\*' } | Sort-Object FullName -Descending | Select-Object -First 1
    if (-not $exe) { throw "No hay signtool.exe (Windows SDK) en $kits" }
    return $exe.FullName
}

function Env-Or([string]$name, [string]$default) {
    $value = [Environment]::GetEnvironmentVariable($name)
    if ($value) { return $value } else { return $default }
}

if ($Prepare) {
    New-Item -ItemType Directory -Force $Work | Out-Null
    $Work = (Resolve-Path $Work).Path
    switch ($Method) {
        'azure' {
            Require-Env 'AZURE_SIGNING_ENDPOINT', 'AZURE_SIGNING_ACCOUNT', 'AZURE_CERTIFICATE_PROFILE'
            $nuget = Join-Path $Work 'nuget'
            & nuget install Microsoft.Trusted.Signing.Client -OutputDirectory $nuget -NonInteractive | Out-Host
            if ($LASTEXITCODE -ne 0) { throw 'No se pudo bajar el paquete Microsoft.Trusted.Signing.Client' }
            $dlib = Get-ChildItem $nuget -Recurse -Filter Azure.CodeSigning.Dlib.dll | Where-Object { $_.FullName -like '*\x64\*' } | Select-Object -First 1
            if (-not $dlib) { throw 'El paquete no trae Azure.CodeSigning.Dlib.dll para x64' }
            Copy-Item $dlib.FullName (Join-Path $Work 'Azure.CodeSigning.Dlib.dll')
            @{
                Endpoint               = $env:AZURE_SIGNING_ENDPOINT
                CodeSigningAccountName = $env:AZURE_SIGNING_ACCOUNT
                CertificateProfileName = $env:AZURE_CERTIFICATE_PROFILE
            } | ConvertTo-Json | Set-Content (Join-Path $Work 'metadata.json') -Encoding ascii
            Find-SignTool | Set-Content (Join-Path $Work 'signtool.txt') -Encoding ascii
        }
        'signpath' {
            Require-Env 'SIGNPATH_API_TOKEN', 'SIGNPATH_ORGANIZATION_ID'
            Install-Module SignPath -Force -Scope CurrentUser -AllowClobber
        }
    }
    Write-Host "Firma del instalador preparada ($Method) en $Work"
    exit 0
}

if (-not $File) { throw 'Falta -File (o -Prepare)' }
$File = (Resolve-Path $File).Path
$Work = (Resolve-Path $Work).Path
Write-Host "Firmando ($Method): $File"

switch ($Method) {
    'azure' {
        $signtool = (Get-Content (Join-Path $Work 'signtool.txt') -Raw).Trim()
        & $signtool sign /v /fd SHA256 /tr http://timestamp.acs.microsoft.com /td SHA256 `
            /dlib (Join-Path $Work 'Azure.CodeSigning.Dlib.dll') /dmdf (Join-Path $Work 'metadata.json') $File | Out-Host
        if ($LASTEXITCODE -ne 0) { throw "signtool ha fallado (codigo $LASTEXITCODE) con $File" }
    }
    'signpath' {
        Require-Env 'SIGNPATH_API_TOKEN', 'SIGNPATH_ORGANIZATION_ID'
        Import-Module SignPath
        $signed = "$File.firmado"
        Submit-SigningRequest `
            -ApiToken $env:SIGNPATH_API_TOKEN `
            -OrganizationId $env:SIGNPATH_ORGANIZATION_ID `
            -ProjectSlug (Env-Or 'SIGNPATH_PROJECT_SLUG' 'roost') `
            -SigningPolicySlug (Env-Or 'SIGNPATH_SIGNING_POLICY_SLUG' 'release-signing') `
            -ArtifactConfigurationSlug (Env-Or 'SIGNPATH_PE_ARTIFACT_CONFIGURATION_SLUG' 'pe-file') `
            -InputArtifactPath $File -OutputArtifactPath $signed `
            -WaitForCompletion -WaitForCompletionTimeoutInSeconds 3600 -Force | Out-Host
        Move-Item $signed $File -Force
    }
}

$sig = Get-AuthenticodeSignature $File
if ($sig.Status -ne 'Valid') { throw "$File no ha quedado con una firma valida ($($sig.Status))" }
Write-Host "Firmado: $File ($($sig.SignerCertificate.Subject))"
