# Quality Gate - AI Agent Instructions

## Project Overview

**Description**: Jellyfin plugin that restricts users to specific media versions based on filename regex patterns. Filters blocked MediaSources from API responses so restricted users only see allowed versions (e.g., 720p transcodes but not 4K originals). Designed for Jellyfin's multi-version naming convention (`Movie (2021) - 720p.mkv`).

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
- System.Text.RegularExpressions (filename pattern matching with ReDoS timeout protection)

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
- `QueryResult<BaseItemDto>` (library listing endpoints)
- `IEnumerable<BaseItemDto>` (lazy enumerables from `/Items/Latest` and similar)

Items where **all** media sources are blocked are hidden entirely from listings (not just stripped of sources), unless the policy has **fallback transcode** enabled — in that case, the original sources are kept but forced through server-side transcoding at the configured resolution cap.

The filter gates on `isRelevant` to avoid running on every request — it only processes `/PlaybackInfo`, `/Users/{id}/Items/...`, and `/Users/{id}/Items` paths (excluding `/Intros`).

This approach was chosen because Jellyfin's response compression breaks HTTP middleware approaches (middleware sees compressed bytes, not JSON).

### Policy Resolution

`QualityGateService.GetUserPolicy(userId)` resolves which policy applies:

1. Check `UserPolicies` for explicit user override
2. If override is `__FULL_ACCESS__`, return null (no filtering)
3. If override points to a missing/disabled/deleted policy, return **deny-all sentinel** (fail-closed)
4. If no override, fall back to `DefaultPolicyId`
5. If `DefaultPolicyId` is set but policy not found/disabled, return **deny-all sentinel** (fail-closed)
6. If no default, return null (full access)

### Filename Matching

`QualityGateService.IsPathAllowed(policy, path)` checks both the original path and symlink-resolved path:

1. Null/empty path -> **DENIED** (fail-closed)
2. Matches any **blocked filename regex** -> **DENIED**
3. **Allowed filename patterns** defined and no match -> **DENIED**
4. Otherwise -> **ALLOWED**

Filename regex matching uses `RegexOptions.IgnoreCase` with a 1-second timeout (ReDoS protection). Both original and symlink-resolved filenames are checked.

`IsSourcePlayable(policy, path)` additionally checks `File.Exists()` to filter out dangling symlinks.

When all sources are blocked and fallback transcode is disabled, the filter returns an **empty array** (fail-closed). When fallback transcode is enabled, sources are kept but forced through server-side transcoding. The **DenyAllPolicy** sentinel (misconfiguration) NEVER triggers fallback — it always stays fail-closed.

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
| **Allowed Filename Patterns** | Regex patterns matched against filenames. Files must match at least one. |
| **Blocked Filename Patterns** | Regex patterns matched against filenames. Matching files are always blocked. |
| **Custom Intro Video** | Optional path to intro video for users under this policy. |
| **If No Match Found** | Dropdown: Block playback (default), or transcode to 480p/720p/1080p/1440p/4K/no cap. Maps to `FallbackTranscode` (bool) + `FallbackMaxHeight` (int) in config. |
| **Enabled** | Toggle policy on/off |

### Config Model Fields Not Currently Enforced

`BlockedMessageHeader`, `BlockedMessageText`, `BlockedMessageTimeoutMs` -- present in the config model for backward compatibility but **removed from the admin UI**. Not enforced server-side. The filter silently removes sources; it does not send user-facing messages.

### Intro Video System

`IntroVideoPath` (per-policy) and `DefaultIntroVideoPath` (global fallback) are actively enforced by `QualityGateIntroProvider`. The provider registers intro videos in Jellyfin's database on first use via `ILibraryManager.CreateItem()`, then returns `IntroInfo { ItemId }`. The filter skips policy enforcement for configured intro paths so intros always play regardless of user restrictions.

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
https://geiserx.github.io/quality-gate/manifest.json
```

### CI/CD

GitHub Actions (`.github/workflows/build.yml`):

1. **Build** (all pushes) -- Restores, builds, packages DLL + `build.yaml` into `quality-gate.zip`
2. **Release** (tag pushes) -- Creates GitHub Release with zip artifact

The CI workflow auto-generates `manifest.json` with version/checksum and deploys to GitHub Pages. No manual manifest updates needed.

Version in `.csproj` (`<AssemblyVersion>` + `<FileVersion>` + `<Version>`) must match `build.yaml`. Tags: `v3.0.0.0` format. The CI workflow auto-generates `manifest.json` with the correct checksum and deploys it to GitHub Pages.

### Config Page

- Jellyfin custom elements: `emby-input`, `emby-button`, `emby-select`, `emby-checkbox`
- Allowed/blocked filename patterns render as repeatable one-line input rows, not multi-line textareas
- Minimal custom CSS for dynamic elements (policy cards, user table, inline chevron select wrapper, path rows); standard Jellyfin classes for everything else
- Embedded resource -- changes require DLL rebuild
- `EnableInMainMenu = true` -- appears in the Jellyfin sidebar, not just under Plugins

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
- **Jellyfin resolves symlinks in MediaSource paths**: When media files are symlinks, Jellyfin stores the **resolved target path** in `MediaSourceInfo.Path`, not the symlink path. The plugin checks both the original and symlink-resolved filenames against patterns.
- **Guid.Empty from API key auth**: Jellyfin API key authentication sets `ClaimTypes.NameIdentifier` to `Guid.Empty`. All userId extraction code must explicitly guard against this.
- **MediaSourceInfo namespace moved**: In Jellyfin 10.11+, `MediaSourceInfo` lives in `MediaBrowser.Model.Dto`, not `MediaBrowser.Model.MediaInfo`.
- **CI manifest**: The workflow auto-generates `manifest.json` with version/checksum and deploys to GitHub Pages. No manual manifest updates needed.
- **Single library, not two**: For multi-version filtering to work, both HQ and LQ media paths must be in a **single** Jellyfin library. Creating separate libraries per quality tier defeats the purpose -- Jellyfin needs both versions as MediaSources on the same item.
- **QueryResult filtering**: The filter handles `QueryResult<BaseItemDto>` (list endpoints), single `BaseItemDto`, `PlaybackInfoResponse`, and `IEnumerable<BaseItemDto>` (lazy enumerables from `/Items/Latest`). All four response shapes are filtered.
- **Lazy enumerables from `/Items/Latest`**: This endpoint returns `ListSelectIterator<T>` (implements `IEnumerable<BaseItemDto>` but doesn't match `QueryResult` or single `BaseItemDto`). Must be caught separately after the switch statement, materialized with `.ToList()`, filtered, then assigned back to `result.Value`.
- **`isRelevant` must match paths with AND without trailing slash**: `/Users/{id}/Items` (library view, no trailing slash) and `/Users/{id}/Items/{itemId}` (item detail, has slash). Use both `path.Contains("/Items/")` and `path.EndsWith("/Items")`.
- **MediaSources null on listing DTOs**: Library listing endpoints (`/Items`, `/Items/Latest`) don't populate `MediaSources` on DTOs unless the client requests `Fields=MediaSources`. The filter must inject `ILibraryManager` + `IMediaSourceManager` to look up actual media sources from the library when DTOs lack them.
- **Intro videos MUST be registered in Jellyfin's database**: `IIntroProvider.GetIntros()` returns `IntroInfo`, but Jellyfin's `LibraryManager.ResolveIntro()` calls `ResolvePath()` then `GetItemById()` — if the video isn't in the DB, it silently returns null and the intro is discarded. The fix: call `ILibraryManager.ResolvePath()` + `CreateItem()` on first use to register the video, then return `IntroInfo { ItemId = video.Id }` instead of just `IntroInfo { Path = ... }`. Cache registered IDs in a `ConcurrentDictionary` to avoid redundant DB registrations.
- **Filter must skip intro video playback**: When a client plays an intro, it calls `/Items/{introId}/PlaybackInfo`. The filter must NOT apply policy filtering to intro videos (their filenames won't match user policies). Check media source paths against configured intro paths (`DefaultIntroVideoPath` + per-policy `IntroVideoPath`) and skip filtering if matched.
- **`ILibraryManager.GetIntros()` returns `Task<IEnumerable<Video>>`**: NOT `Task<IEnumerable<IntroInfo>>`. The conversion from IntroInfo → Video happens in `ResolveIntro()` inside `Emby.Server.Implementations.dll` (not in the NuGet packages). Decompile the server DLL to understand the actual flow.
- **Jellyfin 10.11 ignores `enableDirectPlay`/`enableDirectStream` query params**: These are marked `ParameterObsolete` and have zero effect. The `DeviceProfile` in the POST body is the sole driver for playback decisions (StreamBuilder evaluates `DirectPlayProfiles` → `TranscodingProfiles`).
- **Forcing transcode requires POST body modification**: To force server-side transcode, strip `DirectPlayProfiles` from the `DeviceProfile` in the request body. This must happen in the `IAsyncResourceFilter` phase (before model binding), not `IAsyncResultFilter` (after). The filter implements both interfaces — Phase 1 modifies the POST body, Phase 2 filters the response.
- **Resolution capping requires CodecProfiles, not just MaxStreamingBitrate**: Setting `MaxStreamingBitrate` alone caps bitrate but does NOT cap resolution — Jellyfin will still output at source resolution with a lower bitrate. Must inject a `CodecProfile` with `LessThanEqual` conditions on `Width` and `Height` properties AND set `MaxStreamingBitrate` for proper resolution-limited transcoding.
- **Deep-clone MediaSourceInfo via JSON serialization**: Jellyfin caches `MediaSourceInfo` objects across requests. Mutating `SupportsDirectPlay`/`SupportsDirectStream` directly corrupts the cache for subsequent requests. Always use `JsonSerializer.Deserialize<MediaSourceInfo>(JsonSerializer.SerializeToUtf8Bytes(s))` to clone before modifying.
- **Guard against CollectionFolder in GetStaticMediaSources**: `ILibraryManager.GetItemById()` can return `CollectionFolder` items that don't implement `IHasMediaSources`. Calling `GetStaticMediaSources()` on them throws `InvalidCastException`. Always guard with a null/type check.

## Troubleshooting

| Problem | Cause | Fix |
|---------|-------|-----|
| All sources blocked for restricted user | Filename patterns don't match any version | Check filenames in Jellyfin UI; ensure patterns match the ` - label` suffix |
| Filter not running | Registration issue | Verify `PostConfigure<MvcOptions>` in `PluginServiceRegistrator` |
| Admin sees filtered content | Admin not assigned `__FULL_ACCESS__` override | Add explicit override for admin user |
| Users without override see everything | No `DefaultPolicyId` set | Set a default policy in plugin config |
| Playback error after filtering | All sources removed, player has nothing to play | Expected behavior (fail-closed). Ensure at least one filename pattern allows a version |
| Intros not playing | Intro video not registered in Jellyfin DB | `EnsureIntroRegistered()` handles this automatically. Check logs for "Failed to register intro" |
| Intros blocked by policy | Filter applying filename/path policy to intro playback | `IsConfiguredIntroPath()` should skip filtering. Verify intro path matches config exactly |
| Items visible in library but not on home page | `HidePlayedInLatest` (default: true) hides played items from Latest sections | Mark items as unplayed or disable the setting |
| Filter not catching library views | `isRelevant` check missing path format | Ensure both `/Items/` (with slash) and `/Items` (EndsWith) are matched |
| Transcoding 500 errors | Jellyfin ffmpeg/codec issue, NOT plugin-related | Check ffmpeg availability in container; test media codec compatibility |
| Fallback transcode at source resolution | `FallbackMaxHeight` is 0 (no cap) | Set to desired height (e.g., 720) via the "If No Match Found" dropdown |
| Fallback transcode not triggering | Policy `FallbackTranscode` is false, or policy is DenyAllPolicy | Enable fallback in policy dropdown; DenyAllPolicy never allows fallback |

## Security Notice

> **Do not commit secrets to the repository or to the live app.**
> Always use secure standards to transmit sensitive information.
> Use environment variables, secret managers, or secure vaults for credentials.

**Security Audit Recommendation:** When making changes that involve authentication, data handling, API endpoints, or dependencies, proactively offer to perform a security review of the affected code.

---

*Generated by [LynxPrompt](https://lynxprompt.com)*
