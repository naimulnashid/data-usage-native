# Contributing

Thanks for looking. This is a personal tool shared as is, so the bar is: keep
it working for its one real use, and keep it honest about its numbers.

- **Bugs and questions:** open an issue. Please leave out your own usage data,
  device names and network names; describe the shape of the problem instead.
- **Small fixes:** a pull request is welcome.
- **Anything larger:** open an issue first, so we can agree it fits before you
  spend the time.
- **Security issues:** not in a public issue; see [SECURITY.md](SECURITY.md).

## Before sending a change

```powershell
dotnet build DataUsageNative.slnx
dotnet test --project tests\DataUsage.Core.Tests\DataUsage.Core.Tests.csproj
```

The tests need no admin rights and no real data. If you touch the SRUM reader,
check it field by field against SrumECmd with
`dotnet run --project src\DataUsage.Cli -- read-srum <snapshot> --csv <csv>`;
it must report 0 differences.

## Conventions

- **Conventional commit messages** (`feat:`, `fix:`, `docs:`), one concern per
  commit.
- **Keep every `.ps1` pure ASCII.** Windows PowerShell 5.1 misreads a UTF-8
  dash in a BOM-less script, and silently changes its logic.
- **Never commit collected data.** `.gitignore` blocks databases, snapshots,
  CSVs and the `screenshots/` folder.
- **Screenshots come from the demo data only.** Make it with
  `dotnet run --project src\DataUsage.Cli -- demo-data demo-data` and capture
  with `tools\Capture-Views.ps1`, rather than photographing your own history.
- **Never make the collector task elevated.** Only the shadow copy is, and it
  runs nothing a user can edit.
- **No new dependencies without a reason.** The app is meant to build years
  from now from what is on disk.
