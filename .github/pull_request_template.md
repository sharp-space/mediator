## Summary

<!-- What changes and why, in a sentence or two. Link the issue it resolves, for example "Fixes #123".
     The title of this pull request appears as written in the release notes. -->

## Checklist

- [ ] The title reads as a release note
- [ ] `dotnet build Sharp.Mediator.slnx -c Release -warnaserror --no-incremental` passes
- [ ] `dotnet test --solution Sharp.Mediator.slnx -c Release --no-build --coverlet` passes, and every behavior change
  comes with a test
- [ ] `python3 eng/coverage/verify-coverage.py test/artifacts/coverage` reports 100% line and branch coverage
- [ ] A change to dispatch or the generator keeps the Native AOT canary, `test/Sharp.Mediator.AotTests`, publishing and
  running
- [ ] The tests also pass after a build with `-p:SharpMediatorInterceptors=false`
- [ ] Public API changes have XML docs, and `README.md` describes the new behavior
- [ ] A new diagnostic is recorded in `AnalyzerReleases.Unshipped.md` and has a page in `doc/diagnostics/`
- [ ] No breaking change, or one agreed in an issue for the next major version
