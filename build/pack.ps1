<#
.SYNOPSIS
  Publishes Image Digest Advisor and creates Velopack release packages (Setup.exe, portable zip, update packages).

.EXAMPLE
  ./build/pack.ps1 -Version 1.2.0 -Runtime win-x64
  ./build/pack.ps1 -Version 1.2.0 -Runtime win-arm64 -OutputDir artifacts/releases
#>
param(
    [Parameter(Mandatory)] [string] $Version,
    [ValidateSet('win-x64', 'win-arm64')] [string] $Runtime = 'win-x64',
    [string] $OutputDir = 'artifacts/releases',
    [string] $ReleaseNotes
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
Push-Location $root
try {
    $platform = if ($Runtime -eq 'win-arm64') { 'ARM64' } else { 'x64' }
    $publishDir = Join-Path $root "artifacts/publish/$Runtime"
    if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }

    Write-Host "Publishing $Version for $Runtime..."
    dotnet publish src/ImageDigestAdvisor.App -c Release -p:Platform=$platform -p:Unpackaged=true `
        -p:Version=$Version -p:PublishDir="$publishDir/"
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }

    Write-Host "Packing with Velopack..."
    $packArgs = @(
        'vpk', 'pack',
        '--packId', 'ImageDigestAdvisor',
        '--packVersion', $Version,
        '--packTitle', 'Image Digest Advisor',
        '--packAuthors', 'Holger Vetter',
        '--packDir', $publishDir,
        '--mainExe', 'ImageDigestAdvisor.exe',
        '--icon', 'src/ImageDigestAdvisor.App/Assets/AppIcon.ico',
        '--runtime', $Runtime,
        '--channel', $Runtime,
        '--shortcuts', 'StartMenuRoot,Desktop',
        '--outputDir', $OutputDir
    )
    if ($ReleaseNotes) { $packArgs += @('--releaseNotes', $ReleaseNotes) }
    dotnet @packArgs
    if ($LASTEXITCODE -ne 0) { throw "vpk pack failed" }
}
finally {
    Pop-Location
}
