<#
.SYNOPSIS
    Starts the Resit emulator with host GPU rendering, full hardware acceleration and
    high process priority, then waits until Android has finished booting.

.EXAMPLE
    .\start.ps1
.EXAMPLE
    .\start.ps1 -ColdBoot -NoAudio
#>
[CmdletBinding()]
param(
    # Ignore the Quick Boot snapshot and boot Android from scratch.
    [switch]$ColdBoot,
    # Factory-reset the device before booting.
    [switch]$Wipe,
    # Skip audio emulation (saves a little CPU).
    [switch]$NoAudio,
    # Run without a window, e.g. for CI or adb-only use.
    [switch]$Headless,
    # Return as soon as the emulator process starts instead of waiting for boot.
    [switch]$NoWait,
    # Anything else to pass straight to emulator.exe.
    [string[]]$ExtraArgs = @()
)

$ErrorActionPreference = 'Stop'
$StateFile = Join-Path $PSScriptRoot 'resit.local.json'
if (-not (Test-Path $StateFile)) { throw "Run .\setup.ps1 first ($StateFile not found)." }
$state = Get-Content $StateFile -Raw | ConvertFrom-Json

$env:ANDROID_HOME = $state.sdkRoot
$env:ANDROID_SDK_ROOT = $state.sdkRoot
$env:ANDROID_AVD_HOME = $state.avdHome
$emulator = Join-Path $state.sdkRoot 'emulator\emulator.exe'
$adb = Join-Path $state.sdkRoot 'platform-tools\adb.exe'

$args_ = @('-avd', $state.avdName, '-gpu', 'host', '-accel', 'on', '-no-boot-anim')
if ($ColdBoot) { $args_ += '-no-snapshot-load' }
if ($Wipe) { $args_ += '-wipe-data' }
if ($NoAudio) { $args_ += '-no-audio' }
if ($Headless) { $args_ += '-no-window' }
$args_ += $ExtraArgs

Write-Host "Starting $($state.avdName): $($state.cores) vCPU, $($state.ramMB) MB RAM" -ForegroundColor Cyan
$started = Get-Date
$logDir = Join-Path $state.avdHome "$($state.avdName).avd"
Start-Process $emulator -ArgumentList $args_ -WindowStyle Hidden `
    -RedirectStandardOutput (Join-Path $logDir 'resit-emulator.log') `
    -RedirectStandardError (Join-Path $logDir 'resit-emulator.err.log') | Out-Null

# emulator.exe is a launcher; the VM itself runs in a qemu-system child process.
$qemu = $null
for ($i = 0; $i -lt 60 -and -not $qemu; $i++) {
    Start-Sleep -Milliseconds 500
    $qemu = Get-Process qemu-system-x86_64* -ErrorAction SilentlyContinue |
        Where-Object { $_.StartTime -ge $started.AddSeconds(-2) } | Select-Object -First 1
}
if (-not $qemu) { throw "Emulator did not start. See $logDir\resit-emulator*.log" }
try {
    $qemu.PriorityClass = [Diagnostics.ProcessPriorityClass]::High
    Write-Host "VM process $($qemu.Id) set to High priority"
} catch {
    Write-Warning "Could not raise VM priority: $($_.Exception.Message)"
}

if ($NoWait) { return }

Write-Host 'Waiting for Android to boot...'
& $adb wait-for-device | Out-Null
do {
    Start-Sleep -Seconds 1
    $booted = (& $adb shell getprop sys.boot_completed 2>$null) -join ''
    if ($qemu.HasExited) { throw "Emulator exited during boot. See $logDir\resit-emulator*.log" }
} until ($booted.Trim() -eq '1')

$secs = [Math]::Round(((Get-Date) - $started).TotalSeconds, 1)
Write-Host "$($state.avdName) is ready ($secs s)." -ForegroundColor Green
