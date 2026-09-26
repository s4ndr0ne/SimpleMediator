## Summary

<!-- What does this change do and why? Link the issue if one exists. -->

## Checklist

- [ ] `dotnet build -c Release` is clean (warnings are errors on the library)
- [ ] `dotnet test -c Release` passes on `net8.0` and `net10.0`
- [ ] Public API surface unchanged, or `src/SimpleMediator/PublicAPI.Unshipped.txt` updated intentionally
- [ ] No breaking change, or the change is clearly marked BREAKING and targets the next major
- [ ] `CHANGELOG.md` updated under `[Unreleased]`
- [ ] Documentation (README / XML doc comments) updated where behavior or contracts change
