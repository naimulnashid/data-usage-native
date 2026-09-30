<#
.SYNOPSIS
  Builds the app and installs it for the current user. One UAC prompt, for
  the scheduled tasks only.

.DESCRIPTION
  1. Publishes a self-contained Release build (it carries its own .NET and
     Windows App SDK) into %LOCALAPPDATA%\Programs\Data Usage. A running copy
     is closed first, by the path it runs from - never by name. From a
     release zip, which carries that build in app\ beside this script,
     nothing is built: the build is copied as it is.
  2. Adds "Data Usage" to the Start menu.
  3. Registers it under Settings -> Apps -> Installed apps (and Control
     Panel's Programs and Features), per user, with Uninstall.ps1 copied into
     the program folder as its uninstall command - so it can be removed
     without this repo.
  4. Points the app at its data folder (-DataDir), unless one is already set.
  5. Starts tools\Register-Tasks.ps1 elevated, which registers the collector
     (unelevated, every 15 minutes) and the snapshot task (the one elevated
     step). This is the UAC prompt.

  Re-running updates the installed copy and re-registers the tasks. After a
  Windows reset, re-running it is the whole recovery: point -DataDir at the
  same folder and the history is back.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File tools\Install.ps1
.EXAMPLE
  powershell -ExecutionPolicy Bypass -File tools\Install.ps1 -Uninstall
.EXAMPLE
  powershell -ExecutionPolicy Bypass -File .\Install.ps1    # inside an extracted release zip
#>
param(
  [string]$DataDir = 'D:\PersistentData\data-usage-native',
  [switch]$Uninstall,
  [switch]$RemoveData,
  [switch]$SkipTasks
)
$ErrorActionPreference = 'Stop'

$root = Split-Path $PSScriptRoot -Parent
$target = Join-Path $env:LOCALAPPDATA 'Programs\Data Usage'
$exe = Join-Path $target 'DataUsage.exe'
$shortcut = Join-Path ([Environment]::GetFolderPath('Programs')) 'Data Usage.lnk'
$localDir = Join-Path $env:LOCALAPPDATA 'Data Usage Native'

if ($Uninstall) {
  # One implementation, shared with the Installed apps entry.
  & (Join-Path $PSScriptRoot 'Uninstall.ps1') -RemoveData:$RemoveData -Quiet
  return
}

function Stop-Installed {
  Get-Process DataUsage -ErrorAction SilentlyContinue |
    Where-Object { $_.Path -and ($_.Path -ieq $exe) } |
    ForEach-Object {
      Write-Host "Closing the running copy (PID $($_.Id))"
      Stop-Process -Id $_.Id -Force
      $_.WaitForExit(5000) | Out-Null
    }
}

# ---- 1. Publish and copy ------------------------------------------------------
# A release zip is this script, Uninstall.ps1 and Register-Tasks.ps1 beside
# app\, the published build. Anywhere else this is a clone, and it builds.
$packaged = Test-Path (Join-Path $PSScriptRoot 'app\DataUsage.exe')
if ($packaged) {
  $staging = Join-Path $PSScriptRoot 'app'
  Write-Host "Installing the build in $staging"
} else {
  $staging = Join-Path $root 'publish\DataUsage'
  if (Test-Path $staging) { Remove-Item $staging -Recurse -Force }
  Write-Host 'Publishing a Release build...'
  & dotnet publish (Join-Path $root 'src\DataUsage.App\DataUsage.App.csproj') -c Release -o $staging --nologo -v q
  if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE" }
}
# Without its own resources.pri the app dies at startup (0xC000027B) - what a
# publish without EnableMsixTooling produced. Never install that.
if (-not (Test-Path (Join-Path $staging 'DataUsage.pri'))) {
  throw 'The published build has no DataUsage.pri, so it would crash at startup. Check EnableMsixTooling in DataUsage.App.csproj.'
}

Stop-Installed
if (Test-Path $target) { Remove-Item $target -Recurse -Force }
New-Item -ItemType Directory -Force $target | Out-Null
Copy-Item (Join-Path $staging '*') $target -Recurse -Force
Copy-Item (Join-Path $PSScriptRoot 'Uninstall.ps1') (Join-Path $target 'Uninstall.ps1') -Force
Copy-Item (Join-Path $PSScriptRoot 'Register-Tasks.ps1') (Join-Path $target 'Register-Tasks.ps1') -Force
# Explorer marks every file it extracts from a downloaded zip as downloaded,
# and SmartScreen then stops the app at each launch. Running this script is
# the decision to install it, as it is for any installer: clear the mark on
# the installed copy only.
if ($packaged) { Get-ChildItem $target -Recurse -File | Unblock-File }

# ---- 2. Start menu -------------------------------------------------------------
$shell = New-Object -ComObject WScript.Shell
$link = $shell.CreateShortcut($shortcut)
$link.TargetPath = $exe
$link.WorkingDirectory = $target
$link.IconLocation = "$exe,0"
$link.Description = 'A permanent history of per-app network usage on this PC'
$link.Save()

# ---- 3. Installed apps -----------------------------------------------------------
$bytes = (Get-ChildItem $target -Recurse | Measure-Object Length -Sum).Sum
# From the exe, not Directory.Build.props: a release zip has no props file.
$version = ([version][Diagnostics.FileVersionInfo]::GetVersionInfo($exe).FileVersion).ToString(3)
$uninstallCommand = "powershell.exe -NoProfile -ExecutionPolicy Bypass -File `"$(Join-Path $target 'Uninstall.ps1')`""
$key = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\DataUsageNative'
New-Item -Path $key -Force | Out-Null
$entry = @{
  DisplayName = 'Data Usage'
  DisplayVersion = [string]$version
  Publisher = 'Naimul Nashid'
  DisplayIcon = "$exe,0"
  InstallLocation = $target
  UninstallString = $uninstallCommand
  QuietUninstallString = "$uninstallCommand -Quiet"
  URLInfoAbout = 'https://github.com/naimulnashid/data-usage-native'
  InstallDate = (Get-Date -Format 'yyyyMMdd')
}
foreach ($name in $entry.Keys) { New-ItemProperty -Path $key -Name $name -Value $entry[$name] -PropertyType String -Force | Out-Null }
# No Modify or Repair buttons: re-running this script is the repair.
foreach ($name in 'NoModify', 'NoRepair') { New-ItemProperty -Path $key -Name $name -Value 1 -PropertyType DWord -Force | Out-Null }
New-ItemProperty -Path $key -Name 'EstimatedSize' -Value ([int]($bytes / 1KB)) -PropertyType DWord -Force | Out-Null

# ---- 4. The data folder -----------------------------------------------------------
$location = Join-Path $localDir 'location.json'
if (Test-Path $location) {
  Write-Host "Data folder already set: $((Get-Content $location -Raw | ConvertFrom-Json).dataDir)"
} elseif ($DataDir) {
  $systemRoot = [IO.Path]::GetPathRoot($env:SystemRoot)
  $dataRoot = [IO.Path]::GetPathRoot([IO.Path]::GetFullPath($DataDir))
  if ($dataRoot -ieq $systemRoot) {
    Write-Host "Not setting ${DataDir}: it is on the system drive, which a reset erases. The app will ask on first run." -ForegroundColor Yellow
  } elseif (-not (Test-Path -LiteralPath $dataRoot)) {
    # The default names D:, which not every PC has.
    Write-Host "Not setting ${DataDir}: there is no $dataRoot drive. The app will ask on first run, or re-run with -DataDir." -ForegroundColor Yellow
  } else {
    New-Item -ItemType Directory -Force $DataDir | Out-Null
    New-Item -ItemType Directory -Force $localDir | Out-Null
    $json = @{ dataDir = [IO.Path]::GetFullPath($DataDir); allowSystemDrive = $false } | ConvertTo-Json
    # BOM-less: Windows PowerShell's utf8 writes a BOM.
    [IO.File]::WriteAllText($location, $json, (New-Object Text.UTF8Encoding($false)))
    Write-Host "Data folder: $DataDir"
  }
}

# ---- 5. The scheduled tasks (the UAC prompt) ----------------------------------------
if (-not $SkipTasks) {
  $sid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
  $register = Join-Path $target 'Register-Tasks.ps1'
  Write-Host 'Registering the scheduled tasks (Windows will ask for administrator approval)...'
  $elevated = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$register`"", '-Exe', "`"$exe`"", '-UserSid', $sid)
  # The exported definitions are a record kept in a clone; a release zip has
  # nowhere to keep them.
  if (-not $packaged) { $elevated += @('-ExportDir', "`"$(Join-Path $root 'tools\task')`"") }
  $p = Start-Process powershell.exe -Verb RunAs -ArgumentList $elevated -Wait -PassThru
  if ($p.ExitCode -ne 0) { throw "Registering the scheduled tasks failed (exit $($p.ExitCode)). Nothing will collect until it succeeds: re-run this script." }
  Write-Host 'Scheduled tasks registered.'
}

Write-Host ("Installed to {0} ({1:N0} MB) with a Start menu shortcut and an Installed apps entry." -f $target, ($bytes / 1MB)) -ForegroundColor Green
