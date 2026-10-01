<#
.SYNOPSIS
  Registers (or removes) the app's two scheduled tasks. MUST RUN ELEVATED.
  Install.ps1 and Uninstall.ps1 start it for you, behind one UAC prompt.

.DESCRIPTION
  TWO TASKS, SPLIT BY PRIVILEGE

    Data Usage Native Collector   every 15 minutes, NOT elevated. Runs the
                                  installed DataUsage.exe --collect: records
                                  the network every run, reads SRUM hourly.
    Data Usage Native Snapshot    on demand, elevated. The one step that needs
                                  Administrator: a VSS copy of SRUDB.dat.

  The snapshot task runs NOTHING but Windows' own binaries - cmd.exe and
  esentutl.exe from System32 - with every argument fixed here, at
  registration, which an administrator approves. So nothing the user can edit
  (the app, its settings, this repo) ever runs elevated, and there is no
  deployed script to protect or to remember to redeploy. It writes only into
  <SnapshotRoot>\work, created here admin-writable and user-readable, with
  inheritance off, in ONE call: %ProgramData%'s inherited ACL lets Users
  create files in subfolders, and a drive root's lets them create folders, so
  a directory created first and locked down second has a gap in which links
  could be planted. The ACL is read back afterwards: a folder someone created
  in the instant between removing the old one and creating this one keeps
  its own ACL, and registration stops.

  The task's working directory is that work folder, and it is how the
  collector finds it: the path an administrator approved is the only record.

  Starting a registered task needs no elevation, which is how the unelevated
  collector (and the app's Sync button) ask for a snapshot.

  Both definitions are exported to tools\task\ as a record. They live in the
  Windows task store on C:\, which a reset wipes; after a reset, re-run
  Install.ps1 (the XML's embedded user SID is this install's, and a reset
  makes a new one).

.PARAMETER Exe
  The installed DataUsage.exe the collector task runs.
.PARAMETER UserSid
  The account the tasks run as (the installing user, not the admin whose
  credentials approved the prompt).
.PARAMETER SnapshotRoot
  The folder the snapshot task writes under (in work\). Default
  %ProgramData%\Data Usage Native; Install.ps1 passes one off the system
  drive when the data folder is, because that drive carries the System
  Restore shadow copies a 99 MB copy an hour churns.
#>
param(
  [string]$Exe,
  [string]$UserSid,
  [string]$ExportDir,
  [string]$SnapshotRoot = (Join-Path $env:ProgramData 'Data Usage Native'),
  [switch]$Unregister
)
$ErrorActionPreference = 'Stop'

$collectorTask = 'Data Usage Native Collector'
$snapshotTask = 'Data Usage Native Snapshot'
$root = [IO.Path]::GetFullPath($SnapshotRoot)
$work = Join-Path $root 'work'
$sys = Join-Path $env:SystemRoot 'System32'
$admins = [Security.Principal.SecurityIdentifier]'S-1-5-32-544'

$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) { throw 'Register-Tasks.ps1 must run elevated. Run Install.ps1, which asks for elevation for this step only.' }

function Remove-Root([string]$dir) {
  # rd, not Remove-Item -Recurse: rd removes a junction instead of following it.
  if (Test-Path -LiteralPath $dir) {
    & cmd.exe /d /c rd /s /q "`"$dir`"" | Out-Null
    if (Test-Path -LiteralPath $dir) { throw "Could not remove $dir." }
  }
}

# The root the current registration writes under, read from its task: the
# folder this script made last time, wherever -SnapshotRoot then pointed.
# Only a folder Administrators own is removed - never one this script did not
# create, whatever the task definition says.
function Get-RegisteredRoot {
  $task = Get-ScheduledTask -TaskName $snapshotTask -ErrorAction SilentlyContinue
  $dir = if ($task) { $task.Actions | Select-Object -First 1 -ExpandProperty WorkingDirectory } else { $null }
  if (-not $dir -or (Split-Path $dir -Leaf) -ne 'work') { return $null }
  $parent = Split-Path $dir -Parent
  if (-not (Test-Path -LiteralPath $parent)) { return $null }
  $owner = (Get-Acl -LiteralPath $parent).GetOwner([Security.Principal.SecurityIdentifier])
  if ($owner -ne $admins) { return $null }
  return [IO.Path]::GetFullPath($parent)
}

if ($Unregister) {
  $registered = Get-RegisteredRoot
  foreach ($name in @($collectorTask, $snapshotTask)) {
    if (Get-ScheduledTask -TaskName $name -ErrorAction SilentlyContinue) {
      Unregister-ScheduledTask -TaskName $name -Confirm:$false
      Write-Host "Removed scheduled task '$name'."
    }
  }
  foreach ($dir in @($root, $registered) | Where-Object { $_ } | Select-Object -Unique) {
    Remove-Root $dir
    Write-Host "Removed $dir."
  }
  exit 0
}

if (-not $Exe -or -not (Test-Path -LiteralPath $Exe)) { throw "The app is not at '$Exe'." }
if (-not $UserSid) { throw '-UserSid is required.' }
if (-not (Test-Path -LiteralPath ([IO.Path]::GetPathRoot($root)))) { throw "There is no drive for $root." }

# ---- The snapshot directory, created with its ACL in one call --------------
# Moving it (a new -SnapshotRoot) removes the old one too.
$previous = Get-RegisteredRoot
if ($previous -and $previous -ine $root) { Remove-Root $previous; Write-Host "Removed the previous $previous" }
Remove-Root $root
$security = New-Object System.Security.AccessControl.DirectorySecurity
$security.SetAccessRuleProtection($true, $false)
$security.SetOwner([Security.Principal.SecurityIdentifier]'S-1-5-32-544')
$inherit = [System.Security.AccessControl.InheritanceFlags]'ContainerInherit, ObjectInherit'
$none = [System.Security.AccessControl.PropagationFlags]::None
foreach ($grant in @(
    @('S-1-5-18', 'FullControl'),       # SYSTEM
    @('S-1-5-32-544', 'FullControl'),   # Administrators
    @($UserSid, 'ReadAndExecute')       # the collector reads the snapshot
  )) {
  $security.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule(
        [Security.Principal.SecurityIdentifier]$grant[0],
        [System.Security.AccessControl.FileSystemRights]$grant[1],
        $inherit, $none, [System.Security.AccessControl.AccessControlType]::Allow)))
}
[System.IO.Directory]::CreateDirectory($root, $security) | Out-Null
# CreateDirectory returns an existing folder as it is, ACL and all.
$made = Get-Acl -LiteralPath $root
if (-not $made.AreAccessRulesProtected -or $made.GetOwner([Security.Principal.SecurityIdentifier]) -ne $admins -or
    (Get-Item -LiteralPath $root -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) {
  throw "$root was not created with the ACL set here (something else made it first). Remove it and re-run."
}
[System.IO.Directory]::CreateDirectory($work) | Out-Null
if (@(Get-ChildItem -LiteralPath $work -Force).Count -ne 0) { throw "$work was not created empty." }
Write-Host "Created $work (Administrators write, the user reads)"

# ---- The snapshot task: Windows' own binaries only --------------------------
# del first: esentutl will not copy over an existing file. status.txt is
# written LAST, so a status newer than the request means this run finished.
$srum = Join-Path $sys 'sru\SRUDB.dat'
$w = $work
$command = "del /f /q `"$w\SRUDB.dat`" `"$w\status.txt`" `"$w\esentutl.txt`" 2>nul & " +
  "`"$sys\esentutl.exe`" /y `"$srum`" /vss /d `"$w\SRUDB.dat`" /o > `"$w\esentutl.txt`" 2>&1 " +
  "&& (echo ok> `"$w\status.txt`") || (echo failed> `"$w\status.txt`")"
# /s: strip the outer quotes and run the rest as written.
$snapArgs = "/d /s /c `"$command`""

Register-ScheduledTask -TaskName $snapshotTask `
  -Action (New-ScheduledTaskAction -Execute (Join-Path $sys 'cmd.exe') -Argument $snapArgs -WorkingDirectory $work) `
  -Principal (New-ScheduledTaskPrincipal -UserId $UserSid -LogonType S4U -RunLevel Highest) `
  -Settings (New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -MultipleInstances IgnoreNew -ExecutionTimeLimit (New-TimeSpan -Minutes 30)) `
  -Description 'VSS-copies the Windows SRUM database for Data Usage. The only elevated step; runs only cmd.exe and esentutl.exe, with arguments fixed at registration.' `
  -Force | Out-Null
Write-Host "Registered '$snapshotTask' - on demand, elevated."

# ---- The collector task: every 15 minutes, unelevated ------------------------
# A Once trigger with a repetition, not AtLogOn: re-registering an AtLogOn-only
# task clears its running repetition, so it would stop until the next logon.
# Starting at :07 keeps the hourly SRUM read clear of the web dashboard's
# collector, which reads on the hour. No RepetitionDuration: MaxValue
# serialises to a value Task Scheduler rejects; omitted means forever.
$trigger = New-ScheduledTaskTrigger -Once -At ((Get-Date).Date.AddMinutes(7)) -RepetitionInterval (New-TimeSpan -Minutes 15)
Register-ScheduledTask -TaskName $collectorTask `
  -Action (New-ScheduledTaskAction -Execute $Exe -Argument '--collect' -WorkingDirectory (Split-Path $Exe)) `
  -Trigger $trigger `
  -Principal (New-ScheduledTaskPrincipal -UserId $UserSid -LogonType S4U -RunLevel Limited) `
  -Settings (New-ScheduledTaskSettingsSet -StartWhenAvailable -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -MultipleInstances IgnoreNew -ExecutionTimeLimit (New-TimeSpan -Hours 1)) `
  -Description 'Keeps a permanent history of per-app network usage. Records the network every 15 minutes and reads SRUM hourly, unelevated; the VSS snapshot is delegated to Data Usage Native Snapshot.' `
  -Force | Out-Null
$next = (Get-ScheduledTaskInfo -TaskName $collectorTask).NextRunTime
if (-not $next) { throw "'$collectorTask' registered with no next run time; its trigger did not take." }
Write-Host "Registered '$collectorTask' - every 15 minutes, not elevated. Next run $next."

# ---- A record of both --------------------------------------------------------
# UTF-16, as the XML declares: schtasks rejects the file if the bytes disagree.
if ($ExportDir) {
  New-Item -ItemType Directory -Force -Path $ExportDir | Out-Null
  foreach ($name in @($collectorTask, $snapshotTask)) {
    Export-ScheduledTask -TaskName $name | Set-Content -LiteralPath (Join-Path $ExportDir "$name.xml") -Encoding Unicode
  }
  Write-Host "Exported both definitions to $ExportDir"
}
exit 0
