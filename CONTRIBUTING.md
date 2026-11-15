# Contributing to Sharp.Mediator

Thanks for your interest in Sharp.Mediator. Bug reports, feature requests, and pull requests are welcome. Everyone
who takes part follows the [code of conduct](#conduct). To report a security problem, follow
[SECURITY.md](SECURITY.md) instead of opening an issue.

## Before you start

- Open an issue before you start on a new feature or a change to public behavior, so the design is agreed first.
- A breaking change to the public API is discussed in an issue before any code is written, and it ships only in the
  next package major version, independently of .NET releases.
- Small fixes — a typo, a docs correction, an obvious bug with a test that shows it — can go straight to a pull
  request.

## Prerequisites

- A stable .NET 10.0 SDK meeting the minimum in `global.json`. CI tests the minimum and latest versions.
- For the Native AOT canary, the
  [Native AOT prerequisites](https://learn.microsoft.com/dotnet/core/deploying/native-aot/#prerequisites) of your
  platform. On Linux, CI installs `clang` and `zlib1g-dev`.

## GitHub Actions

Every pull request runs `Verify`, which builds with warnings as errors and runs the tests, the coverage gate, the
package checks, and the Native AOT canary, plus the benchmark checks and a second test run with the generated
interceptors off, and CodeQL's `Analyze`. Both must pass before a pull request is merged. The commands under [Build and
test](#build-and-test) reproduce `Verify` locally.

## Build and test

```bash
# The build as CI runs it
dotnet build Sharp.Mediator.slnx -c Release -warnaserror --no-incremental

# The fastest useful test run: the core tests
dotnet test --project test/Sharp.Mediator.Tests/Sharp.Mediator.Tests.csproj -c Release --no-build

# The full test run with coverage, then the 100% line and branch gate
rm -rf test/artifacts/coverage   # the verifier merges every report it finds
dotnet test --solution Sharp.Mediator.slnx -c Release --no-build --coverlet
python3 eng/coverage/verify-coverage.py test/artifacts/coverage

# The benchmark smoke checks, without timing
dotnet run --project test/Sharp.Mediator.Benchmarks -c Release --no-build -- --check
dotnet run --project test/Sharp.Mediator.ComparisonBenchmarks -c Release --no-build -- --check

# The tests again with the generated interceptors off
dotnet build Sharp.Mediator.slnx -c Release -p:SharpMediatorInterceptors=false
dotnet test --solution Sharp.Mediator.slnx -c Release --no-build

# The Native AOT canary: replace <rid> with your runtime identifier, such as osx-arm64
dotnet publish test/Sharp.Mediator.AotTests -c Release -r <rid> -warnaserror
test/Sharp.Mediator.AotTests/bin/Release/net10.0/<rid>/publish/Sharp.Mediator.AotTests
```

**Warnings as errors.** CI builds with warnings as errors. Keep `--no-incremental` locally when you have built before: a
project that is up to date is not compiled again, so its warnings are not reported again.

**Tests and coverage.** The coverage verifier merges the reports of every test project and requires 100% line and branch
coverage of the four runtime assemblies and the bundled source generator. It keys each source file by its
repository-relative path, so reports from other operating systems merge: a line counts as covered when any report covers
it.

**Interceptors off.** CI runs the tests a second time with the generated interceptors off, so statically typed calls
also reach the mediator through its interface.

**Native AOT canary.** `test/Sharp.Mediator.AotTests` is published with Native AOT and warnings as errors, and the
native binary must run and exit with 0. Verify runs it in its Linux `aot` job. Without the Native AOT toolchain, run the
same program under the JIT with `dotnet run --project test/Sharp.Mediator.AotTests -c Release`.

**Benchmark checks.** CI runs both benchmark projects with `--check`, without timing. `Sharp.Mediator.Benchmarks` calls
every scenario's benchmark methods once, after the setup BenchmarkDotNet would run.
`Sharp.Mediator.ComparisonBenchmarks` does the same for every comparison scenario, and fails unless each library reaches
the same two handlers when it publishes its first or its last event.

**Benchmarks.** `Sharp.Mediator.Benchmarks` measures Sharp.Mediator alone and references neither Mediator nor MediatR.
`ScaleBenchmarks` declares 1000 messages; set the `MessageCount` environment variable to measure another size.
`StartupBenchmarks` measures the module initializer, one process per sample, and must stay flat as `MessageCount` grows.
`CompositionBenchmarks` measures `AddMediator`, the provider and the first mediator, one process per sample; with
open-generic middleware it grows with `MessageCount`, since the generated closer the JIT compiles names every message,
and must stay a small step above the composition without it. `FirstDispatchBenchmarks` measures the first dispatch after
it, also one process per sample; the comparison project below has one of its own.

`Sharp.Mediator.ComparisonBenchmarks` owns the comparisons with Mediator and MediatR, on equal terms: each library gets
the same generated messages, `MessageCount` of each kind (requests with a reference-type response of their own,
requests without a response, and events with two handlers), and one lifetime for all three. It is a project of its own
because Mediator fixes its lifetime at compile time and scans referenced assemblies.
Each library is called through its `IMediator`, and Mediator also through the per-message overloads of its generated
class (`Mediator_Concrete`), which Mediator recommends for speed. `MediatorLifetime=Scoped` rebuilds
every library on the Scoped lifetime; the default is Singleton, Mediator's default. Both projects use BenchmarkDotNet's
switcher and write results to `test/artifacts/benchmark/<Project>/`.
Each project's `Fixtures/` folder holds its hand-written messages, handlers, and middleware, and `Setup.cs` builds the
providers its scenarios dispatch through; the comparison project generates its messages into
`obj/Messages.<MessageCount>.g.cs`.

```bash
# Sharp-only scenarios
dotnet run -c Release --project test/Sharp.Mediator.Benchmarks
# Comparisons with Mediator and MediatR
dotnet run -c Release --project test/Sharp.Mediator.ComparisonBenchmarks -- --filter '*'
# Both smoke checks, without timing
dotnet run -c Release --project test/Sharp.Mediator.Benchmarks -- --check
dotnet run -c Release --project test/Sharp.Mediator.ComparisonBenchmarks -- --check
```

Both benchmark projects use their project names as root namespaces; the generated scale messages use
`Sharp.Mediator.ScaleFixtures`, matching the fixture project.

Test tooling is shared across the Sharp repositories: xUnit v3, Microsoft.Testing.Platform with its TRX extension, and
coverlet.MTP, at the versions in `Directory.Packages.props`. `test/Directory.Build.props` supplies the references and
test defaults. `global.json` requires .NET SDK 10.0.403 or later in the 10.0 feature bands with `latestFeature`, stable
SDKs only, and the native MTP runner. The minimum includes the .NET 10.0.14 Native AOT security fixes. CI tests the
minimum and latest 10.0 SDKs, pins each test job to its installed SDK, and uses the latest stable 10.0 SDK for other
jobs. `test/testconfig.json` is merged into each application's configuration, alongside its results-directory default.

Run the engineering regression tests from the repository root with
`python3 -m unittest discover -s eng/coverage -p '*_test.py'`. Python entry points use kebab-case; imported modules use
snake_case, tests use the `*_test.py` pattern, and imports are ordinary.

Each test project writes to `test/artifacts/coverage/<Project>/`. A build clears that project's previous results.
Before collecting coverage with `--no-build`, remove `test/artifacts/coverage` and run the full solution. The verifier
rejects duplicate or missing project reports and coverage older than the latest TRX test run, including after CI
artifact download. This prevents an older successful run from covering a newer failed or filtered run. Separate
CI matrix artifacts can still merge. Generated output and automatic properties are excluded; the shipped runtime
and tooling assemblies are included. Only reviewed NativeAOT guards are excluded and verified by native canaries.

### Packaging

For ordinary local packaging, run:

```bash
dotnet pack Sharp.Mediator.slnx -c Release -o artifacts/packages
```

To reproduce CI package verification, use the commands below. The verifier requires `.snupkg` files, so this pack
command enables symbols explicitly. Removing the previous output prevents stale packages from satisfying the checks.

```bash
# Reproduce CI package verification with a fresh output directory.
dotnet build Sharp.Mediator.slnx -c Release -warnaserror --no-incremental
rm -rf artifacts/packages
dotnet pack Sharp.Mediator.slnx -c Release --no-build \
  --include-symbols -p:SymbolPackageFormat=snupkg \
  -o artifacts/packages
.github/scripts/verify-packages.sh artifacts/packages
.github/scripts/verify-consumer.sh artifacts/packages
```

`verify-packages.sh` fails when a package lacks its library, XML docs, `README.md`, or symbol package, when the core
package lacks the generator in `analyzers/dotnet/cs` or the interceptor switch in `buildTransitive/`, or when the
generator ships as a package of its own. `verify-consumer.sh` installs the packed core package into a two-assembly app,
builds it with warnings as errors, and runs it under the JIT, as a ReadyToRun publish, and with interceptors off. It
then runs a host that gets the package only through a project reference.

## Repository layout

| Path | What it is |
| --- | --- |
| `src/` | The shipped projects; [ARCHITECTURE.md](ARCHITECTURE.md) maps them |
| `src/Sharp.Mediator/Sharp.Mediator.targets` | Turns the generated interceptors on through `buildTransitive/` |
| `eng/coverage/` | Coverage policy implementation and the coverage verifier |
| `eng/coverage/modules/coverage_policy_test.py` | Regression tests of the coverage tooling |
| `test/testconfig.json` | Shared MTP configuration template |
| `test/coverage-exclusions.json` | Reviewed coverage exclusions |
| `test/Sharp.Mediator.Tests` | Tests of the core package |
| `test/Sharp.Mediator.SourceGenerator.Tests` | Generator diagnostics and generated code, including the check that user code and generated code compile together |
| `test/Sharp.Mediator.Responses.Tests` | Tests of the response envelopes |
| `test/Sharp.Mediator.Validation.Tests` | Tests of the validation middleware |
| `test/Sharp.Mediator.HttpResults.Tests` | Tests of the HTTP result mapping |
| `test/Sharp.Mediator.MultiAssemblyTests` | Dispatch to handlers generated into two assemblies |
| `test/Sharp.Mediator.AotTests` | The Native AOT canary, published with Native AOT and run |
| `test/Sharp.Mediator.Benchmarks` | Sharp-only BenchmarkDotNet scenarios for the hot paths, scale, startup, and composition |
| `test/Sharp.Mediator.ComparisonBenchmarks` | Comparisons with Mediator and MediatR on equal terms |
| `test/Sharp.Mediator.Fixtures` | The test helpers that more than one test project uses, such as the trace log |
| `test/Sharp.Mediator.LibraryFixtures` | The library side of the multi-assembly tests and the Native AOT canary |
| `test/Sharp.Mediator.NoGeneratorFixtures` | Built without the generator: a middleware package and the missing-generator guidance |
| `test/Sharp.Mediator.ScaleFixtures` | 1000 messages, generated at build time, for the scale benchmarks |
| `doc/diagnostics/` | One page per analyzer diagnostic |

## Test organization

These conventions are shared by the Sharp .NET repositories.

On disk, every test project sits directly in `test/`. The solution groups them into folders by kind:

| Solution folder | Projects |
| --- | --- |
| `Tests/Packages/` | `{Project}.Tests`: one for each project under `src/`, named after it |
| `Tests/Benchmarks/` | `{Product}.Benchmarks`, plus `{Product}.{Word}Benchmarks` for a scenario that needs its own compilation |
| `Tests/Fixtures/` | `{Product}.Fixtures`, plus `{Product}.{Word}Fixtures` for one that differs from a plain library: the class libraries that tests or benchmarks reference |
| `Tests/` | `{Product}.{Word}Tests`: a test project whose execution conditions differ, such as the Native AOT canary `{Product}.AotTests` |

- **A test belongs to the project whose code it tests.** A package it only uses as a harness or a tool doesn't count.
  A test whose subject spans packages lives with the package that references the others.
- **The suffix decides how a project builds.** `test/Directory.Build.props` makes `*Tests` test executables, gives
  `*Benchmarks` an artifacts path, and marks `*Fixtures` as non-test projects, so a fixture's name never ends in
  `Tests`. A dot before `Tests` always follows the name of a project under `src/`.
- **Inside a test project, folders group tests by subject** — the code surface under test — and the namespace
  follows the folder path. File names name the subject (`{Subject}Tests.cs`). A regression test lives with its
  subject, with a comment that says what regressed and why the pinned behavior is correct. A project with fewer than
  about six test files stays flat. A file is named for its subject, not for the kind of test or the review that asked
  for it. Test classes are `sealed`, and `Xunit` comes from a `<Using Include="Xunit" />` item in the project file.
- **A test method is named `Subject_Condition_Expected`**, or `Subject_Expected` when there is no condition. Each part
  is PascalCase with no underscore inside it, and the name says what the test pins:
  `Publish_HandlerThrows_StopsAtFirstFailure`. A test checks one behavior, so a name that needs `And` for two unrelated
  outcomes is two tests; inputs that vary one behavior go in a `[Theory]`.

Do not exclude code from coverage to pass the gate. The only hand-written exclusions are the reviewed NativeAOT
platform guards in `test/coverage-exclusions.json`; a code change to an excluded method requires an explicit policy
review. The allowlist hashes code, so comments and layout don't count.
`python3 eng/coverage/verify-coverage.py --check-policy` checks the allowlist and measurement filters before CI builds.

In this repository:

- A generator change extends `test/Sharp.Mediator.SourceGenerator.Tests`, including the check that user code and
  generated code compile together. Behavior across assemblies belongs in `test/Sharp.Mediator.MultiAssemblyTests`.
- `test/Directory.Build.props` applies the generator to every test, benchmark, and fixture project, as the package
  does for consumers, and `test/Directory.Build.targets` imports the interceptor switch the package ships. A project
  that must be built without the generator, such as `Sharp.Mediator.NoGeneratorFixtures`, sets `ApplyMediatorGenerator`
  to `false`.
- `Sharp.Mediator.LibraryFixtures` is the library side of the multi-assembly tests and the Native AOT canary, so it is
  no place for shared helpers. A helper that more than one test project uses lives in `Sharp.Mediator.Fixtures`,
  which declares no handlers and is built without the generator. A helper that one test project alone uses stays in
  that project's own `Fixtures/` folder (its namespace ends in `.Fixtures`, apart from the shared library).
- `Sharp.Mediator.Tests` groups its tests into `Dispatch/`, `Middleware/`, `Publishers/`, and `Registration/`.
- `Sharp.Mediator.SourceGenerator.Tests` groups its tests into `Generator/` for the emitted code and `Diagnostics/` for
  the analyzer and the diagnostics.

## Coding style

`.editorconfig` is authoritative. Every public member has a one-line `<summary>` XML doc comment — a missing one fails
the build. Comments explain *why* — constraints and invariants the code can't express — not *what* the next line
does. Code, comments, and documentation are written in English.

The formatter doesn't wrap XML documentation text, so wrap it yourself. Keep a line within 150 columns after its
`///`, break between phrases, and keep the punctuation that follows a tag on the tag's line. A `<summary>` that fits
on one line stays on one line: `/// <summary>Creates the validator.</summary>`. When an edit makes a line too long,
re-wrap the paragraph rather than push the overflow onto a short line of its own.

A public method gets a new setting as a new overload, never as a new parameter, which would break code compiled
against the old signature. Parameter names are public too: callers write them as named arguments.

A member's leading lines go in this order: its XML doc comment, its attributes, a `//` comment about the member,
then the member. Among the attributes, a test's `[Fact]` or `[Theory]` and its data come first and `[SuppressMessage]`
comes last.

Fix a finding rather than suppress it. When the code has to stay as it is, suppress at the narrowest scope and say
why:

- A ReSharper inspection takes `// ReSharper disable once <Id> — <why>` before the element, or a why comment and
  `// ReSharper disable`/`restore` around several. Don't use `[SuppressMessage("ReSharper", …)]`.
- A compiler or Roslyn analyzer diagnostic takes a why comment and `#pragma warning disable`/`restore`, or
  `[SuppressMessage]` with a `Justification` when the reason covers the whole member. A trimming or AOT warning takes
  `[UnconditionalSuppressMessage]`, the only form the trimmer sees.
- A path-wide setting goes in `.editorconfig` only when its reason holds for every file it matches, with a comment
  that says so. Shipped code never turns an inspection off for a whole folder. `<NoWarn>`, with a comment, is for
  project-wide policies and for NuGet or MSBuild warnings, which `.editorconfig` can't reach.

In this repository:

- The typed `Request`, `Publish`, and `RequestStream` paths and the generated interceptors allocate nothing when the
  handler completes synchronously, by design. The exceptions are a publish of a value-type event with more than one
  level, which boxes the event, a `RequestStream` with a token that can be canceled, which wraps the handler's stream
  so that its enumerator receives the token, and the one call in a mediator's life that moves the chains it keeps
  beside its array into an array of every slot, which allocates that array under a lock. Run the matching benchmark
  scenario when you change one of them.

## Diagnostics

The Sharp analyzers share one ID scheme: a three-letter prefix per product — `SVL` Validator, `SMD` Mediator, `SMS`
Messenger — followed by a three-digit number, with each feature in its own band of ten. Users pin these IDs in
`#pragma` directives and `.editorconfig`, so they are a public contract:

- A new diagnostic takes the next free ID in its feature's band. When a band is full, the feature continues in the
  product's next unassigned band, and that band is added to the table below.
- Record it in `src/Sharp.Mediator.SourceGenerator/Properties/AnalyzerReleases.Unshipped.md`.
- Give it a page at `doc/diagnostics/<ID>.md` and a row in `doc/diagnostics/README.md`.
- Never renumber or reuse an ID after it ships.

| Band | Feature |
| --- | --- |
| `SMD001`–`SMD009` | Messages and dispatch |
| `SMD010`–`SMD019` | Middleware |
| `SMD020`–`SMD029` | Code generation |

## Documentation

- `README.md` is the user manual, and every package uses it as its NuGet page. Links in it to other files are absolute
  URLs, because nuget.org cannot resolve relative ones, and it has no images.
- `doc/diagnostics/` has one page per analyzer diagnostic, listed in `doc/diagnostics/README.md`.
- A change to public behavior updates the README section that describes it.
- [ARCHITECTURE.md](ARCHITECTURE.md) maps the code. A change that moves responsibilities between projects or changes
  an invariant updates it.
- [AGENTS.md](AGENTS.md) gives AI coding agents the commands and rules from this file in short form and must match
  it: a change to them here updates it in the same pull request.
- There is no changelog file. What changed in each version is in the
  [release notes](https://github.com/sharp-space/mediator/releases), which GitHub generates from merged pull requests.

## Pull requests

- Keep a pull request to one change, and link the issue it resolves.
- Write the title as a release note, because it appears as written in the release notes — for example
  "Let `AddHandlers` take several assemblies" or "Fix a stream middleware that ran twice for a derived request".
- Pull requests are squash-merged, so the title becomes the one commit on `main`. Commit messages on a branch take the
  same form: an imperative sentence of at most 72 characters, capitalized, with no type prefix such as `feat:` and no
  closing period. A body, after a blank line and wrapped at 72 columns, says why the change is made.
- Every behavior change comes with a test.
- The build with warnings as errors and the full test suite pass locally before you ask for review.
- Fill in the checklist in the pull request template.
- The maintainers review every pull request. Before merging, they label a breaking change `breaking-change`, which
  gives it its own section in the release notes.

## Releases

Maintainers publish releases. Publishing a GitHub release starts the Release workflow, which builds, tests, and packs
the packages and pushes them to nuget.org; nothing is published by hand. The
[release notes](https://github.com/sharp-space/mediator/releases) list what changed in each version.

## Conduct

This project follows the [Contributor Covenant, version 2.1](https://www.contributor-covenant.org/version/2/1/code_of_conduct/)
in every project space — issues, pull requests, discussions, and reviews. Be respectful, assume good faith, and keep
discussions about the work. Harassment and personal attacks are not tolerated.

To report unacceptable behavior, choose **Report content**, then **Report to repository admins** on the comment,
issue, or pull request in question. Only the maintainers see the report. They handle it confidentially and respond as
the Contributor Covenant's
[enforcement guidelines](https://www.contributor-covenant.org/version/2/1/code_of_conduct/#enforcement-guidelines)
describe.
