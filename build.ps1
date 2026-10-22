<#
.SYNOPSIS
    Builds Resit as single self-contained files (users need no .NET install):
    dist\windows\Resit.exe, and for Linux dist\linux\ plus dist\resit-linux-x64.tar.gz.

.EXAMPLE
    .\build.ps1                  # Windows and Linux
.EXAMPLE
    .\build.ps1 -Target windows
#>
param(
    [ValidateSet('all', 'windows', 'linux')]
    [string]$Target = 'all',
    [string]$Configuration = 'Release'
)
$ErrorActionPreference = 'Stop'

# The first dotnet that has an SDK (a runtime-only install on PATH cannot build).
$candidates = @((Get-Command dotnet -ErrorAction SilentlyContinue).Source, (Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet\dotnet.exe')) |
    Where-Object { $_ -and (Test-Path $_) }
$dotnet = $candidates | Where-Object { (& $_ --list-sdks 2>$null) -match '^10\.' } | Select-Object -First 1
if (-not $dotnet) { throw 'Install the .NET 10 SDK: https://dotnet.microsoft.com/download' }

$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:AVALONIA_TELEMETRY_OPTOUT = '1'
$project = Join-Path $PSScriptRoot 'src\Resit\Resit.csproj'
$dist = Join-Path $PSScriptRoot 'dist'

function Publish([string]$Runtime, [string]$Out) {
    & $dotnet publish $project -c $Configuration -r $Runtime --self-contained `
        -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true `
        -p:DebugType=none -o $Out
    if ($LASTEXITCODE -ne 0) { throw "Build for $Runtime failed ($LASTEXITCODE)" }
    # Debug symbols of the native graphics libraries (100 MB) are of no use to players.
    Get-ChildItem $Out -Filter *.pdb | Remove-Item
}

# Shell scripts must keep Unix line endings even when edited on Windows.
function Copy-Unix([string]$From, [string]$To) {
    $text = [IO.File]::ReadAllText($From) -replace "`r`n", "`n"
    [IO.File]::WriteAllText($To, $text, [Text.UTF8Encoding]::new($false))
}

if ($Target -in 'all', 'windows') {
    Publish 'win-x64' (Join-Path $dist 'windows')
    Write-Host "Built $(Join-Path $dist 'windows\Resit.exe')" -ForegroundColor Green
}

if ($Target -in 'all', 'linux') {
    $linux = Join-Path $dist 'linux'
    Publish 'linux-x64' $linux
    Copy-Unix (Join-Path $PSScriptRoot 'setup.sh') (Join-Path $linux 'setup.sh')
    Copy-Unix (Join-Path $PSScriptRoot 'linux\install.sh') (Join-Path $linux 'install.sh')
    Copy-Unix (Join-Path $PSScriptRoot 'linux\resit.desktop') (Join-Path $linux 'resit.desktop')
    Copy-Item (Join-Path $PSScriptRoot 'assets\logo-256.png') (Join-Path $linux 'resit.png')
    $archive = Join-Path $dist 'resit-linux-x64.tar.gz'
    Push-Location $PSScriptRoot
    try { & $dotnet run (Join-Path $PSScriptRoot 'tools\pack-linux.cs') -- $linux $archive }
    finally { Pop-Location }
    if ($LASTEXITCODE -ne 0) { throw "Packing failed ($LASTEXITCODE)" }
    Write-Host "Built $archive" -ForegroundColor Green
}
