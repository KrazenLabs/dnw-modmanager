<#
.SYNOPSIS
  Builds the DnW Mod Manager and assembles the release:
  release\DnWModManager.exe - self-contained .exe.

.PARAMETER Configuration
  Release (default) or Debug.
#>
param(
    [string]$Configuration = "Release"
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

$published = Join-Path $selfContained "DnWModManager.exe"
if (-not (Test-Path $published)) { throw "The published executable is missing: $published" }

$exe = Join-Path $release "DnWModManager.exe"
Move-Item $published $exe

$megabytes = [Math]::Round((Get-Item $exe).Length / 1MB, 1)

Remove-Item $selfContained -Recurse -Force

Write-Host ""
Write-Host "Done: $exe" -ForegroundColor Green
Write-Host "  DnWModManager.exe $version is $megabytes MB."
Write-Host "  It runs from anywhere and needs nothing installed."
