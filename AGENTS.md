# Quality Gate - AI Agent Instructions

## Project Overview

**Description**: Jellyfin plugin that restricts users to specific media versions based on configurable path-based policies. Filters blocked MediaSources from API responses so restricted users only see allowed versions (e.g., 720p transcodes but not 4K originals).

**Architecture Pattern**: Monolith - single deployable unit (Jellyfin plugin DLL)

**Visibility**: Public repository

### Repository

- **URL**: https://github.com/GeiserX/quality-gate
- **Platform**: GitHub
- **Plugin GUID**: `a1b2c3d4-e5f6-7890-abcd-ef1234567890`
- **Previous names**: `jellyfin-quality-gate` (renamed to `quality-gate` in v2.0.0.0)

## Technology Stack

### Languages

- C# (.NET 9.0)
- HTML / JavaScript (config page -- vanilla JS, no framework)

### Frameworks & Libraries

- Jellyfin Plugin API (10.11+): `BasePlugin<T>`, `IHasWebPages`, `IPluginServiceRegistrator`, `IAsyncResultFilter`, `IIntroProvider`
- ASP.NET Core MVC Filters (result filtering pipeline)

## Architecture

```
Plugin.cs                            Entry point, IHasWebPages (config UI)
├── Configuration/
│   ├── PluginConfiguration.cs       Policies, user assignments, default policy
│   ├── configPage.html              Admin UI -- policy editor, user overrides
│   └── configPage.js                Admin UI logic
├── Api/
│   └── QualityGateController.cs     Custom REST API (binds to authenticated caller only)
├── Filters/
│   └── MediaSourceResultFilter.cs   IAsyncResultFilter -- core enforcement layer
├── Providers/
│   └── QualityGateIntroProvider.cs  Policy-based intro video selection
├── Services/
│   └── QualityGateService.cs        Policy resolution + path matching logic
└── PluginServiceRegistrator.cs      DI registration (PostConfigure for filter)
```

### Enforcement Model

The plugin uses an **IAsyncResultFilter** registered via `PostConfigure<MvcOptions>`. This intercepts Jellyfin API responses _before_ serialization, removing blocked MediaSources from:

- `PlaybackInfoResponse` (playback endpoint)
- `BaseItemDto` (item detail / user item endpoints)

This approach was chosen because Jellyfin's response compression breaks HTTP middleware approaches (middleware sees compressed bytes, not JSON).

### Policy Resolution

`QualityGateService.GetUserPolicy(userId)` resolves which policy applies:

1. Check `UserPolicies` for explicit user override
2. If override is `__FULL_ACCESS__`, return null (no filtering)
3. If no override, fall back to `DefaultPolicyId`
4. If no default, return null (full access)

### Path Matching

`QualityGateService.IsPathAllowed(policy, path)` checks both the original path and symlink-resolved path:

1. Null/empty path -> **DENIED** (fail-closed)
2. Matches any blocked prefix -> **DENIED**
3. Allowed prefixes defined and no match -> **DENIED**
4. Otherwise -> **ALLOWED**

When all sources are blocked, the filter returns an **empty array** (fail-closed). It does NOT fall back to showing originals.

### API Endpoints

| Method | Path | Auth | Returns |
|--------|------|------|---------|
| `GET` | `/QualityGate/MediaSources/{itemId}` | Authenticated user (JWT) | Filtered media sources for caller |
| `GET` | `/QualityGate/DefaultSource/{itemId}` | Authenticated user (JWT) | First allowed source for caller |

The custom API controller binds exclusively to the authenticated caller's JWT claims. It does NOT accept caller-supplied `userId` parameters (IDOR prevention).

### UserId Resolution (Result Filter vs Custom API)

The **custom API controller** (`QualityGateController`) uses ONLY JWT claims to identify the caller. This is the secure, IDOR-free path.

The **result filter** (`MediaSourceResultFilter`) also needs userId fallbacks (query params, route values, URL path extraction) because it intercepts Jellyfin's own endpoints where Jellyfin itself embeds userId in the request. Jellyfin's `[Authorize]` attribute validates the caller before the filter runs.

## Configuration

Editable via **Dashboard -> Plugins -> Quality Gate**.

### Policies

| Field | Description |
|-------|-------------|
| **Policy Name** | Descriptive name (e.g., "720p Only") |
| **Allowed Path Prefixes** | Paths users CAN access. One per line. |
| **Blocked Path Prefixes** | Paths that will be blocked. One per line. |
| **Enabled** | Toggle policy on/off |

### Config Model Fields Not Currently Enforced

`BlockedMessageHeader`, `BlockedMessageText`, `BlockedMessageTimeoutMs` -- present in config/UI for future use but not enforced server-side. The filter silently removes sources; it does not send user-facing messages.

`IntroVideoPath` -- used by `QualityGateIntroProvider` to serve per-policy intro videos.

## Development Guidelines

### Build

```bash
cd Jellyfin.Plugin.QualityGate
dotnet build -c Release
# Output: bin/Release/net9.0/Jellyfin.Plugin.QualityGate.dll
```

### Deploy

Copy DLL + `meta.json` to `<jellyfin-config>/plugins/QualityGate/` and restart Jellyfin. Or install from plugin catalog:

```
https://raw.githubusercontent.com/GeiserX/quality-gate/main/manifest.json
```

### CI/CD

GitHub Actions (`.github/workflows/build.yml`):

1. **Build** (all pushes) -- Restores, builds, packages DLL + `build.yaml` into `quality-gate.zip`
2. **Release** (tag pushes) -- Creates GitHub Release with zip artifact

Manifest auto-commit fails due to branch protection. After each release: download zip, `md5sum`, update `manifest.json` checksum manually, commit and push.

Version in `.csproj` (`<AssemblyVersion>` + `<FileVersion>` + `<Version>`) must match `build.yaml` and `meta.json`. Tags: `v2.0.2.0` format.

### Config Page

- Jellyfin custom elements: `emby-input`, `emby-button`, `emby-select`, `emby-checkbox`
- Standard CSS classes only -- no custom CSS
- Embedded resource -- changes require DLL rebuild

## Boundaries

### Always (do without asking)

- Read any file in the project
- Modify source files in `Jellyfin.Plugin.QualityGate/`
- Run build commands
- Fix compiler warnings or errors
- Update documentation and README

### Ask First

- Add NuGet dependencies
- Change the plugin GUID (breaks update path)
- Modify the CI/CD workflow
- Add new API endpoints
- Change the filter registration strategy

### Never

- Commit secrets or API keys
- Force push to git
- Reuse existing version tags
- Fail open when sources are blocked (always fail-closed)

## Code Style

- Use C# conventions: PascalCase for public members, camelCase with underscore prefix for private fields
- Prefer `async/await` with `.ConfigureAwait(false)` throughout
- File-scoped namespaces
- Nullable reference types enabled
- Log structured messages with `{Placeholder}` syntax, cast Guid arguments to `(object)` to avoid boxing ambiguity

## Learned Patterns

Things discovered during development that save time and prevent mistakes:

- **PostConfigure, not Configure**: Plugin filter registration MUST use `PostConfigure<MvcOptions>` in `IPluginServiceRegistrator`. Plain `Configure` runs too early and the filter gets overwritten by Jellyfin's own MVC setup.
- **Middleware does NOT work**: Jellyfin enables response compression. HTTP middleware sees gzipped bytes, not JSON. The `IAsyncResultFilter` approach operates on C# objects before serialization, completely bypassing compression.
- **Jellyfin resolves symlinks in MediaSource paths**: When media files are symlinks, Jellyfin stores the **resolved target path** in `MediaSourceInfo.Path`, not the symlink path. Policies must target the actual disk paths (e.g., `/mnt/user/ShareMedia/Peliculas/` for transcodes, `/mnt/remotes/TOWER_ShareMedia/Peliculas/` for originals), NOT the mount point paths (`/media/hq/`, `/media/lq/`). This is the most common misconfiguration.
- **Guid.Empty from API key auth**: Jellyfin API key authentication sets `ClaimTypes.NameIdentifier` to `Guid.Empty`. All userId extraction code must explicitly guard against this.
- **MediaSourceInfo namespace moved**: In Jellyfin 10.11+, `MediaSourceInfo` lives in `MediaBrowser.Model.Dto`, not `MediaBrowser.Model.MediaInfo`.
- **CI manifest workaround**: The `stefanzweifel/git-auto-commit-action` step in the release workflow fails due to branch protection. This is expected. Update manifest checksum manually after each release.
- **Single library, not two**: For multi-version filtering to work, both HQ and LQ media paths must be in a **single** Jellyfin library. Creating separate libraries per quality tier defeats the purpose -- Jellyfin needs both versions as MediaSources on the same item.
- **QueryResult filtering**: The filter must handle `QueryResult<BaseItemDto>` (list endpoints) in addition to single `BaseItemDto` and `PlaybackInfoResponse`. The current implementation handles the latter two; list endpoints pass through the policy check per-item.

## Troubleshooting

| Problem | Cause | Fix |
|---------|-------|-----|
| All sources blocked for restricted user | Policies use mount paths instead of resolved paths | Check `MediaInfo -> Path` in Jellyfin UI; use those paths in policies |
| Filter not running | Registration issue | Verify `PostConfigure<MvcOptions>` in `PluginServiceRegistrator` |
| Admin sees filtered content | Admin not assigned `__FULL_ACCESS__` override | Add explicit override for admin user |
| Users without override see everything | No `DefaultPolicyId` set | Set a default policy in plugin config |
| Playback error after filtering | All sources removed, player has nothing to play | Expected behavior (fail-closed). Ensure at least one path prefix allows a version |

## Security Notice

> **Do not commit secrets to the repository or to the live app.**
> Always use secure standards to transmit sensitive information.
> Use environment variables, secret managers, or secure vaults for credentials.

**Security Audit Recommendation:** When making changes that involve authentication, data handling, API endpoints, or dependencies, proactively offer to perform a security review of the affected code.

---

*Generated by [LynxPrompt](https://lynxprompt.com)*
