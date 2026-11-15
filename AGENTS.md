# AGENTS.md

Instructions for AI coding agents working in this repository. [CONTRIBUTING.md](CONTRIBUTING.md) holds the full
conventions and [ARCHITECTURE.md](ARCHITECTURE.md) maps the code. This file repeats CONTRIBUTING.md's commands and rules
in short form and must match it: a change to one changes the other in the same pull request. If you find them
disagreeing, CONTRIBUTING.md is right; fix this file.

## Project

Sharp.Mediator is a mediator for .NET built for speed and Native AOT, with first-class request/response, events,
streaming, and middleware. It targets .NET 10 and stays Native AOT- and trim-safe: nothing scans assemblies or closes
generic types on the dispatch path; the source generator emits the registrations and routes the runtime needs.

## Commands

```bash
# The build as CI runs it; --no-incremental also reports the warnings of projects that are already up to date
dotnet build Sharp.Mediator.slnx -c Release -warnaserror --no-incremental

# The fastest useful test run: the core tests
dotnet test --project test/Sharp.Mediator.Tests/Sharp.Mediator.Tests.csproj -c Release --no-build

# The full test run with coverage, then the 100% line and branch gate
rm -rf test/artifacts/coverage
dotnet test --solution Sharp.Mediator.slnx -c Release --no-build --coverlet
python3 eng/coverage/verify-coverage.py test/artifacts/coverage

# The benchmark smoke checks, without timing
dotnet run --project test/Sharp.Mediator.Benchmarks -c Release --no-build -- --check
dotnet run --project test/Sharp.Mediator.ComparisonBenchmarks -c Release --no-build -- --check

# Reproduce CI package verification with a fresh output directory
dotnet build Sharp.Mediator.slnx -c Release -warnaserror --no-incremental
rm -rf artifacts/packages
dotnet pack Sharp.Mediator.slnx -c Release --no-build \
  --include-symbols -p:SymbolPackageFormat=snupkg \
  -o artifacts/packages
.github/scripts/verify-packages.sh artifacts/packages
.github/scripts/verify-consumer.sh artifacts/packages

# The tests again with the generated interceptors off
dotnet build Sharp.Mediator.slnx -c Release -p:SharpMediatorInterceptors=false
dotnet test --solution Sharp.Mediator.slnx -c Release --no-build

# The Native AOT canary: needs the platform's Native AOT toolchain; replace <rid> with yours, such as osx-arm64
dotnet publish test/Sharp.Mediator.AotTests -c Release -r <rid> -warnaserror
./test/Sharp.Mediator.AotTests/bin/Release/net10.0/<rid>/publish/Sharp.Mediator.AotTests
```

For ordinary local packaging without CI verification, see [CONTRIBUTING.md](CONTRIBUTING.md#packaging).

Run the narrowest test project that covers the change first, and the full suite before you finish.

Do not exclude code from coverage to pass the gate. The only hand-written exclusions are the reviewed NativeAOT
platform guards in `test/coverage-exclusions.json`; a code change to an excluded method requires an explicit policy
review. The allowlist hashes code, so comments and layout don't count.
`python3 eng/coverage/verify-coverage.py --check-policy` checks the allowlist and measurement filters before CI builds.

## Rules

- Every behavior change comes with a test. A bug fix comes with a regression test that fails without the fix and
  passes with it.
- Put a test where CONTRIBUTING.md › Test organization says: in the project named after the code under test, in a
  folder named after its subject.
- Every public member has a one-line `<summary>` XML doc comment; the build fails without one.
- Every shipped assembly keeps 100% line and branch coverage. A new branch needs a test, or the coverage gate fails.
- Preserve public API compatibility unless the task explicitly requests a breaking change. Breaking changes require
  a package major version increase, independently of .NET releases. Raising the minimum supported SDK, compiler, or
  runtime version also requires a package major version increase.
- Do not remove or change a shipped public member of `Sharp.Mediator.Bindings`, old overloads included: libraries
  built against an earlier version call them from generated code. Add a new member instead.
- A new analyzer diagnostic follows CONTRIBUTING.md › Diagnostics: an ID in its feature's band, an entry in
  `AnalyzerReleases.Unshipped.md`, a page in `doc/diagnostics/`, and a row in `doc/diagnostics/README.md`. Entries
  move to `AnalyzerReleases.Shipped.md` only at release.
- `README.md` is the user manual and every package's NuGet page. When behavior changes, update the section that
  describes it. Links in it to other files are absolute URLs.
- Update ARCHITECTURE.md when a change moves responsibilities between projects or changes an invariant.
- Write code, comments, and documentation in English. Comments explain why, not what.
- Do not add files that record work in progress — plans, audits, reports, TODO lists — to the repository. Keep
  working notes outside it. There is no changelog either: the release notes come from pull request titles.
- Do not add a `CLAUDE.md`: Claude Code reads this file only when there is none. If Claude-specific instructions are
  ever needed, start the `CLAUDE.md` with the line `@AGENTS.md`.

## Pull requests

- The title appears as written in the release notes, so write it as one.
- Write commit messages in the same form: an imperative sentence of at most 72 characters, with no type prefix such
  as `feat:` and no closing period. Put the reason for the change in the body.
- Fill in the checklist in `.github/pull_request_template.md`.

## In this repository

- The typed `Request`, `Publish`, and `RequestStream` paths and the generated interceptors allocate nothing when the
  handler completes synchronously, except a publish of a value-type event with more than one level, which boxes the
  event, a `RequestStream` with a token that can be canceled, which wraps the handler's stream so that its enumerator
  receives the token, and the one call in a mediator's life that moves the chains it keeps beside its array into an
  array of every slot, which allocates that array under a lock. Run the matching scenario in
  `test/Sharp.Mediator.Benchmarks` when you change one.
- Generated code calls the library only through public members.
- A generator change needs a test in `test/Sharp.Mediator.SourceGenerator.Tests` that compiles user code together with
  the generated code. Behavior across assemblies is tested in `test/Sharp.Mediator.MultiAssemblyTests`.
- Statically typed calls reach the mediator through generated interceptors, so the tests must pass with
  `-p:SharpMediatorInterceptors=false` as well.
