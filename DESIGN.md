# CastCrew Developer Guide

> This document is the canonical reference for developers and AI agents working on this plugin.
> For user-facing documentation, see [README.md](README.md).

## Quick Start

```bash
# Build
dotnet build src/Jellyfin.Plugin.CastCrew/Jellyfin.Plugin.CastCrew.csproj

# Run unit tests
dotnet test tests/Jellyfin.Plugin.CastCrew.Tests/Jellyfin.Plugin.CastCrew.Tests.csproj

# Run Docker integration tests (requires Docker)
dotnet test tests/Jellyfin.Plugin.CastCrew.IntegrationTests/Jellyfin.Plugin.CastCrew.IntegrationTests.csproj \
  --filter "FullyQualifiedName~CastCrewDockerIntegrationTests"

# Deploy locally (macOS)
VERSION=$(dotnet msbuild src/Jellyfin.Plugin.CastCrew/Jellyfin.Plugin.CastCrew.csproj -nologo -getProperty:Version | tail -n 1)
dotnet publish src/Jellyfin.Plugin.CastCrew/Jellyfin.Plugin.CastCrew.csproj \
  --configuration Debug --framework net10.0 -p:Version="$VERSION" --output artifacts/local
mkdir -p ~/Library/Application\ Support/jellyfin/plugins/CastCrew_${VERSION}
cp artifacts/local/Jellyfin.Plugin.CastCrew.dll \
  ~/Library/Application\ Support/jellyfin/plugins/CastCrew_${VERSION}/
# Then restart Jellyfin
```

---

## Architecture Overview

```
┌─────────────────────────────────────────────────────────────────┐
│  Jellyfin Server (ASP.NET Core)                                 │
│                                                                 │
│  ┌──────────────────────────────────────────────────────────┐   │
│  │  CastCrew Plugin                                         │   │
│  │                                                          │   │
│  │  Startup:                                                │   │
│  │    IStartupFilter → CastCrewConfigJsonMiddleware         │   │
│  │    IHostedService → CastCrewStartupSyncHostedService     │   │
│  │                                                          │   │
│  │  Request Pipeline:                                       │   │
│  │    GET /web/config.json → Middleware injects menuLinks    │   │
│  │    GET /CastCrew/Actors → CastCrewController             │   │
│  │    GET /CastCrew/Directors → CastCrewController          │   │
│  │    GET /CastCrew/Producers → CastCrewController          │   │
│  │                                                          │   │
│  │  Services:                                               │   │
│  │    CastCrewActorQueryService → ILibraryManager           │   │
│  │    CastCrewActorQueryNormalizer (paging/sort/filter)      │   │
│  └──────────────────────────────────────────────────────────┘   │
│                                                                 │
│  Static Files: /web/config.json, /web/index.html, etc.          │
└─────────────────────────────────────────────────────────────────┘

┌─────────────────────────────────────────────────────────────────┐
│  Jellyfin Web Client (Browser)                                  │
│                                                                 │
│  1. Fetches /web/config.json → gets menuLinks with Cast&Crew    │
│  2. Renders sidebar entry in navigation drawer                  │
│  3. On click → navigates to /web/#/home?tab=cast_crew           │
│  4. castcrew-top-banner-link.js renders Cast&Crew UI in shell   │
│  5. UI calls /CastCrew/Actors etc. for data                     │
└─────────────────────────────────────────────────────────────────┘
```

### Key Design Decisions

| Decision | Rationale |
|----------|-----------|
| HTTP middleware for config.json | Works on ALL platforms (Docker, Windows, macOS) without file writes |
| File-based sync as supplement | Faster on writable roots (no middleware overhead per request) |
| `IStartupFilter` registration | Ensures middleware runs BEFORE static file middleware |
| Multi-target net8.0 + net9.0 + net10.0 | Supports Jellyfin 10.10.x, 10.11.x, and 12.x from a single codebase |
| Embedded resources for web pages | No external file dependencies; works in any plugin directory |
| `InternalPeopleQuery` for data | Shared people-query path with a Jellyfin 12 result adapter |

---

## Project Structure

```
src/Jellyfin.Plugin.CastCrew/
├── Jellyfin.Plugin.CastCrew.csproj    # Multi-target net8.0;net9.0;net10.0
├── CastCrewPlugin.cs                   # Entry point: BasePlugin + IHasWebPages
├── PluginServiceRegistrator.cs         # DI registration: services + IStartupFilter
├── CastCrewConfigJsonMiddleware.cs     # Middleware: injects menuLinks into config.json
├── CastCrewWebConfigPatcher.cs         # File-based: writes config.json when writable
├── CastCrewStartupSyncHostedService.cs # IHostedService: runs file sync on startup
├── CastCrewPluginManifestCompatibility.cs # Workaround for read-only plugin manifests
├── Api/
│   ├── CastCrewController.cs           # HTTP endpoints: /CastCrew/{Actors,Directors,Producers}
│   ├── CastCrewActorsQuery.cs          # Request model
│   └── CastCrewActorsResponse.cs       # Response model
├── Services/
│   ├── CastCrewActorQueryService.cs    # Query logic: ILibraryManager + IDtoService
│   └── CastCrewActorQueryNormalizer.cs # Normalization: paging, sort, filter bounds
├── Configuration/
│   ├── PluginConfiguration.cs          # Config model with defaults
│   └── config.html                     # Admin configuration page (embedded)
└── Web/
    ├── actors.html                     # Legacy standalone browse page (vanilla JS)
    ├── cast-crew-standalone.html       # Legacy compatibility redirect
    └── castcrew-top-banner-link.js     # Primary Cast&Crew home-shell UI (filters, sync status, refresh)

tests/
├── Jellyfin.Plugin.CastCrew.Tests/            # Unit tests (xUnit, net8.0+net9.0+net10.0)
├── Jellyfin.Plugin.CastCrew.IntegrationTests/ # Docker + live-host tests
└── docker/                                     # Manual Docker test environment
```

---

## How Things Connect

### Sidebar Entry Injection (the critical path)

**Writable web root** (standard Linux/macOS installs):
1. `CastCrewStartupSyncHostedService.StartAsync()` calls `CastCrewWebConfigPatcher.SyncCastCrewMenuLink()`
2. Patcher reads `/web/config.json`, adds menuLink, writes back
3. Patcher injects `<script>` tag into `/web/index.html` for the home-shell renderer
4. Static file middleware serves the modified files directly

**Read-only web root** (Docker, Windows installer, macOS app bundle):
1. Patcher fails with `UnauthorizedAccessException` → logs clear guidance
2. `CastCrewConfigJsonMiddleware` (registered via `IStartupFilter`) intercepts `GET /web/config.json`
3. Middleware reads original file, injects menuLink, serves modified JSON with `Cache-Control: no-store`
4. Browser always gets fresh config with the Cast&Crew entry

### Plugin Lifecycle

```
Jellyfin starts
  → RegisterServices (PluginServiceRegistrator)
      → registers CastCrewActorQueryService
      → registers CastCrewStartupSyncHostedService
      → registers IStartupFilter (CastCrewConfigJsonMiddleware)
  → Build() applies IStartupFilter → middleware added to pipeline
  → StartAsync (hosted service) → attempts file-based sync
  → GetPages() called by Jellyfin → returns config page
  → HTTP pipeline active → middleware serves modified config.json
```

---

## API Contract

### Endpoints

| Method | Path | Description |
|--------|------|-------------|
| GET | `/CastCrew/Actors` | List actors |
| GET | `/CastCrew/Directors` | List directors |
| GET | `/CastCrew/Producers` | List producers |

### Query Parameters

| Parameter | Type | Default | Description |
|-----------|------|---------|-------------|
| `startIndex` | int | 0 | Pagination offset |
| `limit` | int | config | Page size (10-200) |
| `searchTerm` | string | null | Name search filter |
| `sortBy` | string | config | `Name`, `DateCreated`, or `Random` |
| `sortOrder` | string | `Ascending` | `Ascending` or `Descending` |
| `isFavorite` | bool | null | Filter favorites only |
| `tag` | string | null | Filter by tag |
| `productionLocation` | string | null | Filter by country/region |
| `libraryIds` | string (csv) | null | Optional included-library filter (`<id1>,<id2>`) |
| `userId` | string | null | User context for favorites |

### Response

```json
{
  "Items": [...],              // BaseItemDto[]
  "TotalRecordCount": 150,
  "StartIndex": 0,
  "PageSize": 50,
  "SortBy": "Name",
  "DetailRoutePreference": "Auto",
  "AvailableTags": ["tag1"],
  "AvailableProductionLocations": ["US", "UK"],
  "AvailableLibraries": [{ "Id": "lib-1", "Name": "Movies" }],
  "LibraryMappingLastSyncedUtc": "2026-06-28T08:30:00Z",
  // When searchTerm is set:
  "NameMatchItems": [...],
  "NameMatchCount": 5,
  "DescriptionMatchItems": [...],
  "DescriptionMatchCount": 2
}
```

`LibraryMappingLastSyncedUtc` is `null` while the person-library mapping sync is still pending.

---

## Configuration

| Key | Type | Default | Bounds | Description |
|-----|------|---------|--------|-------------|
| `DefaultPageSize` | int | 50 | 10–200 | Persons per page |
| `DefaultSortBy` | string | `Name` | Name, DateCreated | Default sort field |
| `EnableCastCrewMainMenuEntry` | bool | true | — | Show sidebar entry |
| `EnableDebugLogging` | bool | false | — | Emit verbose logs for API requests, query filtering, startup/web sync, and mapping |
| `DetailRoutePreference` | string | `Auto` | Auto, HashBang, Hash | Person detail route format |

---

## Conventions and Rules

### Backend (C#)

- HTTP models in `Api/`, business logic in `Services/`, config in `Configuration/`
- Extend existing normalizers/services rather than duplicating logic
- Keep API output contracts stable — breaking changes must be intentional
- When adding web pages: add to `Web/`, register as `EmbeddedResource` in `.csproj`
- Plugin identity constants (`Id`, page names) are stable — don't change without migration
- `JellyfinVersion` in csproj must be the **baseline** (minimum) for each target series

### Frontend (Web)

- Framework-free vanilla JS (IIFE pattern) — no build pipeline
- Use Jellyfin host globals (`ApiClient`, `Dashboard`) — not external libraries
- Always escape API strings via `escapeHtml()` before HTML interpolation
- Preserve API call fallback: `ApiClient.getJSON` → `fetch` with headers
- Keep route navigation fallback chain: `Dashboard.navigate` → hash/url navigation
- Maintain localization map and `aria-*` accessibility semantics

### Testing

- Unit tests: deterministic service/normalizer behavior only
- Integration tests: opt-in via env vars (`CASTCREW_RUN_INTEGRATION_TESTS=true`)
- Docker tests: auto-run when Docker is available (Testcontainers)
- Keep `RollForward=Major` in test projects for runtime flexibility
- Unit tests multi-target `net8.0` + `net9.0` + `net10.0`; integration tests target `net10.0`

---

## Development Commands

| Task | Command |
|------|---------|
| Restore | `dotnet restore src/Jellyfin.Plugin.CastCrew/Jellyfin.Plugin.CastCrew.csproj` |
| Build | `dotnet build src/Jellyfin.Plugin.CastCrew/Jellyfin.Plugin.CastCrew.csproj` |
| Unit tests | `dotnet test tests/Jellyfin.Plugin.CastCrew.Tests/Jellyfin.Plugin.CastCrew.Tests.csproj` |
| Docker tests | `dotnet test tests/Jellyfin.Plugin.CastCrew.IntegrationTests/Jellyfin.Plugin.CastCrew.IntegrationTests.csproj --filter "FullyQualifiedName~CastCrewDockerIntegrationTests"` |
| Integration tests | `CASTCREW_RUN_INTEGRATION_TESTS=true CASTCREW_BASE_URL=http://127.0.0.1:8096 CASTCREW_API_KEY=<key> dotnet test tests/Jellyfin.Plugin.CastCrew.IntegrationTests/Jellyfin.Plugin.CastCrew.IntegrationTests.csproj` |
| Release ZIPs | Build Release, then `python3 scripts/package_plugin.py --version 0.2.1.0 --repository souravrax/Jellyfin-Plugin-CastCrew` |

### Integration Test Environment Variables

| Variable | Purpose |
|----------|---------|
| `CASTCREW_RUN_INTEGRATION_TESTS` | Set `true` to enable live-host tests |
| `CASTCREW_BASE_URL` | Jellyfin URL (default `http://127.0.0.1:8096`) |
| `CASTCREW_API_KEY` | API key auth |
| `CASTCREW_USERNAME` / `CASTCREW_PASSWORD` | Username/password auth |
| `CASTCREW_USER_ID` | Optional: force user context |

---

## CI/CD Pipeline

Workflow: `.github/workflows/package-plugin.yml`

- **Trigger:** Push to `main`, pull requests, tag push (`v*`), or manual dispatch
- **Steps:** Build all targets → Unit tests → Jellyfin 12 Docker integration tests → Existing manifest fetch → ZIP + checksum + manifest merge → Upload artifacts → GitHub Release + raw manifest branch update (tags only)
- **Outputs:** `CastCrew_<version>_jellyfin-10.10.zip`, `CastCrew_<version>_jellyfin-10.11.zip`, `CastCrew_<version>_jellyfin-12.zip`, `manifest.json`
- **Release history:** The manifest merges current entries into prior versions; version-specific GitHub release URLs keep historical packages downloadable.
- **Version:** Derived from the git tag (strips `v`) for releases; otherwise uses the reproducible project version.

---

## Troubleshooting

| Symptom | Cause | Fix |
|---------|-------|-----|
| No sidebar entry (Docker) | Plugin version doesn't have middleware | Update to v0.1.0.6+ |
| Sidebar shows in private mode only | Browser HTTP cache has stale config.json | Hard refresh (Ctrl+Shift+R) once |
| `UnauthorizedAccessException` in logs | Read-only web root (expected in Docker) | Normal — middleware handles it |
| Plugin DLL fails to load in container | Wrong .NET target (net9.0 on 10.10.x host) | Use net8.0 build for Jellyfin 10.10.x |
| Cast&Crew page blank after clicking | `castcrew-top-banner-link.js` not injected | Provide a writable web root during initialization or provision the web script and index reference |
| Docker tests fail during startup | Image pull, Docker daemon, or server startup failure | Inspect the fixture failure in Actions; tests use the baseline Jellyfin 12.0 image |

---

## Risks and Mitigations

| Risk | Mitigation |
|------|-----------|
| Jellyfin web route format changes | Route fallback chain in frontend (Auto/HashBang/Hash) |
| Web root permissions vary by platform | HTTP middleware (Docker) + file sync (writable) + Windows bootstrap |
| Large person libraries slow to load | Paginated queries; `InternalPeopleQuery` is efficient |
| Native mobile clients don't show UI | API endpoints remain independent; Web is the supported UX surface |
| Browser caches stale config.json | `Cache-Control: no-store` on middleware responses |

---

## Copilot / AI Agent Guidance

When working on this codebase as an AI agent:

1. **Read this file first** for architecture context before making changes.
2. **Check `.github/copilot-instructions.md`** for runtime-specific rules (cleanup, deployment paths).
3. **Check `.github/instructions/*.instructions.md`** for path-scoped rules when editing specific areas.
4. **Run tests after changes:** `dotnet test tests/Jellyfin.Plugin.CastCrew.Tests/...`
5. **For Docker testing:** Docker tests auto-run with Docker available — no env vars needed.
6. **Key invariants to preserve:**
   - Plugin GUID: `a1c3e5f7-2b4d-6e8f-0a1c-3e5f7b9d1e3a`
   - Primary navigation URL: `/web/#/home?tab=cast_crew`
   - Middleware intercepts exactly `GET /web/config.json`
   - API contract at `/CastCrew/{Actors,Directors,Producers}` is stable
   - `EnableInMainMenu` is NOT used for sidebar (only middleware/file sync)

---

*This document is the canonical baseline for all code generation and development tasks.*

## Jellyfin 12 migration

The .NET 10 target builds against the baseline Jellyfin 12.0.0 SDK and emits target ABI 12.0.0.0. User entities retain the 10.11 database namespace. `ILibraryManager.GetPeopleItems` now returns `QueryResult<BaseItem>`: the adapter extracts person items and disables the unused database total count because facets and sorting still operate on the full set. Restricted users use the server's `ConfigureUserAccess` with an `AccessFilter`, matching the native Persons endpoint. Fetch fallbacks use the current Authorization header.

All three targets are tested and packaged separately. Release ZIPs contain only the plugin assembly and symbols at the archive root; server dependencies are supplied by Jellyfin. GitHub release assets provide immutable version-specific download URLs; the raw manifest on the `gh-pages` branch retains earlier releases and requires no GitHub Pages site. The Docker integration suite uses the baseline official Jellyfin 12.0 image.

Plugin versions must be unique across server ABIs. A release tag `vA.B.C.0` produces assembly/catalog/file versions `A.B.C.10` for Jellyfin 10.10, `A.B.C.11` for 10.11, and `A.B.C.12` for 12. The fourth release-tag component is reserved and must be zero. Each newer ABI therefore has a greater plugin version, while each later release advances an earlier component and supersedes all builds from the prior release. Jellyfin's minimum-ABI filter followed by descending plugin-version selection then chooses the correct artifact regardless of manifest order.

The packager and project file share this mapping. C# tests check actual assembly/file versions per target, while packaging regression tests reproduce the old shared-version failure and verify all permutations of the generated manifest on Jellyfin 10.10, 10.11, 12.0 and 12.4. Historical ambiguous versions are collapsed to the highest target ABI so they cannot reintroduce ties.
