# NuGetMirror

[![.NET](https://github.com/MM-YJN/NuGetMirror/actions/workflows/ci.yml/badge.svg)](https://github.com/MM-YJN/NuGetMirror/actions/workflows/ci.yml)

A NuGet **V3** mirror / caching proxy built on ASP.NET Core with Native AOT
(.NET 10). It proxies an upstream NuGet feed, rewrites service-index and
registration URLs to point back at the mirror, and caches package content in a
pluggable storage backend.

## Quick start

### Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)

### Build & run

```sh
dotnet tool restore
dotnet restore NuGetMirror.slnx --locked-mode
dotnet build   NuGetMirror.slnx
dotnet run --project source/NuGetMirror/NuGetMirror.csproj   # -> http://localhost:5049
```

### Docker

The release pipeline publishes a multi-arch Native AOT container image to GitHub
Container Registry:

```sh
docker pull ghcr.io/mm-yjn/nugetmirror:latest
docker run -p 8081:8080 ghcr.io/mm-yjn/nugetmirror:latest
```

`latest` tracks the `main` branch. Every CD run also publishes the version
reported by Nerdbank.GitVersioning (e.g. `0.1.155`) as a multi-arch manifest,
with arch-specific `0.1.155-x64` and `0.1.155-arm64` tags behind it:

```sh
docker pull ghcr.io/mm-yjn/nugetmirror:0.1.155
```

#### Docker persistence & permissions

The container listens on port **8080** and runs as a **non-root user** (default
UID **1654**, from the chiseled base image). Docker named volumes
(`-v name:/path`) mount as `root`, so writes to the volume will fail unless you
set up permissions.

When caching is enabled (`Mirror:Cache:Enabled=true`) with the
`FileSystem` backend, **the mirror checks at startup that the cache
directory is writable**. If it isn't, the container exits immediately with
an actionable error message instead of starting and later returning cryptic
500 errors.

**Working setup**

```sh
# Create and pre-own the volume (one-time setup)
docker volume create mirror-data
docker run --rm -v mirror-data:/data alpine chown 1654:1654 /data

# Run with caching enabled
docker run -d --name nugetmirror \
  -e Mirror__Cache__Enabled=true \
  -e Mirror__Cache__FileSystem__Directory=/data \
  -v mirror-data:/data \
  -p 8081:8080 \
  ghcr.io/mm-yjn/nugetmirror:latest
```

Alternatively, run the container as your host user (option 2 in
`docker-compose.yml`).

A full `docker-compose.yml` example with eviction is included in the
`examples/` directory.

#### Building the container image locally

```sh
dotnet publish source/NuGetMirror/NuGetMirror.csproj --os linux --arch x64 /t:PublishContainer
```

Set `ContainerRegistry` and `ContainerImageTags` when pushing to a registry:

```sh
dotnet publish source/NuGetMirror/NuGetMirror.csproj --os linux --arch x64 /t:PublishContainer -p:ContainerRegistry=ghcr.io -p:ContainerImageTags='"latest;1.0"'
```

## Connecting NuGet clients

> [!IMPORTANT]
> This mirror implements the **NuGet V3 protocol only**. V2/OData is not
> supported (it was deprecated by nuget.org in 2021; V3 is faster and more
> reliable).

NuGet clients select the protocol based on the **source URL shape**:

| Source URL example        | Protocol used     |
|---------------------------|-------------------|
| Ends in `index.json`      | V3                |
| Anything else (bare host) | V2 OData (legacy) |

**Always register the source with the `/v3/index.json` suffix** (adjust the host
and port for your deployment; the default `http` profile listens on `5049`):

| Client          | Command / action                                                        |
|-----------------|-------------------------------------------------------------------------|
| `dotnet nuget`  | `dotnet nuget add source http://localhost:5049/v3/index.json -n mirror` |
| Visual Studio   | Tools -> NuGet Package Manager -> Package Sources -> add the same URL    |
| `NuGet.config`  | `<add key="mirror" value="http://localhost:5049/v3/index.json" />`       |

Registering the bare host (e.g. `http://localhost:5049`) makes the client fall
back to the legacy V2 OData protocol and probe endpoints such as `/Search()` and
`/FindPackagesById()`, which this mirror does not serve and which return
`404 Not Found`.

## Configuration

All settings live under the `"Mirror"` key in `appsettings.json`.

### Upstream (`Mirror:Upstream`)

| Key                 | Type       | Default                                 | Description                             |
|---------------------|------------|-----------------------------------------|-----------------------------------------|
| `IndexUrl`          | `string`   | `https://api.nuget.org/v3/index.json`   | Upstream NuGet V3 service index         |
| `DiscoveryCacheTtl` | `TimeSpan` | `00:30:00`                              | How long the service index is cached    |
| `Auth`              | `object?`  | `null`                                  | Optional upstream authentication        |
| `ExtraRewriteHosts` | `string[]` | `[]`                                    | Extra hostnames to rewrite in responses |
| `Proxy`             | `string?`  | `null`                                  | Proxy URL for upstream connections      |

#### Response body limits

Upstream bodies are checked before buffering and while reading. Configure
`Mirror:Upstream:MaxServiceIndexBodyBytes` (default 4194304, 4 MiB) for discovery
success and error responses, and `Mirror:Upstream:MaxRewriteBodyBytes` (default
67108864, 64 MiB) for general rewritten responses. Both must be positive.
Registration, repository signatures, and vulnerability responses use their own
`MaxBodyBytes` settings. Oversized proxy responses return 502 and are not cached;
failed discovery refreshes retain the previous snapshot when available.
These per-response limits bound accepted body bytes; decoding, parsing, and
rewriting require additional memory. They do not cap aggregate concurrent memory.

#### Upstream authentication (`Auth`)

```json
"Auth": {
  "Scheme": "Bearer",
  "Token": "your-token"
}
```

| `Scheme` value | Behavior                                       |
|----------------|------------------------------------------------|
| `Bearer`       | Sets `Authorization: Bearer {Token}`           |
| `Basic`        | Sets `Authorization: Basic {Token}`            |
| `Header`       | Adds a custom header: `{HeaderName}: {Token}`  |

### Cache (`Mirror:Cache`)

| Key       | Type     | Default      | Description                    |
|-----------|----------|--------------|--------------------------------|
| `Enabled` | `bool`   | `false`      | Enable package content caching |
| `Backend` | `string` | `FileSystem` | `FileSystem` or `S3`  |

### Storage backends

**FileSystem** (default) — stores packages on local disk under
`Mirror:Cache:FileSystem:Directory` (default `mirror-cache`).

```json
"FileSystem": { "Directory": "mirror-cache" }
```

**S3** — stores packages in an S3-compatible bucket (MinIO, AWS, etc.) via a
hand-rolled, zero-dependency S3 client (AOT-safe, no AWS SDK).

```json
"S3": {
  "Bucket": "my-bucket",
  "Region": "us-east-1",
  "ServiceUrl": "http://localhost:9000",
  "AccessKey": "...",
  "SecretKey": "...",
  "KeyPrefix": "",
  "UsePathStyle": true
}
```

`Bucket`, `Region`, `AccessKey`, and `SecretKey` are required when
`Backend` is `S3`; a non-empty `ServiceUrl` must be an absolute http/https URI.
At startup the mirror verifies the bucket is reachable and exits immediately
with an actionable error message if it is not, instead of failing later with
cryptic request errors.

### Cache eviction (`Mirror:Cache:Eviction`)

A background service periodically sweeps cached `.nupkg`/`.nuspec` files and
evicts entries according to the configured policy. Evicted entries are
re-downloaded from upstream on the next request.

| Key                 | Type        | Default    | Description                                        |
|---------------------|-------------|------------|----------------------------------------------------|
| `Enabled`           | `bool`      | `true`     | Enable periodic eviction sweeps                    |
| `Strategy`          | `string`    | `Oldest`   | `Oldest` (by creation time) or `Lru` (last access) |
| `MaxSizeBytes`      | `long?`     | `null`     | Maximum total cache size before eviction kicks in  |
| `MaxAge`            | `TimeSpan?` | `null`     | Retain only entries newer than this age            |
| `Interval`          | `TimeSpan`  | `00:15:00` | Sweep period                                       |
| `TargetUtilization` | `double`    | `0.9`      | Fraction of `MaxSizeBytes` to shrink to on evict   |

On `FileSystem`, `Lru` bumps `LastWriteTimeUtc` on cache hits; on `S3` it does a
self-copy to refresh `LastModified`.

### Negative cache (`Mirror:Cache:NegativeCache`)

Caches 404 responses to avoid repeatedly hitting upstream for non-existent
packages. Uses an in-memory TTL-based cache.

| Key          | Type   | Default      | Description                                |
|--------------|--------|--------------|--------------------------------------------|
| `Enabled`    | `bool` | `true`       | Enable negative caching                   |
| `Ttl`        | `TimeSpan` | `00:01:00` | How long a 404 stays cached       |
| `MaxEntries` | `int`  | `10000`      | Maximum number of cached 404 entries      |

### Cache size reporting (`Mirror:Cache:SizeReporting`)

Reports up-to-date cache size and entry count via metrics even when eviction is
disabled. When both eviction and reporting are enabled, eviction sweeps update
the gauges.

| Key        | Type        | Default      | Description                                  |
|------------|-------------|--------------|----------------------------------------------|
| `Enabled`  | `bool`      | `false`      | Enable periodic cache size enumeration       |
| `Interval` | `TimeSpan`  | `00:05:00`   | How often to enumerate for size reporting    |

### Admin endpoint (`Mirror:Admin`)

Provides a JSON endpoint with mirror version, uptime, upstream status, cache
stats (including negative-cache entry count), and storage health.
**Disabled by default** — set `Enabled` to `true`.

| Key        | Type     | Default          | Description                               |
|------------|----------|------------------|-------------------------------------------|
| `Enabled`  | `bool`  | `false`          | Enable the admin stats endpoint           |
| `Path`     | `string`| `/admin/stats`   | URL path for the admin endpoint           |

### Repository signatures (`Mirror:RepositorySignatures`)

Controls whether the mirror proxies and rewrites the upstream
RepositorySignatures resource. **Disabled by default.**

The mirror is typically run over HTTP, but some NuGet clients require HTTPS
for repository-signature URLs. Rewriting those URLs to the mirror's HTTP base
would break signature verification, so this feature is opt-in. When
`Enabled = false` (default), the rewritten service index preserves the
upstream HTTPS URL and clients fetch signatures directly from upstream.

| Key       | Type   | Default | Description                                    |
|-----------|--------|---------|------------------------------------------------|
| `Enabled` | `bool` | `false` | Enable repository-signatures proxy and rewrite |

When enabled, you may also tune caching via `Mirror:Cache:RepositorySignatures`
(`Enabled`, `CacheTtl`, and `MaxBodyBytes` in `appsettings.json`).

### Upstream background refresh (`Mirror:Upstream`)

| Key                     | Type        | Default      | Description                                  |
|-------------------------|-------------|--------------|----------------------------------------------|
| `BackgroundRefresh`     | `bool`      | `false`      | Enable proactive discovery cache refresh     |
| `DiscoveryRefreshInterval` | `TimeSpan?` | `null`    | Override the refresh interval (default: TTL - 1 min) |

### Conditional GET (304 Not Modified)

The mirror honours the `If-None-Match` request header on cached responses
(flat-container packages, readme, vulnerability pages, repository-signatures
certificates). When the client's ETag matches, the mirror returns `304 Not
Modified` without a body, saving bandwidth. Responses carry the upstream ETag
so downstream clients can revalidate cheaply.

### Public base URL (`Mirror:PublicBaseUrl`)

Overrides the mirror URL used in rewritten responses. When `null`, the mirror
uses the incoming request's scheme and host.

### Base path / subpath (`Mirror:BasePath`)

Registers all NuGet protocol endpoints and the admin stats endpoint under a URL
path prefix. When set to `/nuget`, the service index moves from `/v3/index.json`
to `/nuget/v3/index.json`, and all other NuGet endpoints move accordingly.

| Key        | Type     | Default | Description                                                         |
|------------|----------|---------|---------------------------------------------------------------------|
| `BasePath` | `string` | `null`  | URL path prefix for NuGet and admin endpoints (e.g. `/nuget`)       |

**Health-check endpoints** (`/health/live`, `/health/ready`) are intentionally
**not** affected and always remain at the root — orchestrators and load balancers
that use those probes do not need to be reconfigured.

The value is normalised automatically: leading/trailing slashes and whitespace
are removed, and a single leading slash is added. `nuget`, `/nuget`, and
`/nuget/` are all equivalent. Multi-segment prefixes are supported
(e.g. `/feeds/internal`).

When combined with `Mirror:PublicBaseUrl`, the base path is appended to the
public URL so there is a single source of truth for the subpath:

```json
"Mirror": {
  "PublicBaseUrl": "https://nuget.example.com",
  "BasePath": "/nuget"
}
```

In this configuration every rewritten resource URL will begin with
`https://nuget.example.com/nuget/…`.

## Architecture

```
Client -> Mirror (/v3/index.json, /v3-flatcontainer/*, /v3/registration-*/*)
            |
            +-- Cache hit  -> serve from FileSystem / S3
            +-- Cache miss -> fetch upstream, tee to client + cache
            +-- URL rewrite -> upstream URLs replaced with mirror URLs
```

- **Discovery**: fetches the upstream V3 service index on startup (cached per
  `DiscoveryCacheTtl`) and maps upstream base URLs to mirror prefixes.
- **Flat-container proxy** (`/v3-flatcontainer/*`): streams `.nupkg`/`.nuspec`
  content. Cacheable, with single-flight coordination so concurrent requests for
  the same package download upstream only once. The mutable `index.json` version
  listing is always proxied live.
- **Registration proxy** (`/v3/registration-*/*`): fetches upstream registration
  JSON and replaces upstream URLs with mirror URLs before returning the body.
- **Search proxy** (`/v3/search`, `/v3/autocomplete`): forwards the query string to
  the upstream search/autocomplete endpoints and rewrites registration and
  package-content URLs in the results to point back at the mirror, so client
  search (e.g. `dotnet package search`, the Visual Studio Package Manager UI) is
  served through the mirror instead of going directly to the upstream feed.
- **Catalog proxy** (`/v3/catalog0/*`): live-proxies the catalog index, pages,
  and package-detail leaves, rewriting upstream URLs to the mirror so
  downstream catalog walkers and offline tooling can traverse the full catalog
  through the mirror.
- **Vulnerability proxy** (`/v3/vulnerability/*`): when caching is enabled
  (`Mirror:Cache:Vulnerability:Enabled=true`), caches the upstream vulnerability
  index and page data with stale-fallback so vulnerability auditing works in
  air-gapped environments. Enabled by default.
- **Repository-signatures proxy** (`/v3/repository-signatures/*`): when
  enabled (`Mirror:RepositorySignatures:Enabled=true`), proxies and rewrites
  signing-certificate metadata and `.crt` files. Caching is controlled via
  `Mirror:Cache:RepositorySignatures:Enabled` (enabled by default). The index
  `contentUrl`s are rewritten via discovery prefix pairs.
  Disabled by default to avoid rewriting HTTPS URLs for a mirror served over
  HTTP.
- **Native AOT**: the app is fully AOT-compiled. All JSON uses the
  source-generated `MirrorJsonContext`; there is no reflection-based
  serialization.

## Endpoints

| Route                                 | Method(s)     | Description                           |
|---------------------------------------|---------------|---------------------------------------|
| `/v3/index.json`                      | `GET`         | Rewritten V3 service index          |
| `/v3-flatcontainer/{*path}`           | `GET`, `HEAD` | Streaming package content (cached)  |
| `/v3/registration-semver2/{*path}`    | `GET`         | Registration index (SemVer 2)       |
| `/v3/registration-gz-semver1/{*path}` | `GET`         | Registration index (gzip, SemVer 1) |
| `/v3/registration-semver1/{*path}`    | `GET`         | Registration index (SemVer 1)       |
| `/v3/search`                          | `GET`         | Search query service (rewritten)    |
| `/v3/autocomplete`                    | `GET`         | Autocomplete service (rewritten)    |
| `/v3/catalog0/{*path}`                | `GET`         | Catalog index/pages/leaves (rewritten) |
| `/v3/vulnerability/index.json`        | `GET`         | Vulnerability index (cached)         |
| `/v3/vulnerability-page/{*path}`      | `GET`         | Vulnerability page data (cached)     |
| `/v3/repository-signatures/{version}/index.json` | `GET` | Repository signatures index (cached; opt-in) |
| `/v3/repository-signatures/certificates/{*path}` | `GET`, `HEAD` | Repository signing certificates (cached; opt-in) |
| `/admin/stats`                           | `GET`         | Admin stats (disabled by default)      |
| `/health/live`                           | `GET`         | Liveness probe                          |
| `/health/ready`                       | `GET`         | Readiness probe                      |

## Testing

Tests use **xUnit v3** on the **Microsoft.Testing.Platform** (MTP) runner.

| Project                        | Description                                     | Docker      |
|--------------------------------|-------------------------------------------------|-------------|
| `NuGetMirror.UnitTests`        | Unit tests (no external dependencies)           | Not needed  |
| `NuGetMirror.IntegrationTests` | Integration tests (S3 via MinIO)                | Self-skips  |
| `NuGetMirror.Contract.Tests`   | V3 protocol snapshot tests (local helper)       | Not needed  |
| `NuGetMirror.E2E.Tests`        | End-to-end `dotnet restore` via Testcontainers  | Self-skips  |

```sh
dotnet test --solution NuGetMirror.slnx                          # all tests
dotnet test --project tests/NuGetMirror.UnitTests/NuGetMirror.UnitTests.csproj
```

Integration and E2E tests require Docker; they self-skip when Docker is
unavailable.

### Code coverage

Coverage is collected by the [coverlet](https://github.com/coverlet-coverage/coverlet)
MTP extension (`coverlet.MTP`), referenced by every test project. Local
`dotnet test` runs do **not** collect coverage by default — pass `--coverlet`
to opt in. CI collects Cobertura output and publishes a Markdown summary
(rendered with `reportgenerator`) to the GitHub Actions job summary.

```sh
# Collect coverage (Cobertura) and render an HTML report locally
dotnet test --solution NuGetMirror.slnx --results-directory ./test_results \
  --coverlet --coverlet-output-format cobertura \
  --coverlet-include "[NuGetMirror]*,[NuGetMirror.*]*" \
  --coverlet-exclude-by-file "**/*.g.cs,**/*.Generated.cs"

dotnet reportgenerator "-reports:./test_results/coverage.cobertura.*.xml" \
  "-targetdir:./test_results/coverage-report" "-assemblyfilters:+NuGetMirror" \
  -reporttypes:Html
```

Only the product assemblies (`[NuGetMirror]*`, `[NuGetMirror.*]*`) are measured;
generated files are excluded, and the test projects and
`NuGetMirror.ServiceDefaults` opt out via `[assembly: ExcludeFromCodeCoverage]`.
`reportgenerator` is a local tool — run `dotnet tool restore` first.
