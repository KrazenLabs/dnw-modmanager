<#
.SYNOPSIS
  Builds the DnW Mod Manager and assembles the release:
  release\DnWModManager-<version>.zip - self-contained .exe.

.PARAMETER Configuration
  Release (default) or Debug.

.PARAMETER Framework
  Also produce the small framework-dependent build (needs the .NET 8 Runtime). 
  Off by default.
#>
param(
    [string]$Configuration = "Release",
    [switch]$Framework
)

$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$project = Join-Path $root "src\DnWModManager\DnWModManager.csproj"
$release = Join-Path $root "release"

$version = ([xml](Get-Content (Join-Path $root "Directory.Build.props"))).Project.PropertyGroup |
    ForEach-Object { $_.DnwManagerVersion } | Where-Object { $_ } | Select-Object -First 1
if (-not $version) { $version = "0.0.0" }

Write-Host "Building DnW Mod Manager $version ($Configuration)..." -ForegroundColor Cyan

if (Test-Path $release) { Remove-Item $release -Recurse -Force }
New-Item -ItemType Directory -Force -Path $release | Out-Null

$selfContained = Join-Path $release "self-contained"
& dotnet publish $project -c $Configuration -r win-x64 --self-contained true -nologo `
    -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -o $selfContained
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE" }

$exe = Join-Path $selfContained "DnWModManager.exe"
if (-not (Test-Path $exe)) { throw "The published executable is missing: $exe" }

$zip = Join-Path $release "DnWModManager-$version.zip"
Compress-Archive -Path $exe -DestinationPath $zip -Force

$megabytes = [Math]::Round((Get-Item $exe).Length / 1MB, 1)
$zipMegabytes = [Math]::Round((Get-Item $zip).Length / 1MB, 1)

# optional
if ($Framework) {
    $frameworkDir = Join-Path $release "framework-dependent"
    & dotnet publish $project -c $Configuration -r win-x64 --self-contained false -nologo `
        -p:PublishSingleFile=true -o $frameworkDir
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish (framework-dependent) failed with exit code $LASTEXITCODE" }

    $frameworkZip = Join-Path $release "DnWModManager-$version-needs-dotnet8.zip"
    Compress-Archive -Path (Join-Path $frameworkDir "*") -DestinationPath $frameworkZip -Force
    Write-Host "  small build:  $frameworkZip  ($([Math]::Round((Get-Item $frameworkZip).Length / 1MB, 1)) MB, needs the .NET 8 Desktop Runtime)"
}

Remove-Item $selfContained -Recurse -Force
if ($Framework) { Remove-Item (Join-Path $release "framework-dependent") -Recurse -Force }

Write-Host ""
Write-Host "Done: $zip" -ForegroundColor Green
Write-Host "  DnWModManager.exe is $megabytes MB; the zip is $zipMegabytes MB."
Write-Host "  It runs from anywhere and needs nothing installed."
