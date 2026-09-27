<#
.SYNOPSIS
  Builds the DnW Mod Manager and assembles the release:
  release\DnWModManager.exe - self-contained Windows exe.
  release\DnWModManager-linux-x64.zip - self-contained Linux executable (DnWModManager, marked executable).

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

function Set-ZipUnixMode([string]$zipPath, [string[]]$entryNames, [int]$mode) {
    $bytes = [System.IO.File]::ReadAllBytes($zipPath)
    $end = -1
    for ($i = $bytes.Length - 22; $i -ge 0; $i--) {
        if ([BitConverter]::ToUInt32($bytes, $i) -eq 0x06054b50) { $end = $i; break }
    }
    if ($end -lt 0) { throw "$zipPath has no end of central directory record." }
    $count = [BitConverter]::ToUInt16($bytes, $end + 10)
    $offset = [int][BitConverter]::ToUInt32($bytes, $end + 16)
    $attributes = [BitConverter]::GetBytes([uint32]([int64](0x8000 -bor $mode) * 65536))
    $marked = @()
    for ($n = 0; $n -lt $count; $n++) {
        if ([BitConverter]::ToUInt32($bytes, $offset) -ne 0x02014b50) { throw "$zipPath has a damaged central directory." }
        $nameLength = [BitConverter]::ToUInt16($bytes, $offset + 28)
        $extraLength = [BitConverter]::ToUInt16($bytes, $offset + 30)
        $commentLength = [BitConverter]::ToUInt16($bytes, $offset + 32)
        $name = [System.Text.Encoding]::UTF8.GetString($bytes, $offset + 46, $nameLength)
        if ($entryNames -contains $name) {
            $bytes[$offset + 5] = 3
            [Array]::Copy($attributes, 0, $bytes, $offset + 38, 4)
            $marked += $name
        }
        $offset += 46 + $nameLength + $extraLength + $commentLength
    }
    foreach ($name in $entryNames) {
        if ($marked -notcontains $name) { throw "$name is not in $zipPath." }
    }
    [System.IO.File]::WriteAllBytes($zipPath, $bytes)
}

function Publish-Manager([string]$runtime, [string]$output) {
    Write-Host "Building DnW Mod Manager $version ($Configuration, $runtime)..." -ForegroundColor Cyan
    & dotnet publish $project -c $Configuration -r $runtime --self-contained true -nologo `
        -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -o $output
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish for $runtime failed with exit code $LASTEXITCODE" }
}

if (Test-Path $release) { Remove-Item $release -Recurse -Force }
New-Item -ItemType Directory -Force -Path $release | Out-Null

$windowsOut = Join-Path $release "win-x64"
Publish-Manager "win-x64" $windowsOut
$published = Join-Path $windowsOut "DnWModManager.exe"
if (-not (Test-Path $published)) { throw "The published executable is missing: $published" }
$exe = Join-Path $release "DnWModManager.exe"
Move-Item $published $exe
Remove-Item $windowsOut -Recurse -Force

$linuxOut = Join-Path $release "linux-x64"
Publish-Manager "linux-x64" $linuxOut
$linuxExecutable = Join-Path $linuxOut "DnWModManager"
if (-not (Test-Path $linuxExecutable)) { throw "The published Linux executable is missing: $linuxExecutable" }
$linuxZip = Join-Path $release "DnWModManager-linux-x64.zip"
Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
$archive = [System.IO.Compression.ZipFile]::Open($linuxZip, [System.IO.Compression.ZipArchiveMode]::Create)
try {
    [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $linuxExecutable, "DnWModManager", [System.IO.Compression.CompressionLevel]::Optimal) | Out-Null
}
finally {
    $archive.Dispose()
}
Set-ZipUnixMode $linuxZip @("DnWModManager") ([Convert]::ToInt32("755", 8))
Remove-Item $linuxOut -Recurse -Force

$exeMegabytes = [Math]::Round((Get-Item $exe).Length / 1MB, 1)
$zipMegabytes = [Math]::Round((Get-Item $linuxZip).Length / 1MB, 1)

Write-Host ""
Write-Host "Done:" -ForegroundColor Green
Write-Host "  $exe ($exeMegabytes MB)"
Write-Host "  $linuxZip ($zipMegabytes MB)"
