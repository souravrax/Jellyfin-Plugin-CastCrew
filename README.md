# Jellyfin-Plugin-CastCrew

A Jellyfin plugin that adds a **Cast & Crew** page to the sidebar for browsing actors, directors, and producers with search, sorting, pagination, and person detail navigation.

Fork of [Zieng/Jellyfin-Plugin-CastCrew](https://github.com/Zieng/Jellyfin-Plugin-CastCrew) with Jellyfin 12 support. Plugin identity and existing settings are preserved. Install the package matching your server; remove the old plugin build before upgrading from Jellyfin 10.x.

## Screenshots

<details>
<summary>Navigation Demo (GIF)</summary>

![Navigation Entry](docs/images/navigation-entry.gif)

</details>

## Installation

### From Plugin Repository (recommended)

1. In Jellyfin, go to **Dashboard → Plugins → Repositories**.
2. Add a new repository:
   - **Name:** `CastCrew`
   - **URL:** `https://raw.githubusercontent.com/souravrax/Jellyfin-Plugin-CastCrew/refs/heads/gh-pages/manifest.json`
3. Go to **Catalog**, find **CastCrew**, and click **Install**.
4. Restart Jellyfin.

Jellyfin will automatically select the correct version for your server (10.10.x, 10.11.x, or 12.x).
The manifest is served directly from the repository; GitHub Pages is not required.
Release `v0.2.1.0` provides distinct plugin versions so Jellyfin selects the correct build without depending on manifest order:

| Jellyfin server | Plugin version | Runtime |
|---|---|---|
| 10.10.x | `0.2.1.10` | .NET 8 |
| 10.11.x | `0.2.1.11` | .NET 9 |
| 12.x | `0.2.1.12` | .NET 10 |

The catalog version, DLL assembly version, and installation folder use the same version. The former `0.2.0.0` release reused one version for all three builds and could install an incompatible DLL. Update Jellyfin 12 installations to `0.2.1.12`. Historical duplicate entries are collapsed to one ABI per version.

### Manual Installation

1. Download the correct zip from [Releases](https://github.com/souravrax/Jellyfin-Plugin-CastCrew/releases):
   - `CastCrew_<version>_jellyfin-10.10.zip` for Jellyfin 10.10.x
   - `CastCrew_<version>_jellyfin-10.11.zip` for Jellyfin 10.11.x
   - `CastCrew_<version>_jellyfin-12.zip` for Jellyfin 12.x
2. Extract into your Jellyfin plugins directory (e.g., `<data>/plugins/CastCrew_<version>/`).
3. Restart Jellyfin.

## Features

- **Sidebar entry** — "Cast & Crew" appears in Jellyfin's left navigation drawer
- **Role tabs** — Switch between Actors, Directors, and Producers
- **Search** — Filter by name with grouped name/description matches
- **Sort & Filter** — Sort by Name, DateCreated, or Random; filter by favorites, tags, or country/region
- **Library filter** — Narrow Cast & Crew results to a specific included library (default: all included libraries)
- **Library-scoped people** — Optionally include specific libraries and show only cast/crew that appear in those libraries
- **Pagination** — Browse large libraries without performance issues
- **Person detail** — Click any person to open their native Jellyfin detail page
- **Quick settings** — Gear shortcut in the Cast & Crew page header opens CastCrew settings directly
- **Sync controls** — Header shows latest person-library sync time and provides a manual refresh button
- **Multi-language** — UI strings available in English and Chinese

## Configuration

Go to **Dashboard → Plugins → CastCrew** to configure:

| Setting | Default | Description |
|---------|---------|-------------|
| Page Size | 50 | Number of persons per page (10–200) |
| Sort By | Name | Default sort mode (Name or DateCreated) |
| Main Menu Entry | Enabled | Show/hide Cast&Crew in sidebar |
| Debug Logging | Disabled | Enable verbose CastCrew logs for plugin debug. |
| Route Preference | Auto | Person detail navigation mode (Auto, HashBang, Hash) |
| Included Libraries | All libraries | Restrict Cast & Crew results to people that appear in movies from selected libraries |

Library-person mapping refreshes automatically after saving settings and when library content changes. The Cast & Crew page header also includes a manual refresh button and displays the latest sync timestamp.
If mapping sync is still pending, Cast & Crew results still load immediately and library scoping is applied after the sync finishes.

## Compatibility

| Platform | Support |
|----------|---------|
| Jellyfin 10.10.x (net8.0) | ✅ |
| Jellyfin 10.11.x (net9.0) | ✅ |
| Jellyfin 12.x (net10.0) | ✅ |
| Docker (linuxserver, official) | ✅; see web-root permissions below |
| Windows installer | ✅ Auto-bootstraps writable web dir |
| macOS app bundle | ⚠️ Requires writable `--webdir` |
| Native mobile clients | ❌ Web-only (API endpoints still available) |

### Docker

The menu link is injected into `config.json` responses even when the web root is read-only. Rendering the Cast & Crew page also requires the plugin script in `index.html`; allow writes to the web directory during plugin initialization or provision those web assets separately. A fully read-only web root can display the menu link without rendering the page.

### Windows Installer

On Windows installs where the web root (`Program Files`) is read-only, CastCrew automatically:
1. Copies web assets to `%LOCALAPPDATA%\Jellyfin\custom-web`
2. Sets user-level `JELLYFIN_WEB_DIR` environment variable
3. Refreshes the Jellyfin tray launcher

Restart Jellyfin once after first install to activate.

### macOS App Bundle

Run Jellyfin with a writable web directory:
```
jellyfin --webdir "$HOME/Library/Application Support/jellyfin/custom-web"
```

## Limitations

- CastCrew UI is rendered inside Jellyfin Web only. Native mobile clients (iOS/Android apps) that don't embed the web UI won't show the sidebar entry.
- The CastCrew API endpoints (`/CastCrew/Actors`, `/CastCrew/Directors`, `/CastCrew/Producers`) remain available on all platforms regardless of UI visibility.

## Troubleshooting

### No Cast&Crew in sidebar

Open the page in a **private/incognito window**. If it appears there, your browser has cached stale data — do a hard refresh (Ctrl+Shift+R / Cmd+Shift+R) in your normal browser.

> **Tip:** If you're unsure whether the plugin is working, try opening Jellyfin in a private/incognito browser window first. This bypasses all browser caches and gives you a clean test.

## Development

Requires the .NET 10 SDK. Test projects allow runtime roll-forward if only .NET 10 is installed.

```sh
dotnet build src/Jellyfin.Plugin.CastCrew -c Release
dotnet test tests/Jellyfin.Plugin.CastCrew.Tests -c Release
python3 -B -m unittest discover -s tests/packaging -v
dotnet test tests/Jellyfin.Plugin.CastCrew.IntegrationTests -c Release
python3 scripts/package_plugin.py --version 0.2.1.0 --repository souravrax/Jellyfin-Plugin-CastCrew
```

The integration suite starts an isolated Jellyfin 12.0 container when Docker is available and removes it after the run. It does not use your normal Jellyfin library. Host endpoint tests require `CASTCREW_RUN_INTEGRATION_TESTS=true` and a configured test server. Tagging `v<major>.<minor>.<patch>.0` publishes three server-specific ZIPs, a GitHub release, and the plugin repository manifest. Package URLs point to release assets so older manifest entries remain downloadable.

For architecture details, build commands, API contracts, project structure, and contribution guidance, see **[DESIGN.md](DESIGN.md)**.

## Validation

Version 0.2.0.0 was checked with:

- 43 unit tests on each of .NET 8, 9, and 10 (129 passing test runs), including nonempty actor/director/producer queries and grouped search.
- Four authenticated HTTP integration tests against Jellyfin 12.0.0.
- Eight Docker integration checks against the official Jellyfin 12.0 image in GitHub Actions.
- A synthetic movie with local actor, director, and producer credits: nonempty results, grouped name search, and library filtering.
- Jellyfin 12's Modern web layout: Cast & Crew navigation and person cards.
- Separate release ZIPs with root-level plugin assemblies and matching manifest checksums.

Temporary local server data, media fixtures, downloaded SDKs, and build outputs used for these checks were removed afterward. GitHub CI runs the unit suite and Jellyfin 12 Docker checks; see [Actions](https://github.com/souravrax/Jellyfin-Plugin-CastCrew/actions).
