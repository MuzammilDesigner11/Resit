<#
.SYNOPSIS
    Turns on hardware acceleration for the Android Emulator. Needs admin rights.

.DESCRIPTION
    Two options, both supported by Google:

    Aehd  Android Emulator Hypervisor Driver. No reboot, and Windows itself keeps running
          without Hyper-V. Other emulators (BlueStacks, MuMu, ...) keep working as before.
    Whpx  Windows Hypervisor Platform. Needs a reboot and turns on Hyper-V for the whole
          system; other emulators then need their Hyper-V compatible versions.

    Auto (the default) uses WHPX when Hyper-V is already running, otherwise AEHD.
    CPU virtualization (Intel VT-x / AMD SVM) must also be enabled in the BIOS/UEFI.

.EXAMPLE
    .\enable-hypervisor.ps1
.EXAMPLE
    .\enable-hypervisor.ps1 -Driver Whpx
#>
param(
    [ValidateSet('Auto', 'Aehd', 'Whpx')]
    [string]$Driver = 'Auto'
)
$ErrorActionPreference = 'Stop'

$hypervisorRunning = (Get-CimInstance Win32_ComputerSystem).HypervisorPresent
if ($Driver -eq 'Auto') { $Driver = if ($hypervisorRunning) { 'Whpx' } else { 'Aehd' } }

if (-not $hypervisorRunning -and -not (Get-CimInstance Win32_Processor | Select-Object -First 1).VirtualizationFirmwareEnabled) {
    Write-Warning 'CPU virtualization looks disabled in firmware. Enable Intel VT-x / AMD SVM in the BIOS first.'
}

if ($Driver -eq 'Aehd') {
    $state = Join-Path $PSScriptRoot 'resit.local.json'
    $sdk = if (Test-Path $state) { (Get-Content $state -Raw | ConvertFrom-Json).sdkRoot }
           elseif ($env:ANDROID_HOME) { $env:ANDROID_HOME }
           else { Join-Path $env:LOCALAPPDATA 'Android\Sdk' }
    $dir = Join-Path $sdk 'extras\google\Android_Emulator_Hypervisor_Driver'
    if (-not (Test-Path (Join-Path $dir 'silent_install.bat'))) {
        Write-Host '==> Downloading the Android Emulator Hypervisor Driver' -ForegroundColor Cyan
        & (Join-Path $sdk 'cmdline-tools\latest\bin\android.exe') --no-metrics "--sdk=$sdk" sdk install extras/google/Android_Emulator_Hypervisor_Driver
        if ($LASTEXITCODE -ne 0) { throw 'Could not download the driver.' }
    }
    Write-Host '==> Installing the driver (Windows will ask for admin rights)' -ForegroundColor Cyan
    $p = Start-Process cmd.exe -ArgumentList '/c', "`"$dir\silent_install.bat`"" -Verb RunAs -Wait -PassThru
    if ($p.ExitCode -ne 0) { throw "Driver install failed ($($p.ExitCode))." }
    Write-Host 'Android Emulator Hypervisor Driver installed. No reboot needed: run .\start.ps1' -ForegroundColor Green
    return
}

$principal = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Host 'Requesting administrator rights...'
    Start-Process powershell.exe -Verb RunAs -ArgumentList '-NoProfile', '-ExecutionPolicy', 'Bypass', '-NoExit', '-File', "`"$PSCommandPath`"", '-Driver', 'Whpx'
    return
}

$feature = Get-WindowsOptionalFeature -Online -FeatureName HypervisorPlatform
if ($feature.State -eq 'Enabled') {
    Write-Host 'Windows Hypervisor Platform is already enabled.' -ForegroundColor Green
    return
}
$result = Enable-WindowsOptionalFeature -Online -FeatureName HypervisorPlatform -All -NoRestart
if ($result.RestartNeeded) {
    Write-Host 'Windows Hypervisor Platform enabled. Reboot to finish, then run .\start.ps1' -ForegroundColor Yellow
} else {
    Write-Host 'Windows Hypervisor Platform enabled.' -ForegroundColor Green
}
