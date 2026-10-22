<#
.SYNOPSIS
    Installs the Android SDK pieces needed to run an emulator and creates the "Resit" AVD,
    tuned to use as much of the host machine as is sensible.

.DESCRIPTION
    - Downloads the Android command-line tools (which include the native `android` CLI,
      so no Java is needed).
    - Installs platform-tools, the emulator and the newest stable x86_64 system image.
    - Writes the AVD with a config.ini sized from the host's CPU, RAM and disk.
    - Saves the chosen paths to resit.local.json so start.ps1 can find everything.

    Nothing is written outside -SdkRoot / -AvdHome except a per-user GPU preference
    (HKCU, skip with -SkipGpuPreference) so hybrid-graphics laptops use the fast GPU.

.EXAMPLE
    .\setup.ps1
.EXAMPLE
    .\setup.ps1 -SdkRoot E:\Android\Sdk -AvdHome E:\Android\avd -ImageType google_apis
#>
[CmdletBinding()]
param(
    [string]$AvdName = 'Resit',
    [string]$SdkRoot = $(if ($env:ANDROID_HOME) { $env:ANDROID_HOME } elseif ($env:ANDROID_SDK_ROOT) { $env:ANDROID_SDK_ROOT } else { Join-Path $env:LOCALAPPDATA 'Android\Sdk' }),
    [string]$AvdHome = $(if ($env:ANDROID_AVD_HOME) { $env:ANDROID_AVD_HOME } else { Join-Path $env:USERPROFILE '.android\avd' }),
    # Android API level, e.g. '35' or '37.0'. Empty = newest stable one with an x86_64 image of -ImageType.
    [string]$ApiLevel = '',
    [ValidateSet('google_apis_playstore', 'google_apis', 'default')]
    [string]$ImageType = 'google_apis_playstore',
    # Screen: widescreen like other gaming emulators (portrait apps turn the window).
    [int]$LcdWidth = 1920,
    [int]$LcdHeight = 1080,
    [int]$LcdDensity = 280,
    # 0 = auto (see Get-HostProfile).
    [int]$Cores = 0,
    [int]$RamMB = 0,
    [int]$DataGB = 0,
    # Delete and recreate the AVD (wipes its apps and data) if it already exists.
    [switch]$Force,
    [switch]$SkipGpuPreference
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

$RepoUrl = 'https://dl.google.com/android/repository'
$StateFile = Join-Path $PSScriptRoot 'resit.local.json'

function Write-Step([string]$Message) { Write-Host "==> $Message" -ForegroundColor Cyan }

# curl.exe ships with Windows 10 1803+; it shows progress, follows redirects and resumes partial files.
function Get-File([string]$Url, [string]$OutFile) {
    curl.exe -fL --retry 5 -C - --progress-bar -o $OutFile $Url
    if ($LASTEXITCODE -ne 0) { throw "Download failed ($LASTEXITCODE): $Url" }
}

function Get-HostProfile {
    $cpu = @(Get-CimInstance Win32_Processor)
    $physical = ($cpu | Measure-Object NumberOfCores -Sum).Sum
    $logical = ($cpu | Measure-Object NumberOfLogicalProcessors -Sum).Sum
    $ramMB = [int]((Get-CimInstance Win32_ComputerSystem).TotalPhysicalMemory / 1MB)
    $gpus = @(Get-CimInstance Win32_VideoController | Where-Object { $_.Name -notmatch 'Virtual|Basic Display|Remote|Mirage|Parsec' })
    [pscustomobject]@{
        CpuName       = ($cpu[0].Name).Trim()
        PhysicalCores = $physical
        LogicalCores  = $logical
        RamMB         = $ramMB
        Gpus          = $gpus.Name
        # One vCPU per physical core: past that, SMT siblings add contention rather than speed,
        # and the host still has its hyperthreads for the emulator's render/IO threads.
        Cores         = [Math]::Max(2, [Math]::Min(16, $physical))
        # 4 GB suits most games. More only makes Quick Boot slower: the snapshot holds all of
        # the guest RAM, so it is written on every close and read on every start.
        GuestRamMB    = [Math]::Max(2048, [Math]::Min(4096, [int][Math]::Floor($ramMB / 2 / 1024) * 1024))
    }
}

function Install-CmdlineTools {
    if (Test-Path $AndroidCli) { return }

    Write-Step 'Downloading Android command-line tools'
    $repo = [xml](Invoke-WebRequest -UseBasicParsing "$RepoUrl/repository2-3.xml").Content
    $node = $repo.SelectSingleNode("//remotePackage[@path='cmdline-tools;latest']")
    $file = ($node.archives.archive | Where-Object { $_.'host-os' -eq 'windows' }).complete.url
    $tmp = Join-Path ([IO.Path]::GetTempPath()) "resit-$([guid]::NewGuid())"
    New-Item -ItemType Directory $tmp | Out-Null
    try {
        Get-File "$RepoUrl/$file" (Join-Path $tmp $file)
        tar.exe -xf (Join-Path $tmp $file) -C $tmp
        $dest = Join-Path $SdkRoot 'cmdline-tools\latest'
        if (Test-Path $dest) { Remove-Item -Recurse -Force $dest }
        New-Item -ItemType Directory -Force (Split-Path $dest) | Out-Null
        Move-Item (Join-Path $tmp 'cmdline-tools') $dest
    } finally {
        Remove-Item -Recurse -Force $tmp
    }
    if (-not (Test-Path $AndroidCli)) { throw "These command-line tools have no android.exe ($AndroidCli)." }
}

# Run the `android` CLI with output shown live. (Not redirected, so Windows PowerShell
# doesn't turn its stderr notices into terminating errors.)
function Invoke-Android([string[]]$Arguments) {
    & $AndroidCli --no-metrics "--sdk=$SdkRoot" @Arguments
    if ($LASTEXITCODE -ne 0) { throw "android $($Arguments -join ' ') failed ($LASTEXITCODE)" }
}

# Run the `android` CLI and capture its output. cmd merges stderr so PowerShell never sees it.
function Get-AndroidOutput([string]$Arguments) {
    cmd /c "`"$AndroidCli`" --no-metrics `"--sdk=$SdkRoot`" $Arguments 2>&1"
}

function Select-SystemImage {
    if ($ApiLevel) { return "system-images/android-$ApiLevel/$ImageType/x86_64" }
    # Matches android-35 or android-37.0 but not -extNN, -betaN or the _ps16k / _tablet variants.
    $pattern = "^\s+(system-images/android-(\d+)(?:\.(\d+))?/$ImageType/x86_64)\s"
    $best = Get-AndroidOutput 'sdk list --all' | ForEach-Object {
        if ($_ -match $pattern) {
            [pscustomobject]@{ Id = $Matches[1]; Major = [int]$Matches[2]; Minor = [int]$Matches[3] }
        }
    } | Sort-Object Major, Minor -Descending | Select-Object -First 1
    if (-not $best) { throw "No stable x86_64 $ImageType system image found." }
    return $best.Id
}

function Set-IniValues([string]$Path, [Collections.IDictionary]$Values) {
    $lines = [Collections.Generic.List[string]]::new()
    if (Test-Path $Path) { $lines.AddRange([string[]](Get-Content $Path)) }
    foreach ($key in $Values.Keys) {
        $line = "$key=$($Values[$key])"
        $pattern = "^\s*$([regex]::Escape($key))\s*="
        $i = 0
        while ($i -lt $lines.Count -and $lines[$i] -notmatch $pattern) { $i++ }
        if ($i -lt $lines.Count) { $lines[$i] = $line } else { $lines.Add($line) }
    }
    # The emulator reads these files as plain ASCII; avoid PowerShell 5's UTF-16/BOM defaults.
    [IO.File]::WriteAllLines($Path, $lines, [Text.ASCIIEncoding]::new())
}

function Set-HighPerformanceGpu([string[]]$Exes) {
    $key = 'HKCU:\Software\Microsoft\DirectX\UserGpuPreferences'
    if (-not (Test-Path $key)) { New-Item $key -Force | Out-Null }
    foreach ($exe in $Exes) {
        if (Test-Path $exe) { New-ItemProperty $key -Name $exe -Value 'GpuPreference=2;' -PropertyType String -Force | Out-Null }
    }
}

# ---------------------------------------------------------------------------

$AndroidCli = Join-Path $SdkRoot 'cmdline-tools\latest\bin\android.exe'

$hw = Get-HostProfile
if ($Cores -le 0) { $Cores = $hw.Cores }
if ($RamMB -le 0) { $RamMB = $hw.GuestRamMB }
New-Item -ItemType Directory -Force $SdkRoot, $AvdHome | Out-Null
if ($DataGB -le 0) {
    # 32 GB of app storage, unless the AVD drive is short on space.
    $free = (Get-Item $AvdHome).PSDrive.Free
    $DataGB = if ($free -and $free / 1GB -lt 60) { 8 } else { 32 }
}

Write-Host ''
Write-Host "Host   : $($hw.CpuName)  ($($hw.PhysicalCores)C/$($hw.LogicalCores)T, $([Math]::Round($hw.RamMB / 1024)) GB RAM)"
Write-Host "GPU    : $($hw.Gpus -join ', ')"
Write-Host "Guest  : $Cores vCPU, $RamMB MB RAM, $DataGB GB data, ${LcdWidth}x$LcdHeight @ $LcdDensity dpi, host GPU rendering"
Write-Host "SDK    : $SdkRoot"
Write-Host "AVDs   : $AvdHome"
Write-Host ''

$env:ANDROID_HOME = $SdkRoot
$env:ANDROID_SDK_ROOT = $SdkRoot
$env:ANDROID_AVD_HOME = $AvdHome

Install-CmdlineTools

Write-Step 'Finding the newest stable system image'
$image = Select-SystemImage
Write-Host "    $image"

Write-Host ''
Write-Host 'Installing Android SDK packages means accepting the Android SDK License Agreement:'
Write-Host '    https://developer.android.com/studio/terms'
Write-Host ''
Write-Step "Installing platform-tools, emulator and $image (several GB on first run)"
Invoke-Android @('sdk', 'install', 'platform-tools', 'emulator', $image)

$imageDir = Join-Path $SdkRoot ($image -replace '/', '\')
if (-not (Test-Path (Join-Path $imageDir 'system.img'))) { throw "System image did not install to $imageDir" }
$emulator = Join-Path $SdkRoot 'emulator\emulator.exe'

Write-Step 'Checking hardware acceleration'
$accel = (cmd /c "`"$emulator`" -accel-check 2>&1") -join "`n"
$accelOk = $accel -match 'is installed and usable'
if ($accelOk) {
    Write-Host ('    ' + ($accel -split "`n" | Where-Object { $_ -match 'usable' } | Select-Object -First 1)) -ForegroundColor Green
} else {
    Write-Warning "No usable hypervisor; the emulator will be extremely slow until one is enabled.`n$accel`nRun .\enable-hypervisor.ps1 (asks for admin, then reboot)."
}

$avdDir = Join-Path $AvdHome "$AvdName.avd"
$avdIni = Join-Path $AvdHome "$AvdName.ini"
if ((Test-Path $avdDir) -and $Force) {
    Write-Step "Deleting existing AVD '$AvdName'"
    Remove-Item -Recurse -Force $avdDir
}
Write-Step "Writing AVD '$AvdName'"
New-Item -ItemType Directory -Force $avdDir | Out-Null

$apiId = ($image -split '/')[1]                 # e.g. android-37.0
$tagDisplay = @{ google_apis_playstore = 'Google Play'; google_apis = 'Google APIs'; default = 'Default Android System Image' }[$ImageType]

[IO.File]::WriteAllLines($avdIni, [string[]]@(
    'avd.ini.encoding=UTF-8',
    "path=$avdDir",
    "target=$apiId"
), [Text.ASCIIEncoding]::new())

Set-IniValues (Join-Path $avdDir 'config.ini') ([ordered]@{
    'AvdId'                    = $AvdName
    'avd.ini.displayname'      = $AvdName
    'avd.ini.encoding'         = 'UTF-8'
    'target'                   = $apiId
    'image.sysdir.1'           = ($image -replace '/', '\') + '\'
    'tag.id'                   = $ImageType
    'tag.display'              = $tagDisplay
    'abi.type'                 = 'x86_64'
    'hw.cpu.arch'              = 'x86_64'
    'PlayStore.enabled'        = ($ImageType -eq 'google_apis_playstore').ToString().ToLower()

    'hw.cpu.ncore'             = $Cores
    'hw.ramSize'               = $RamMB
    # Per-app Dalvik heap; 512 MB is the ceiling real flagship devices ship with.
    'vm.heapSize'              = 512
    'disk.dataPartition.size'  = "${DataGB}G"
    'hw.sdCard'                = 'no'
    'hw.gpu.enabled'           = 'yes'
    'hw.gpu.mode'              = 'host'

    # No hw.device.name: naming a phone makes the emulator add its camera cutout and rounded corners.
    'hw.lcd.width'             = $LcdWidth
    'hw.lcd.height'            = $LcdHeight
    'hw.lcd.density'           = $LcdDensity
    'hw.initialOrientation'    = $(if ($LcdWidth -gt $LcdHeight) { 'landscape' } else { 'portrait' })
    'showDeviceFrame'          = 'no'
    'hw.keyboard'              = 'yes'
    'hw.mainKeys'              = 'no'
    'hw.dPad'                  = 'no'
    'hw.trackBall'             = 'no'
    'hw.accelerometer'         = 'yes'
    'hw.gyroscope'             = 'yes'
    'hw.sensors.orientation'   = 'yes'
    'hw.sensors.proximity'     = 'yes'
    'hw.gps'                   = 'yes'
    'hw.battery'               = 'yes'
    # Mic off: whenever Android opens it, Bluetooth headphones drop into call mode and lose sound.
    'hw.audioInput'            = 'no'
    'hw.audioOutput'           = 'yes'
    'hw.camera.back'           = 'emulated'
    'hw.camera.front'          = 'emulated'
    'runtime.network.latency'  = 'none'
    'runtime.network.speed'    = 'full'

    # Quick Boot: save a snapshot on exit and resume from it in a few seconds.
    'fastboot.forceColdBoot'   = 'no'
    'fastboot.forceFastBoot'   = 'yes'
})

if (-not $SkipGpuPreference) {
    Set-HighPerformanceGpu @(
        $emulator,
        (Join-Path $SdkRoot 'emulator\qemu\windows-x86_64\qemu-system-x86_64.exe'),
        (Join-Path $SdkRoot 'emulator\qemu\windows-x86_64\qemu-system-x86_64-headless.exe')
    )
}

# Where Resit finds the SDK: next to this script (repo checkout) and in its settings folder
# (a downloaded Resit.exe).
$state = [ordered]@{
    avdName = $AvdName
    sdkRoot = $SdkRoot
    avdHome = $AvdHome
    image   = $image
    cores   = $Cores
    ramMB   = $RamMB
} | ConvertTo-Json
$state | Set-Content $StateFile -Encoding ASCII
$settingsDir = Join-Path $env:APPDATA 'Resit'
New-Item -ItemType Directory -Force $settingsDir | Out-Null
$state | Set-Content (Join-Path $settingsDir 'resit.local.json') -Encoding ASCII

Write-Host ''
Write-Host "Done. In Resit, press Play on '$AvdName' (or run .\start.ps1 from the repo)." -ForegroundColor Green
if (-not $accelOk) { Write-Host 'But enable a hypervisor first: the Enable button in Resit, or .\enable-hypervisor.ps1' -ForegroundColor Yellow }
