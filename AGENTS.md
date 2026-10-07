# AGENTS.md

Guidance for AI coding agents working in the **NuGetMirror** repository.

## Project overview

NuGetMirror is a **NuGet V3 mirror / caching proxy** built as an ASP.NET Core
web application. It proxies an upstream NuGet feed (default
`https://api.nuget.org/v3/index.json`), rewrites service-index and registration
URLs to point back at the mirror, and caches package content in a pluggable
storage backend.

- Target framework: **net10.0** (.NET 10) for every project.
- Main app uses **Native AOT** (`PublishAot=true`), `WebApplication.CreateSlimBuilder`,
  and source-generated JSON (`MirrorJsonContext`).
- License: MIT.

Key endpoints (`source/NuGetMirror/Proxy/MirrorEndpoints.cs`):

> All NuGet protocol and admin paths below are relative to `Mirror:BasePath`
> (default empty — served at root). When `BasePath=/nuget`, for example,
> `/v3/index.json` becomes `/nuget/v3/index.json`. Health endpoints are always
> served at the root regardless of `BasePath`.

- `GET /v3/index.json` — rewritten service index
- `GET|HEAD /v3-flatcontainer/{*path}` — streaming package-content proxy (cached path)
- `GET /v3/registration-*/{*path}` — registration proxies with URL rewriting (cacheable via `Mirror:Cache:Registration`)
- `GET /v3/search`, `GET /v3/autocomplete` — search/autocomplete proxies with URL rewriting
- `GET /v3/catalog0/{*path}` — catalog index/pages/leaves proxy with URL rewriting
- `GET /v3/vulnerability/index.json`, `GET /v3/vulnerability-page/{*path}` — vulnerability data proxy (cached)
- `GET /v3/repository-signatures/{version}/index.json`, `GET|HEAD /v3/repository-signatures/certificates/{*path}` — repository signatures proxy (cached, opt-in via `Mirror:RepositorySignatures:Enabled`)
- `GET /admin/stats` — admin operational stats (disabled by default; see `Mirror:Admin`)
- `GET /health/live` — liveness probe (process self-check only; always at root)
- `GET /health/ready` — readiness probe (upstream reachability + storage availability; always at root)

## Build & run

There are **no build scripts**; use the `dotnet` CLI against the solution file
(`NuGetMirror.slnx`).

```sh
dotnet tool restore                 # restore local tools first (nbgv, dotnet-inspect, etc.)
dotnet restore NuGetMirror.slnx --locked-mode
dotnet build   NuGetMirror.slnx
dotnet build   NuGetMirror.slnx -c Release
dotnet run --project source/NuGetMirror/NuGetMirror.csproj   # http profile -> http://localhost:5049

# AOT publish
dotnet publish source/NuGetMirror/NuGetMirror.csproj -c Release

# Aspire orchestration (requires Docker; launches dashboard + MinIO + mirror)
aspire run
dotnet run --project source/NuGetMirror.AppHost

# Format / style
dotnet format NuGetMirror.slnx                      # apply fixes per .editorconfig
dotnet format NuGetMirror.slnx --verify-no-changes  # CI-style check
```

## Tests

Tests use **xUnit v3** on the **Microsoft.Testing.Platform (MTP)** runner
(configured in `global.json` and `Directory.Build.props`). Test projects are
executables (`<OutputType>Exe</OutputType>`).

```sh
dotnet test --solution NuGetMirror.slnx                                   # all tests

# Run a specific project (MTP)
dotnet test --project tests/NuGetMirror.UnitTests/NuGetMirror.UnitTests.csproj

# Run a specific test class within a project (MTP filter)
dotnet test --project tests/NuGetMirror.UnitTests/NuGetMirror.UnitTests.csproj --filter-class <fully-qualified-class-name>
```

> **SDK pinning & package locks are enabled:** `global.json` pins the exact .NET
> SDK version, and `RestorePackagesWithLockFile=true` (in `Directory.Build.props`)
> makes NuGet maintain a committed `packages.lock.json` for every project.
> After adding, removing, or updating any .NET dependency, run
> `dotnet restore NuGetMirror.slnx --force-evaluate` to refresh all lock files
> and commit the updated `packages.lock.json` files with the dependency change.

Test projects (`tests/`):
- `NuGetMirror.UnitTests` — unit tests (no external dependencies required).
- `NuGetMirror.IntegrationTests` — integration tests (requires Docker for S3 tests; self-skips when Docker unavailable).
- `NuGetMirror.TestKit` — shared test helpers (not a test project).
- `NuGetMirror.Contract.Tests` — NuGet V3 protocol snapshot tests (local `Snapshots.VerifyJson` helper; fixtures in `Fixtures/Upstream/`, snapshots in `Snapshots/`; on mismatch a `*.received.*` file is written next to the snapshot — rename it `*.verified.*` to accept).
- `NuGetMirror.E2E.Tests` — end-to-end NuGet restore tests via Testcontainers.

**Integration and E2E tests require Docker.** When Docker is unavailable,
`MinioFixture` (integration) sets `DockerAvailable = false` and its tests
self-skip via `Assert.Skip`. The E2E suite does **not** self-skip — it starts
SDK containers via Testcontainers and forwards host ports, so it fails when
Docker is unavailable.

### Code coverage

Coverage is collected via the **coverlet** MTP extension (`coverlet.MTP`,
referenced by every test project). Local `dotnet test` runs do **not** collect
coverage by default — pass `--coverlet` to opt in. CI
(`.github/workflows/ci.yml`) collects Cobertura output and renders a Markdown
summary with `reportgenerator` into the GitHub Actions job summary.

```sh
# Collect coverage locally (Cobertura), then render an HTML report
dotnet test --solution NuGetMirror.slnx --results-directory ./test_results \
  --coverlet --coverlet-output-format cobertura \
  --coverlet-include "[NuGetMirror]*,[NuGetMirror.*]*" \
  --coverlet-exclude-by-file "**/*.g.cs,**/*.Generated.cs"
dotnet reportgenerator "-reports:./test_results/coverage.cobertura.*.xml" \
  "-targetdir:./test_results/coverage-report" "-assemblyfilters:+NuGetMirror" \
  -reporttypes:Html
```

- Only product assemblies are measured (`[NuGetMirror]*`, `[NuGetMirror.*]*`);
  generated files (`*.g.cs`, `*.Generated.cs`) are excluded.
- Test projects and `NuGetMirror.ServiceDefaults` opt out via the
  `[assembly: ExcludeFromCodeCoverage]` attribute declared in their `.csproj`.
- `reportgenerator` (local tool `dotnet-reportgenerator-globaltool`) converts
  the Cobertura XML into human-readable reports (`MarkdownSummary` in CI, any
  `-reporttypes` such as `Html` locally).

> **AI agents:** When asked to generate a code coverage report, place all output
> files (test results, Cobertura XML, reportgenerator output) under the
> `artifacts/` directory if no explicit output path is provided. For example:
> `--results-directory ./artifacts/test_results` and
> `-targetdir:./artifacts/coverage-report`.

## Code style

Style is **enforced at build time** (`EnforceCodeStyleInBuild=true`); violations
surface as build warnings, so keep the build warning-free. Rules live in the
large `.editorconfig`, with analyzer suppressions documented in
`Directory.Build.props`. CI additionally runs `dotnet format NuGetMirror.slnx
--verify-no-changes`, so run `dotnet format NuGetMirror.slnx` before finishing
to apply fixes.

- C#: 4-space indent, **file-scoped namespaces** matching folder structure.
- `using` directives **outside** the namespace, `System.*` first, grouped with blank lines.
- Prefer **primary constructors**, `is null`, null propagation, collection/object initializers; prefer target-typed `new()`.
- **Allman braces**; braces always required (even single-statement blocks); new line before `else`/`catch`/`finally`; no file header.
- `var` only when the type is apparent; use explicit types otherwise (IDE0007/IDE0008 are warnings).
- No `this.` qualification; use language keywords (`int`, not `Int32`); require accessibility modifiers; prefer `readonly` and `sealed`.
- Async methods in app code use the `Async` suffix and `.ConfigureAwait(false)`.
  Tests are exempt from both: `.editorconfig` disables the suffix rule for
  `**.*Tests/**.cs`, and every test project suppresses CA2007 via
  `NoWarnForTestProjects` — do not use `.ConfigureAwait(false)` in test code.
- **Line endings: CRLF** for `.cs`/`.razor` (enforced via `.gitattributes`); LF for `.sh`/`.crt`; insert final newline.
- Indent 2 spaces for JSON, YAML, and MSBuild/XML (`.props`, `.csproj`, `.slnx`).
- Spelling analyzer is on; add allowed words to `exclusion.dic`.
- **One top-level type per file.** Do not place multiple classes, records,
  structs, enums, or interfaces in the same `.cs` file. Nested `private`/
  `internal` types that are an implementation detail of the containing type
  (e.g. the inner `MetaInfo` and `FileCacheWriteHandle` classes in
  `FileSystemPackageContentStore.cs`) are acceptable.

### Types & error handling (observed patterns)

- Types are typically `sealed` and `internal` by default; keep the public surface minimal.
- `partial` classes for source-generated logging (`[LoggerMessage]`).
- `readonly record struct` for small value types; `record` for DTOs.
- `ConfigureAwait(false)` on every `await` in app code (CA2007 is a warning).
- `CancellationToken` passed last (optional parameters may follow); use `TestContext.Current.CancellationToken` in tests.
- Validate args with `ArgumentNullException.ThrowIfNull(...)`.

## Native AOT constraints

The main app is `PublishAot=true`. Stay AOT-compatible:
- JSON goes through the source-gen context (`MirrorJsonContext`); add new serialized types there. No reflection-based serialization.
- Logging uses source-gen `[LoggerMessage]`.
- Config binding uses the source generator; all options are bound under a
  single `MirrorOptions` root (sub-objects: `Upstream`, `Cache`, `Admin`,
  `RepositorySignatures`). For composition-time reads before the DI container
  is built, read manually from `IConfiguration` (see `RegisterPackageStore` in
  `Program.cs`).
- Avoid reflection, dynamic codegen, and unbounded generics that break trimming/AOT.

## Conventions for changes

- Keep `dotnet build` and `dotnet format NuGetMirror.slnx --verify-no-changes` clean before finishing.
- Add or update tests in the appropriate project; test classes are `sealed`,
  use `[Fact]`/`[Theory]`/`[InlineData]`, AAA structure, and namespaces
  mirroring folders.
- Don't add new analyzer suppressions casually; existing suppressions in `Directory.Build.props` are documented with justifications.
- `README.md` is the end-user/contributor-facing guide (configuration
  reference, client setup, Docker image); this file targets AI coding agents.
  Update both when behaviour or options change.

## Project layout

```
source/NuGetMirror/        main ASP.NET Core mirror app
  Program.cs               slim builder, DI wiring, backend selection
  Configuration/           Mirror/Upstream/Cache/FileSystem/S3 options
  Diagnostics/             MirrorMetrics, CacheStatsState, MirrorStatsResponse
  Discovery/               discovery cache, background refresh service, URL rewriting
  Json/                    source-gen JSON context, ServiceIndex
  Proxy/                   MirrorEndpoints, Forwarder, ConditionalRequest helper, vulnerability/repo-sig forwarders
  Storage/                 IPackageContentStore + FileSystem/S3 impls
  Upstream/                UpstreamClient
source/NuGetMirror.AppHost/          Aspire app host (orchestration)
source/NuGetMirror.ServiceDefaults/  shared service defaults (extensions, telemetry)
tests/                     UnitTests, IntegrationTests, Contract.Tests, E2E.Tests, TestKit (shared helpers)
```

Storage backend is selected at startup via `Mirror:Cache:Backend`
(`Program.cs` `RegisterPackageStore`): `FileSystem` (default) or `S3`.

## Cache eviction

A `BackgroundService` (`CacheEvictionService`) periodically sweeps all cached
content and evicts entries according to configured
policy. Both `FileSystem` and `S3` backends implement the `ICacheMaintenance`
interface required for eviction.

**Configuration** (`Mirror:Cache:Eviction` section):
- `Enabled` — `true` by default. Set to `false` to disable periodic sweeping.
- `Strategy` — `Oldest` (default, evict by creation time) or `Lru` (evict by
  last access; on `FileSystem` this updates `LastWriteTimeUtc` on cache hits;
  on `S3` it performs a self-copy to bump `LastModified`).
- `MaxSizeBytes` — optional maximum total cache size in bytes. When exceeded,
  oldest entries are evicted until total drops to `MaxSizeBytes * TargetUtilization`.
- `MaxAge` — optional `TimeSpan` retention window. Entries older than this are
  evicted regardless of total size.
- `Interval` — sweep period (default `00:15:00`).
- `TargetUtilization` — fraction of `MaxSizeBytes` to shrink to after a size
  sweep (default `0.9`).

Eviction targets all cached content including package files (.nupkg/.nuspec),
readmes ($readme/), registration responses ($registration/),
vulnerability page data ($vuln/page/), and
repository-signature data ($reposign/). The vulnerability index is in-memory
only and is not evicted. Evicted entries are re-downloaded from upstream on the
next request.

## Local tools

Defined in `dotnet-tools.json` (run `dotnet tool restore` first):
`nbgv` (Nerdbank.GitVersioning), `dotnet-outdated`, `reportgenerator`,
`dotnet-inspect`, `aspire`. For querying .NET APIs/packages, see the
`dotnet-inspect` skill at `.agents/skills/dotnet-inspect/SKILL.md`.

## Notes

- CI builds and tests on push/PR to `main` via `.github/workflows/ci.yml`. The
  `build` job runs `dotnet format NuGetMirror.slnx --verify-no-changes` and the
  unit + integration + contract tests (E2E excluded via `Category=E2E`) with
  coverage in the job summary; the separate `e2e` job runs the E2E suite on
  pushes to `main` and manual dispatches only (never on PRs), since it starts
  SDK containers via Testcontainers and runs real `dotnet restore` operations
  through the mirror.
- CD publishes a multi-arch (`x64` + `arm64`) Native AOT container image to GHCR
  (`ghcr.io/mm-yjn/nugetmirror`) via `.github/workflows/cd.yml` (manual
  `workflow_dispatch`). Per-arch images are tagged `<version>-x64` / `<version>-arm64`,
  with a multi-arch `<version>` manifest (NBGV `SimpleVersion`) in front; on `main`
  the same manifest is also pushed as `latest`.
- The main project exposes internals to the four test projects via
  `InternalsVisibleTo` (`NuGetMirror.UnitTests`, `NuGetMirror.IntegrationTests`,
  `NuGetMirror.Contract.Tests`, `NuGetMirror.E2E.Tests`).
- Versioning is handled by Nerdbank.GitVersioning (`version.json`).
