<#
.SYNOPSIS
  Installs a Windows Setup.exe produced by HCG Executable Maker, verifies it, launches the app, uninstalls it and
  confirms cleanup. Exits non-zero on the first failed check.

.DESCRIPTION
  Runs a real installation for the CURRENT USER (no administrator rights) into a temporary folder:
    1. silent install with the optional desktop shortcut selected
    2. application files, uninstaller, Start-menu and desktop shortcuts (and their targets), uninstall registration
    3. launch the installed executable and confirm it keeps running
    4. silent uninstall
    5. folder, shortcuts and uninstall registration removed
  Intended for CI runners and test PCs. It changes the current user's profile while it runs.

.EXAMPLE
  ./tools/installer-test/Test-Installer.ps1 -Setup .\dist\HCG-Executable-Maker-0.2.0-win-x64-Setup.exe `
      -AppName "HCG Executable Maker" -ExeName HCGExecutableMaker.exe -Version 0.2.0
#>
param(
    [Parameter(Mandatory)] [string] $Setup,
    [Parameter(Mandatory)] [string] $AppName,
    [Parameter(Mandatory)] [string] $ExeName,
    [string] $Version,
    [int] $LaunchSeconds = 8
)
$ErrorActionPreference = 'Stop'
$failures = 0
function Check([bool] $ok, [string] $what) {
    if ($ok) { Write-Host "  PASS  $what" } else { Write-Host "  FAIL  $what" -ForegroundColor Red; $script:failures++ }
}

$Setup = (Resolve-Path $Setup).Path
$work = Join-Path ([IO.Path]::GetTempPath()) ("hcg-install-test-" + [Guid]::NewGuid().ToString('N').Substring(0, 8))
New-Item -ItemType Directory -Path $work | Out-Null
$installDir = Join-Path $work 'App'
$log = Join-Path $work 'install.log'
$startMenu = Join-Path ([Environment]::GetFolderPath('Programs')) "$AppName.lnk"
$desktop = Join-Path ([Environment]::GetFolderPath('DesktopDirectory')) "$AppName.lnk"
$exe = Join-Path $installDir $ExeName
function Find-UninstallEntry {
    Get-ChildItem 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall' -ErrorAction SilentlyContinue |
        Where-Object { $_.PSChildName -like '*_is1' -and (Get-ItemProperty $_.PSPath).DisplayName -eq $AppName }
}

Write-Host "Setup:   $Setup ($((Get-Item $Setup).Length) bytes, SHA-256 $((Get-FileHash $Setup -Algorithm SHA256).Hash))"
Write-Host "Install: $installDir"

# 1. Install
$p = Start-Process -FilePath $Setup -PassThru -Wait -ArgumentList @(
    '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/CURRENTUSER', '/TASKS=desktopicon', "/DIR=`"$installDir`"", "/LOG=`"$log`"")
Check ($p.ExitCode -eq 0) "Setup exited with code 0 (was $($p.ExitCode))"
if ($p.ExitCode -ne 0 -and (Test-Path $log)) { Get-Content $log | Select-Object -Last 40 }

# 2. Verify the installation
Check (Test-Path $exe) "Application executable installed: $exe"
Check (Test-Path (Join-Path $installDir 'unins000.exe')) 'Uninstaller installed (unins000.exe)'
$fileCount = (Get-ChildItem $installDir -Recurse -File -ErrorAction SilentlyContinue).Count
Check ($fileCount -gt 2) "Application files installed ($fileCount files)"
$shell = New-Object -ComObject WScript.Shell
foreach ($lnk in @($startMenu, $desktop)) {
    Check (Test-Path $lnk) "Shortcut exists: $lnk"
    if (Test-Path $lnk) { Check ($shell.CreateShortcut($lnk).TargetPath -ieq $exe) "Shortcut points to $exe" }
}
$entry = Find-UninstallEntry
Check ($null -ne $entry) "Uninstall registration present (Settings > Apps)"
if ($entry) {
    $props = Get-ItemProperty $entry.PSPath
    Write-Host "         $($entry.PSChildName): $($props.DisplayName) $($props.DisplayVersion) — $($props.Publisher)"
    if ($Version) { Check ($props.DisplayVersion -eq $Version) "Registered version is $Version" }
    Check ($props.UninstallString -like '*unins000.exe*') 'UninstallString points to unins000.exe'
}

# 3. Launch
if (Test-Path $exe) {
    $app = Start-Process -FilePath $exe -WorkingDirectory $installDir -PassThru
    Start-Sleep -Seconds $LaunchSeconds
    $running = -not $app.HasExited
    Check $running "Installed application launched and is still running after $LaunchSeconds s"
    if (-not $running) { Write-Host "         exit code: $($app.ExitCode)" }
    if ($running) { Stop-Process -Id $app.Id -Force; $app.WaitForExit() }
}

# 4. Uninstall
$uninstaller = Join-Path $installDir 'unins000.exe'
if (Test-Path $uninstaller) {
    $u = Start-Process -FilePath $uninstaller -PassThru -Wait -ArgumentList @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART')
    Check ($u.ExitCode -eq 0) "Uninstaller exited with code 0 (was $($u.ExitCode))"
    # The uninstaller finishes from a temporary copy; wait for it to complete.
    $deadline = (Get-Date).AddSeconds(60)
    while ((Test-Path $installDir) -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 500 }
}

# 5. Cleanup
Check (-not (Test-Path $installDir)) 'Install folder removed'
Check (-not (Test-Path $startMenu)) 'Start-menu shortcut removed'
Check (-not (Test-Path $desktop)) 'Desktop shortcut removed'
Check ($null -eq (Find-UninstallEntry)) 'Uninstall registration removed'

Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
if ($failures -gt 0) { Write-Host "$failures check(s) failed." -ForegroundColor Red; exit 1 }
Write-Host 'Installer verified: install, shortcuts, registration, launch, uninstall and cleanup.' -ForegroundColor Green
