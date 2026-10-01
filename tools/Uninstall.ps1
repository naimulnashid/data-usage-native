<#
.SYNOPSIS
  Removes Data Usage installed by Install.ps1.

.DESCRIPTION
  Closes the installed copy (by the path it runs from, never by name), then
  removes the program folder, the Start menu shortcut, the start-at-login
  entry and the Installed apps entry, and - behind one UAC prompt - the two
  scheduled tasks and the snapshot folder (DataUsageNative-snapshot on the
  data drive, or %ProgramData%\Data Usage Native).

  THE HISTORY IS NEVER TOUCHED. The data folder (the database, its backup,
  logos, settings) is the thing this app exists to keep; delete it by hand if
  you mean to. -RemoveData removes only %LOCALAPPDATA%\Data Usage Native
  (preferences, logs) and scratch, which every run empties anyway.

  Install.ps1 copies this script into the program folder and registers it as
  the uninstall command, so Installed apps can remove the app without the
  source repo. Run from there, it first copies itself to %TEMP% and hands
  over, because it is about to delete the folder it lives in.
#>
param(
  [switch]$RemoveData,
  # No final pause - for Install.ps1 -Uninstall and scripted use.
  [switch]$Quiet
)
$ErrorActionPreference = 'Stop'

$target = Join-Path $env:LOCALAPPDATA 'Programs\Data Usage'
$exe = Join-Path $target 'DataUsage.exe'
$shortcut = Join-Path ([Environment]::GetFolderPath('Programs')) 'Data Usage.lnk'
$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$uninstallKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\DataUsageNative'
$localDir = Join-Path $env:LOCALAPPDATA 'Data Usage Native'

# Running from inside the folder about to be deleted: continue from a copy.
if ($PSCommandPath -and $PSCommandPath.StartsWith($target, [StringComparison]::OrdinalIgnoreCase)) {
  $copy = Join-Path $env:TEMP "DataUsage-Uninstall-$PID.ps1"
  $registerCopy = Join-Path $env:TEMP "DataUsage-Register-$PID.ps1"
  Copy-Item $PSCommandPath $copy -Force
  Copy-Item (Join-Path $target 'Register-Tasks.ps1') $registerCopy -Force -ErrorAction SilentlyContinue
  $forward = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$copy`"")
  if ($RemoveData) { $forward += '-RemoveData' }
  if ($Quiet) { $forward += '-Quiet' }
  Set-Location $env:TEMP
  $p = Start-Process powershell.exe -ArgumentList $forward -Wait -PassThru
  Remove-Item $copy, $registerCopy -Force -ErrorAction SilentlyContinue
  exit $p.ExitCode
}

try {
  Get-Process DataUsage -ErrorAction SilentlyContinue |
    Where-Object { $_.Path -and ($_.Path -ieq $exe) } |
    ForEach-Object {
      Write-Host "Closing the running copy (PID $($_.Id))"
      Stop-Process -Id $_.Id -Force
      $_.WaitForExit(5000) | Out-Null
    }

  # The tasks, elevated. Register-Tasks.ps1 sits beside this script, in the
  # repo's tools\ or (handed over from the program folder) in %TEMP%.
  $register = @((Join-Path $PSScriptRoot 'Register-Tasks.ps1'), (Join-Path $env:TEMP ($([IO.Path]::GetFileName($PSCommandPath)) -replace 'Uninstall', 'Register'))) |
    Where-Object { Test-Path $_ } | Select-Object -First 1
  $tasks = @(Get-ScheduledTask -ErrorAction SilentlyContinue | Where-Object { $_.TaskName -like 'Data Usage Native *' })
  if ($tasks.Count -gt 0 -or (Test-Path (Join-Path $env:ProgramData 'Data Usage Native'))) {
    if (-not $register) { throw 'Register-Tasks.ps1 is missing, so the scheduled tasks cannot be removed. Remove "Data Usage Native Collector" and "Data Usage Native Snapshot" in Task Scheduler.' }
    Write-Host 'Removing the scheduled tasks (Windows will ask for administrator approval)...'
    $p = Start-Process powershell.exe -Verb RunAs -Wait -PassThru -ArgumentList @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$register`"", '-Unregister')
    if ($p.ExitCode -ne 0) { throw "Removing the scheduled tasks failed (exit $($p.ExitCode))." }
  }

  if (Test-Path $shortcut) { Remove-Item $shortcut -Force }
  $run = Get-ItemProperty -Path $runKey -Name 'Data Usage' -ErrorAction SilentlyContinue
  if ($run -and $run.'Data Usage' -like "*$exe*") { Remove-ItemProperty -Path $runKey -Name 'Data Usage' }
  if (Test-Path $uninstallKey) { Remove-Item $uninstallKey -Recurse -Force }
  if (Test-Path $target) { Remove-Item $target -Recurse -Force }

  $dataDir = $null
  $location = Join-Path $localDir 'location.json'
  if (Test-Path $location) { $dataDir = (Get-Content $location -Raw | ConvertFrom-Json).dataDir }
  if ($RemoveData -and (Test-Path $localDir)) {
    Remove-Item $localDir -Recurse -Force
    Write-Host "Removed $localDir"
  }
  # AppPaths.ScratchDir: at the data drive's root unless that is the system drive.
  $scratch = if ($dataDir) { Join-Path ([IO.Path]::GetPathRoot($dataDir)) 'DataUsageNative-scratch' } else { $null }
  if ($RemoveData -and $scratch -and (Test-Path -LiteralPath (Join-Path $scratch '.data-usage-scratch'))) {
    Remove-Item -LiteralPath $scratch -Recurse -Force
    Write-Host "Removed $scratch"
  }
  Write-Host 'Data Usage is uninstalled.' -ForegroundColor Green
  if ($dataDir) { Write-Host "Your history in $dataDir was kept. Reinstalling and pointing at it brings it all back." }
  $code = 0
}
catch {
  Write-Host "Uninstall failed: $($_.Exception.Message)" -ForegroundColor Red
  $code = 1
}

# Launched from Installed apps, this is a console window of its own: leave the
# result on screen long enough to read.
if (-not $Quiet) { Start-Sleep -Seconds 5 }
exit $code
