# Quality Gate - AI Agent Instructions

## Project Overview

**Description**: Jellyfin plugin that caps the resolution a user may be served. The cap is measured against the media's actual height, read from its video `MediaStream`, never against the filename, so it survives a rename or a symlink. Over-cap media is served as a capped transcode or as a lower-resolution version of the same item, and requests for the original file are refused. Optionally groups an encoded copy with its original so one film shows two versions rather than appearing twice. Filename regex patterns are legacy and do not enforce media-source restrictions on Jellyfin 12; see [docs/configuration.md](docs/configuration.md#fields-that-do-not-restrict-playback).

**Architecture Pattern**: Monolith - single deployable unit (Jellyfin plugin DLL)

**Visibility**: Public repository

### Repository

- **URL**: https://github.com/GeiserX/quality-gate
- **Platform**: GitHub
- **Plugin GUID**: `9cab70ca-0af3-4d3a-adab-6a0df2496a33`
- **Previous names**: `jellyfin-quality-gate` (renamed to `quality-gate` in v2.0.0.0)

## Technology Stack

### Languages

- C# (.NET 10.0, Jellyfin 12 ABI)
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
├── Filters/
│   ├── ResolutionCapFilter.cs       Resource + result filter -- the enforcement layer
│   └── MediaSourceResultFilter.cs   Legacy filename filter -- present but NOT registered
├── Providers/
│   └── QualityGateIntroProvider.cs  Policy-based intro video selection
├── Services/
│   └── QualityGateService.cs        Policy resolution + path matching logic
└── PluginServiceRegistrator.cs      DI registration (PostConfigure for filter)
```

### Enforcement Model

**The resolution cap (v3.5.0.0+) is the enforcing rule.** `ResolutionCapFilter` is an
`IAsyncResourceFilter` + `IAsyncResultFilter` registered via `PostConfigure<MvcOptions>`. It
compares `QualityPolicy.MaxHeight` against the media's actual height from its video
`MediaStream`, never against the filename. `MaxHeight = 0` (the default) disables it entirely,
so an existing config and every unrestricted user are untouched.

It covers both ways video leaves the server:

- **Negotiation** (`POST /Items/{id}/PlaybackInfo`): phase 1 injects a required
  `Height <= cap` video `CodecProfile` condition into the DeviceProfile in the request body
  before model binding; phase 2 drops over-cap sources from `PlaybackInfoResponse` when a
  within-cap sibling exists, and otherwise marks every source transcode-only.
- **Direct delivery**: 403 on `/Videos/{id}/stream`, `stream.{container}`,
  `master|main|live.m3u8`, `hls1/…`, `/Audio/{id}/stream` and `/Items/{id}/File|Download`
  when the item is over the cap and the request wants the bytes as-is or an uncapped transcode.
  On `/Items/{id}/File|Download` the route's own id is the only thing measured. Those actions
  take no media source parameter, so a caller-supplied `mediaSourceId` naming a small version
  must not stand in for it. On every other delivery route the filter measures each candidate id
  (`params` index 2, `mediaSourceId`, the route id) and the tallest decides, because the action
  settles on one of them after the filter has run.

One route is deliberately not covered: the legacy HLS segment route
`/Videos/{id}/hls/{playlistId}/{segmentId}.{container}`, whose `itemId` is declared and never
read. [docs/how-it-works.md](docs/how-it-works.md#the-one-route-that-is-not-covered) records it
as an accepted, known bypass and what closing it would take.

The filter fails open everywhere except one place. A delivery request from a user it has
already established is capped fails **closed**: by then the only open question is how tall the
media is, so a throw is a defect here rather than something the library said, and allowing the
request would hand over the bytes the cap exists to withhold. A height the library reports as
*unknown* (never probed) is a data condition rather than a defect. It is allowed and logged as
a warning, and negotiation still caps it.

**MediaSourceResultFilter** is present in the assembly but not registered. Nothing constructs
it and nothing adds it to `MvcOptions`, so on Jellyfin 12 it never runs and filename patterns
restrict nothing. It stays only so the older behaviour is still readable in one place.

The rest of this section is legacy: how that filter behaved on Jellyfin 10.x, when
`PostConfigure<MvcOptions>` registered it. It intercepted API responses _before_
serialization, removing blocked MediaSources from:

- `PlaybackInfoResponse` (playback endpoint)
- `BaseItemDto` (item detail / user item endpoints)
- `QueryResult<BaseItemDto>` (library listing endpoints)
- `IEnumerable<BaseItemDto>` (lazy enumerables from `/Items/Latest` and similar)

Items where **all** media sources were blocked were hidden entirely from listings (not just stripped of sources), unless the policy had **fallback transcode** enabled — in that case, the original sources were kept but forced through server-side transcoding at the configured resolution cap.

The filter gated on `isRelevant` to avoid running on every request — it only processed `/PlaybackInfo`, `/Users/{id}/Items/...`, and `/Users/{id}/Items` paths (excluding `/Intros`).

A result filter beat middleware because Jellyfin's response compression breaks HTTP middleware approaches (middleware sees compressed bytes, not JSON).

### Policy Resolution

`QualityGateService.GetUserPolicy(userId)` resolves which policy applies:

1. Check `UserPolicies` for explicit user override
2. If override is `__FULL_ACCESS__`, return null (no filtering)
3. If override points to a missing/disabled/deleted policy, return **deny-all sentinel** (fail-closed)
4. If no override, fall back to `DefaultPolicyId`
5. If `DefaultPolicyId` is set but policy not found/disabled, return **deny-all sentinel** (fail-closed)
6. If no default, return null (full access)

### Filename Matching (legacy)

This restricts nothing on Jellyfin 12. The filter that enforced it,
`MediaSourceResultFilter`, is no longer registered. The one live caller left is
`QualityGateIntroProvider`, which uses it to decide whether to skip an intro for an item the
user could not have played anyway. It removes no media sources. The pattern fields stay in the
config and the admin page as inert settings.

`QualityGateService.IsPathAllowed(policy, path)` checks both the original path and symlink-resolved path:

1. Null/empty path -> **DENIED** (fail-closed)
2. Matches any **blocked filename regex** -> **DENIED**
3. **Allowed filename patterns** defined and no match -> **DENIED**
4. Otherwise -> **ALLOWED**

Filename regex matching uses `RegexOptions.IgnoreCase` with a 1-second timeout (ReDoS protection). Both original and symlink-resolved filenames are checked.

`IsSourcePlayable(policy, path)` additionally checks `File.Exists()` to filter out dangling symlinks.

When all sources are blocked and fallback transcode is disabled, the filter returns an **empty array** (fail-closed). When fallback transcode is enabled, sources are kept but forced through server-side transcoding. The **DenyAllPolicy** sentinel (misconfiguration) NEVER triggers fallback — it always stays fail-closed.

### API Endpoints

The plugin ships one controller, `EncodePriority/EncodePriorityController.cs`, for the encode
priority status panel. Both routes are admin only (`Policies.RequiresElevation` on the class) and
neither is on the playback path:

- `GET /QualityGate/EncodePriority/Status` returns the state the scheduled task keeps, with item
  titles looked up at request time.
- `POST /QualityGate/EncodePriority/Preview` queues a run that builds every enabled encoder's
  list as a dry run and writes nothing.

Controllers mount by assembly scanning, so any new controller class is a new endpoint (still an
ask-first change). `QualityGateController` was deleted in v3.4.0.0 because its only job was
MediaSource filtering and the admin page never called it. A stale copy still sits untracked in
some working copies and is excluded from compilation in the csproj.

### UserId Resolution

`ResolutionCapFilter.GetUserId` reads `Jellyfin-UserId` first (Jellyfin 12's own claim, a Guid
in "N" format) and falls back to `ClaimTypes.NameIdentifier` for 10.x. It never accepts a
caller-supplied `userId` from the query or route: on the routes it gates, the caller does not
get to say who they are. Jellyfin's `[Authorize]` validates the caller before the filter runs.

## Configuration

Editable via **Dashboard -> Plugins -> Quality Gate**.

### Policies

| Field | Description |
|-------|-------------|
| **Policy Name** | Descriptive name (e.g., "720p Only") |
| **Maximum Resolution** | Height cap in pixels, matched against the media's video stream. Maps to `MaxHeight` (int). 0 = no cap, and no enforcement at all. **This is the field that enforces.** |
| **Allowed Filename Patterns** | Regex patterns matched against filenames. Files must match at least one. |
| **Blocked Filename Patterns** | Regex patterns matched against filenames. Matching files are always blocked. |
| **Custom Intro Video** | Optional path to intro video for users under this policy. |
| **If No Match Found** | Dropdown: Block playback (default), or transcode to 480p/720p/1080p/1440p/4K/no cap. Maps to `FallbackTranscode` (bool) + `FallbackMaxHeight` (int) in config. |
| **Enabled** | Toggle policy on/off |
| **Play within-cap versions as they are** | Maps to `KeepWithinCapVersionsDirect` (bool, default false). PlaybackInfo phase 1 raises the request's bitrate ceilings to what the item's within-cap versions need, so a bitrate limit alone never transcodes them. Cannot lift Jellyfin's remote client bitrate limit, which is not in the request |

### Config Model Fields Not Currently Enforced

`BlockedMessageHeader`, `BlockedMessageText`, `BlockedMessageTimeoutMs` -- present in the config model for backward compatibility but **removed from the admin UI**. Not enforced server-side. The filter silently removes sources; it does not send user-facing messages.

### Intro Video System

`IntroVideoPath` (per-policy) and `DefaultIntroVideoPath` (global fallback) are actively enforced by `QualityGateIntroProvider`. The provider registers intro videos in Jellyfin's database on first use via `ILibraryManager.CreateItem()`, then returns `IntroInfo { ItemId }`. The filter skips policy enforcement for configured intro paths so intros always play regardless of user restrictions.

## Development Guidelines

### Build

```bash
cd Jellyfin.Plugin.QualityGate
dotnet build -c Release
# Output: bin/Release/net10.0/Jellyfin.Plugin.QualityGate.dll
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

- **Jellyfin 12 issues no `ClaimTypes.NameIdentifier`**: `CustomAuthenticationHandler` emits `Jellyfin-UserId` holding a Guid in **"N" format** (no dashes), plus `ClaimTypes.Name` and `ClaimTypes.Role`. Any user resolution must read `Jellyfin-UserId` first. `Guid.TryParse` accepts the "N" form.
- **A `Height` CodecProfile condition is the lever for a resolution cap**: `StreamBuilder` maps a failed Height condition to `TranscodeReason.VideoResolutionNotSupported` (rules out direct play) and a `LessThanEqual` Height condition to `item.MaxHeight` (caps the transcode). One condition does both jobs.
- **`IsRequired` decides what happens to unknown values**: `ConditionProcessor.IsConditionSatisfied` returns `!condition.IsRequired` when the value is null. An unprobed item therefore *satisfies* a non-required cap and direct plays at full size. Mark the cap condition required.
- **Never derive a Width condition from a Height cap**: a 2.39:1 film at 720p is 1720px wide, so a 16:9-derived width cap would force a transcode of media already within the cap. Cap height only.
- **The `params` query blob overrides bound values inside the action**: `StreamingHelpers.ParseParams` splits `params` on `;` and assigns **by position** — index 2 `MediaSourceId`, index 3 `Static`, index 13 `MaxHeight`. It runs in `GetStreamingState`, i.e. after every MVC filter. A gate reading only the named query parameters is walked past by `?params=;;;true`.
- **`/Audio/{itemId}/stream` serves video**: `AudioController` never checks the item type, and `AudioHelper` returns `GetStaticFileResult(state.MediaPath, …)`. A Videos-only gate is bypassable through the Audio routes.
- **`/Items/{id}/File` returns the original, symlinks resolved**: only a plain `[Authorize]`, no resolution concept. `/Items/{id}/Download` is the same behind the `Download` policy.
- **The legacy HLS segment route cannot be gated**: `HlsSegmentController`'s `{itemId}` is declared but unused (`CA1801` suppression); the file is found by `segmentId`, an MD5 of media path + user agent + device id + play session id. There is no way to map a request back to an item without tracking transcode starts.
- **Jellyfin 12 ships no MVC filters of its own** and no `IFilterProvider`/`IApplicationModelConvention`, so a plugin's global filter runs unopposed on every controller, streaming routes included. Response compression sits outside routing and does not affect filter short-circuiting.
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
- **Jellyfin SDK pinning**: ALWAYS pin `Jellyfin.Controller`, `Jellyfin.Model`, and `Jellyfin.Common` to the MINIMUM supported minor version (e.g., `10.11.0`), NEVER use wildcards like `10.*-*` or `10.11.*`. Wildcards resolve to the latest patch at build time, which breaks users on older patch versions with `ReflectionTypeLoadException`. All plugin APIs used are stable across patch versions.

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
