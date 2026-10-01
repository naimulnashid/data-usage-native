# Security policy

## Reporting a vulnerability

Please report security issues **privately**, through GitHub's
**Security → Report a vulnerability** on this repository, not in a public
issue. Include what you found, how to reproduce it, and what an attacker could
do with it. You should hear back within a week.

Only the latest release is supported; there are no maintained release
branches.

## What is in scope

This is a local app with no network service, so the interesting boundaries
are local ones:

- **The elevated task.** `Data Usage Native Snapshot` runs as Administrator,
  and runs only `cmd.exe` and `esentutl.exe` from `System32` with every
  argument fixed at registration. Any way for a non-administrator to make it
  run something of their choosing, or to steer its writes outside the `work`
  folder it was registered with (`DataUsageNative-snapshot\work` on the data
  drive, or `%ProgramData%\Data Usage Native\work`), is in scope.
- **The collector task and the app**, which run as the signed-in user and
  read the SRUM snapshot the elevated task leaves behind. Anything that lets
  another local account read or alter that snapshot or the usage database is
  in scope.
- **The installer and uninstaller** (`tools\Install.ps1`, `Uninstall.ps1`),
  which register the tasks behind one UAC prompt.

Out of scope: anything requiring an already-elevated attacker, physical access
to an unlocked machine, or a data folder the user deliberately placed where
other accounts can write.
