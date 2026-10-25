## What this changes

## Tested

Game version and what you ran:

## Checklist

- [ ] `dotnet build -c Release` has 0 warnings
- [ ] `PLUGIN_VERSION` raised (`z`)
- [ ] `CHANGELOG.md` line added, if a dependent mod can observe the change
- [ ] Doc comments on new public members
- [ ] Every new patch body is wrapped in a try/catch that logs
- [ ] `tools\offline-checks.ps1` passes, if patches, by-name lookups or public API changed
- [ ] Docs under `docs/` updated