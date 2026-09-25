# Encode priority: design

Status: implemented on branch `feat/encode-priority`, plan steps 1 to 13; not released yet. Written for a reader who knows Jellyfin but not this plugin's code. Where the code differs from what follows, the code is right and section 13 (Deviations) says how. The user guide is [docs/encode-priority.md](../encode-priority.md).

## 1. The problem

Quality Gate caps the resolution each user may be served. When a capped user opens an item that has a version within the cap, that version is served. When every version is above the cap, the user gets a capped live transcode (`Filters/ResolutionCapFilter.cs:265-301`).

The within-cap versions usually come from an encoder such as jellyfin-encoder, which makes 720p copies of a library. It works through the library in directory order. A capped viewer can follow a show for weeks while the next episodes sit thousands of files down the queue, and every one of them plays as a live transcode.

jellyfin-encoder 1.5.4 (PR #34, merged 2026-09-24) reads a priority list: a JSON file the encoder re-reads when it changes, with paths relative to its source folder, highest priority first. This design makes Quality Gate produce that file. The plugin already knows who is capped and which items have no within-cap version. Jellyfin already knows what each user is watching. Putting the two together gives a ranked list of the files capped viewers will reach next and that still need an encode.

The feature is called **encode priority**. It is off by default and changes nothing about playback.

### Vocabulary

These words are used the same way in the settings page, the logs, the docs and this document.

| Term | Meaning |
|---|---|
| **Encoder** | One encoder process, configured as one *target*. It reads one priority file and sees the media through one source folder. |
| **Folder mapping** | "Jellyfin sees this folder as X; inside the encoder's source folder it is Y." Y is usually empty because the two folders are the same directory mounted at different paths. |
| **Output height** | The height the encoder produces. jellyfin-encoder always produces 720. |
| **Audience** | The users whose viewing drives the list. By default, every capped user an encoder can help. |
| **Signal** | Evidence that a user is watching something: playing now, continue watching, next up, favourite. |
| **Gap** | An item a capped viewer gets as a live transcode: at least one version has a known height above the cap, and no version is within it. This is the same test playback applies. |
| **Covered** | A gap that has gained a within-cap version. It leaves the list. |
| **Served** | A covered item that a capped viewer then played on its within-cap version. This is the end-to-end proof. |
| **Unmapped** | A gap whose path is under no folder mapping of any encoder. It is counted and shown, never guessed. |
| **Priority file** | The JSON file written for one encoder. |

Two things the admin never sets, because they follow from settings that already exist:

- **Which users an encoder serves.** An encode at height H helps a viewer capped at C only when H is at most C. A 720p encoder serves users on 720p and 1080p policies, and cannot help users on a 480p policy.
- **Which libraries an encoder covers.** An item belongs to an encoder when its path is under one of that encoder's folder mappings. There is no separate library picker to keep in sync.

## 2. What the admin configures

All fields live on the existing flat `PluginConfiguration` (`Configuration/PluginConfiguration.cs`) plus two nested classes. **Basic** fields are visible as soon as the feature is switched on. **Advanced** fields sit in a collapsed block. Every default keeps the plugin's current behaviour.

### 2.1 Plugin-wide fields

| Field | Type | Default | Range | Tier | Meaning |
|---|---|---|---|---|---|
| `EnableEncodePriority` | bool | `false` | | Basic | Master switch. When off, nothing is built, and files the plugin wrote earlier are removed (section 6.4). |
| `EncodeTargets` | `List<EncodeTarget>` | empty | 0 to 16 | Basic | One entry per encoder. |
| `PriorityNowPlaying` | bool | `true` | | Basic | Signal: items a capped viewer is playing right now. |
| `PriorityContinueWatching` | bool | `true` | | Basic | Signal: resumable items per capped viewer. |
| `PriorityContinueWatchingPerUser` | int | `20` | 1 to 100 | Advanced | Most resumable items considered per viewer. |
| `PriorityNextUp` | bool | `true` | | Basic | Signal: the next episodes of shows a capped viewer is following. |
| `PriorityNextUpDepth` | int | `3` | 1 to 20 | Basic | Episodes ahead per show, counting the next-up episode itself. |
| `PriorityNextUpShowsPerUser` | int | `10` | 1 to 50 | Advanced | Most shows considered per viewer, most recently played first. |
| `PriorityNextUpIncludeSpecials` | bool | `false` | | Advanced | Include season 0 in the lookahead. |
| `PriorityFavourites` | bool | `false` | | Basic | Signal: favourite movies, and the next unwatched episodes of favourite shows. |
| `PriorityFavouritesPerUser` | int | `25` | 1 to 200 | Advanced | Most favourites considered per viewer. |
| `PriorityWatchedWithinDays` | int | `30` | 1 to 365 | Basic | Only activity in the last N days counts. Sets the Next Up cutoff and skips viewers idle for longer. |
| `PriorityUnprobedNeedsEncode` | bool | `false` | | Advanced | Treat a version with no known height as over the cap when building the list. Playback is not affected (section 4.3). |
| `PriorityRefreshOnPlayback` | bool | `true` | | Basic | Queue a run shortly after a capped viewer starts playback. |
| `PriorityDebounceMinutes` | int | `10` | 1 to 240 | Advanced | Minimum gap between playback-triggered runs. |
| `PriorityRunBudgetSeconds` | int | `180` | 10 to 1800 | Advanced | Hard runtime cap for one run. A run that goes over writes nothing and keeps the previous files. |

### 2.2 `EncodeTarget` (one per encoder)

| Field | Type | Default | Validation | Tier | Meaning |
|---|---|---|---|---|---|
| `Id` | string | new id from the page | non-empty, unique | hidden | Stable key for state and logs. |
| `Name` | string | `""` | 1 to 64 chars, unique | Basic | Label in the page, the logs and the file. |
| `Enabled` | bool | `true` | | Basic | Switch one encoder off without deleting it. Its file is removed. |
| `Folders` | `List<EncodeFolderMapping>` | empty | at least one when enabled; folders may not overlap within a target | Basic | The folders Jellyfin sees, and where each sits inside the encoder's source folder. |
| `OutputHeight` | int | `720` | 144 to 4320 | Basic | What the encoder produces. |
| `OutputMode` | string | `SourceFolder` | one of `SourceFolder`, `DataFolder`, `Custom` | Basic | Where the file goes (section 6.1). |
| `OutputPath` | string | `""` | required for `Custom`; absolute; when inside any library location the file name must start with `.` | Basic (Custom only) | The file, as Jellyfin sees it. |
| `AudienceMode` | string | `Auto` | one of `Auto`, `Policies`, `Users` | Advanced | Who counts (section 4.1). |
| `AudiencePolicyIds` | `List<string>` | empty | at least one when mode is `Policies` | Advanced | Policies whose users count. |
| `AudienceUserIds` | `List<Guid>` | empty | at least one when mode is `Users` | Advanced | Users who count, capped or not. |
| `ExcludedUserIds` | `List<Guid>` | empty | | Advanced | Users who never count. |
| `ResolveSymlinks` | bool | `false` | | Advanced | Resolve each path to its final target before mapping. For libraries that point at a tree of links. |
| `MaxEntries` | int | `300` | 1 to 5000 | Advanced | Longest list written. |
| `DryRun` | bool | `false` | | Advanced | Build and report, write no file. The target claims no output path, so an earlier file is removed. |

### 2.3 `EncodeFolderMapping`

| Field | Type | Default | Validation | Tier | Meaning |
|---|---|---|---|---|---|
| `JellyfinPath` | string | `""` | absolute; trailing separators trimmed; warning (not error) when under no library location | Basic | The folder as Jellyfin sees it inside its container. |
| `EncoderPath` | string | `""` | relative; `/` separators only; no leading `/`; no `..`; no `\` | Advanced | Where that folder sits inside the encoder's source folder. Empty means it is the source folder itself. |

Output paths must be unique across enabled, non-dry-run targets. Two targets may share a `JellyfinPath` (the 480p plus 720p example in section 4.4 needs it); one target may not list overlapping folders.

Enumerated fields are strings, not C# enums. An unknown value falls back to the default with one warning in the log. A C# enum would make the whole config save fail to deserialise, and the admin's other changes would be lost with it.

## 3. The settings page

One new section on the existing page (`Configuration/configPage.html`), placed after Version Grouping and before User Access. It is not a second page: `PluginTests` pins the page count at two. The section has five blocks.

### 3.1 Header

- Toggle: **Prioritise encodes for capped viewers** (`EnableEncodePriority`).
- One paragraph: "Writes a list of what capped viewers are watching so your encoder makes those copies first. Works with jellyfin-encoder 1.5.4 or newer. Playback is not changed."
- A link to `docs/encode-priority.md`.
- While the toggle is off, blocks 3.2 to 3.5 are collapsed and greyed out. Their values stay in the config object, so they round-trip through save.

### 3.2 What counts as being watched

Checkboxes with number fields beside them, laid out in rank order so the order on screen is the order in the file:

1. Playing right now
2. Continue watching
3. Next up, [3] episodes ahead
4. Favourites (off by default)
5. "Only count activity from the last [30] days"

The per-user limits, specials and the unprobed rule are in the Advanced block at the bottom of the section.

### 3.3 Encoders

One card per target with **Add encoder** and **Remove** buttons. A collapsed card shows one summary line, for example: `Shows · /media/tv → encoder root · 720p · serves 2 policies (41 viewers) · writes /media/tv/.encoder-priority.json`.

An expanded card has:

- **Name**, **Enabled**.
- **Folders**: rows of `[Jellyfin folder] [inside the encoder's source folder, usually empty] [×]` plus **Add folder**. The Jellyfin folder input has a `<datalist>` filled from `GET /Library/VirtualFolders` (each library's `Locations`). Free text stays allowed, because a folder can be a subfolder of a location. The second column is hidden until the admin opens Advanced on the card; with one folder and an empty encoder path, the encoder's source folder is that Jellyfin folder.
- A live example line, computed in the page: "`/media/tv/Show Name/Season 2/Show Name S02E05.mkv` will be written as `Show Name/Season 2/Show Name S02E05.mkv`". It uses the first mapping plus a fixed sample file name and the same pure mapping function the tests exercise.
- **Encoder output height**: a select with 480, 576, 720, 1080, 1440, 2160. It always includes the stored value even when that value is not in the list. A dropdown that drops the stored value makes the next save silently write a different one (the lesson behind `heightChoices`, `configPage.js:225-246`).
- **Where to write**: three radios (section 6.1). The resolved path is shown read-only. Under it, an **Encoder setup** box with copy-paste text for the chosen mode.
- **Derived lines**, computed from the config and the users the page already loads:
  - "Serves: *Kids 720p* (12 viewers), *Remote 1080p* (3 viewers)."
  - "Cannot help: *Mobile 480p*, whose cap is below this encoder's 720p output. Those viewers keep getting live transcodes." A warning, not an error.
- **Advanced** (collapsed): who counts (radios: *Capped viewers this encoder can help*, *Only users on these policies* with a checklist showing each policy's cap and greying out policies below the output height, *These users, even if uncapped* with a user checklist), never count these users, resolve symlinks, max entries, dry run.
- Stored ids that no longer resolve are shown as "Unknown user (id…)" or "Deleted policy (id…)" and kept, for the same reason the height select keeps its value.

Validation before save, client side like `validateRegexPatterns` (`configPage.js:1104-1119`): absolute Jellyfin folders; an output file inside a library location must be a dotfile; unique names; unique output files; no duplicate Jellyfin folders; at least one folder per enabled target; the audience lists non-empty for their modes.

### 3.4 Refresh

- ☑ **Refresh when a capped viewer starts playing**, minimum gap [10] minutes.
- A read-only line: "Scheduled runs: edit under Dashboard › Scheduled Tasks › *Quality Gate: build encode priority lists*." The plugin does not duplicate Jellyfin's trigger editor.
- **Run now**: `POST /ScheduledTasks/Running/{taskId}`, an existing admin endpoint. The task id comes from `GET /ScheduledTasks`, matched on `Key`.
- Run budget lives in Advanced.

### 3.5 Status

Per target, a small panel:

| Element | Contents | Source |
|---|---|---|
| Badge | `Off` · `Waiting for first run` · `OK` · `OK, unchanged` · `Warning` · `Error` · `Timed out, previous list kept` · `Dry run` | the target's last run from the status endpoint; without it, scheduled task result plus activity entries |
| Timeline | Last run (time, duration, trigger), last file write | same |
| Counts | Viewers · demand items · gaps · **listed** · covered · height unknown · unmapped · cut by the limit | activity entries |
| Findings | One line each with the fix (section 7.2) | activity entries |
| Preview | Rank · title · path as the encoder sees it · versions with heights · reason chips | status endpoint (section 7.3, ask-first) |

Without the status endpoint the page still shows the badge, the timeline, the counts and the findings, because those come from two endpoints Jellyfin already ships: `GET /ScheduledTasks/{taskId}` (`LastExecutionResult` with status, time and error message) and `GET /System/ActivityLog/Entries?type=QualityGate.EncodePriority&limit=20`. The preview table needs the endpoint.

A note under the panel: "The encoder applies a new list the next time it picks a file. A running encode is never interrupted."

### 3.6 Page plumbing

- `loadConfig` gets a normalisation line per new field (`configPage.js:1055-1071`), and `collectFromDOM` maps every control back (`configPage.js:880-915`). The page posts the whole object, so server fields it does not render still round-trip.
- Exported pure helpers so the node-driven tests can reach them (the `ConfigPageHeightOptionsTests` pattern): `mapToEncoderPath(path, mappings, isWindows)`, `resolveOutputPath(target, dataPath)`, `targetAudience(target, config, users)`, `validateEncodeTargets(config, libraryLocations)`.

## 4. How the list is built

One run of the scheduled task, one pipeline. Inputs: a snapshot of the config, `IUserManager`, `ILibraryManager`, `ITVSeriesManager`, `ISessionManager`, `IUserDataManager`, `IMediaSourceManager`, `IActivityManager` and `IApplicationPaths`. All are constructor-injectable in a plugin. A `CancellationTokenSource` links the task token with the run budget.

**Step 0: options.** `EncodePriorityOptions.From(config)` clamps every integer into its range and maps unknown enum strings to their defaults. Validation on the page is client-side only, and a hand-edited XML must not produce `MaxEntries = 0`.

**Step 0b: cleanup pass.** Always runs, even when the feature is off (section 6.4). Then, if the feature is off or no target is enabled, the run stops.

### 4.1 Audience

For each user from `IUserManager.GetUsers()` (there is no `Users` property; `IUserManager.cs:28`):

- Skip disabled users, users in the target's `ExcludedUserIds`, and users whose `LastActivityDate` is older than `PriorityWatchedWithinDays`.
- `cap = QualityGateService.GetUserPolicy(user.Id)?.MaxHeight ?? 0` (`Services/QualityGateService.cs:37-77`).
- `Auto`: the user counts for a target when `cap > 0` and `cap >= OutputHeight`.
- `Policies`: as `Auto`, and the resolved policy id is in `AudiencePolicyIds`.
- `Users`: the user is in `AudienceUserIds`. An uncapped user is treated as capped at `OutputHeight`. A user capped below `OutputHeight` is skipped and counted in the findings.

Users on a deleted or disabled policy resolve to the deny-all sentinel, whose `MaxHeight` is 0 (`QualityGateService.cs:22-28`). They are uncapped in playback today and are not audience here. The API-key policy has no watch history and is ignored.

The audience is computed once per user and reused by every target, so the per-user queries below run once per user, not once per target.

### 4.2 Demand

Every query is an `InternalItemsQuery(user)`, so each user's library access and parental rules apply and the results are what that user can actually see.

| Signal | Tier | Query |
|---|---|---|
| Playing now | 0 | `ISessionManager.Sessions` where the user is audience and `NowPlayingItem` is set. The item is a candidate; an episode also feeds its series into the Next Up lookahead. |
| Continue watching | 1 | `IsResumable = true, Recursive = true, IsVirtualItem = false, OrderBy DatePlayed desc, Limit = PriorityContinueWatchingPerUser`. Jellyfin 12 returns the version the user actually played, so a viewer resuming a 720p copy is already covered. |
| Next up | 2 | `ITVSeriesManager.GetNextUp(new NextUpQuery { User, EnableResumable = true, NextUpDateCutoff = now - days, Limit = PriorityNextUpShowsPerUser })`. Both settings are explicit: the in-process default for `EnableResumable` is false, which drops the very episode being watched, and without a cutoff the whole watch history is scanned. For depth greater than 1, one query per show: `SeriesPresentationUniqueKey = key, IncludeItemTypes = [Episode], MinParentAndIndexNumber = (season, episode), OrderBy ParentIndexNumber asc, IndexNumber asc, Limit = depth, IsVirtualItem = false`. The key is the presentation key, never `SeriesId`: a library with one folder per season produces several `Series` items for one show (the trap fixed in v3.8.2.0). Season 0 is skipped unless `PriorityNextUpIncludeSpecials`, and so is the lookahead after a special being played now; an episode with no season number feeds no lookahead, since it has no position to start from. |
| Favourites | 3 | `IsFavorite = true`. Movies are candidates directly. Series go through the same lookahead, starting at their Next Up episode or else the first unplayed episode. |

The lookahead runs without a user and is cached on `(series key, season, episode, depth)`, so ten viewers of one show cost one query. It only orders encodes and never shows anything to anyone.

Candidates are keyed by item id and carry `{ tier, depth, users, lastActivity }`. A duplicate keeps its best tier and depth and adds to the user set. Only the demand of viewers for whom the item is a gap (section 4.3) is merged: a viewer who already has a version within their cap does not need the encode, so their signal neither ranks the item nor gives it a reason, and does not count as one of its users. `depth` is 1 for the next-up episode and 2 or more for the lookahead; tier 0 and 1 items have depth 0.

### 4.3 Gap test

One new pure static function in `QualityGateService`, next to `ExceedsHeightCap` (`QualityGateService.cs:172-186`):

```csharp
/// True when a viewer capped at <paramref name="cap"/> would get a forced live transcode:
/// at least one source, and none within the cap.
public static bool IsCapGap(IReadOnlyList<int?> sourceHeights, int cap, bool unprobedIsOverCap = false)
```

- Default rule: `cap > 0`, at least one height, and every height exceeds the cap under `ExceedsHeightCap`, which treats an unknown height as within the cap.
- With `unprobedIsOverCap`: an unknown height counts as over the cap. This flag is passed only by the list builder, from `PriorityUnprobedNeedsEncode`. Playback never passes it.
- `CapPlaybackInfo` (`ResolutionCapFilter.cs:265-301`) is changed to call `IsCapGap` for its "every source exceeds" branch, with no behaviour change. It tests `MediaSourceInfo` objects today (`ExceedsHeightCap(policy, source)`), so the heights are read with `GetVideoHeight(source)` first and the list of `int?` is what `IsCapGap` sees. The list and playback then share one predicate and cannot drift apart.

For each candidate: `versions = video.GetAllVersions()` (Jellyfin 12; includes the primary when called on an alternate). Each version's height is measured the way playback measures it, `GetVideoHeight(IMediaSourceManager.GetMediaStreams(version.Id))` (`ResolutionCapFilter.cs:471-474`), cached per run. This is one DB call per version on the bounded candidate set, never across the library. `InternalItemsQuery.MaxHeight` is not used: in Jellyfin 12 it is version-aware and excludes any item with one version above the cap, so it cannot express "has a within-cap version".

The gap is evaluated at each demanding user's own cap. The candidate records `gapCaps`, the caps of every user for whom it is a gap. An item that is a gap only for a 1080p viewer (a 2160p-only file) is kept only for targets with `OutputHeight <= 1080`.

Unknown heights, default rule: an item whose versions all lack a height is not a gap, because playback serves it directly. An unprobed encoded copy counts as covering its original, as it does in playback. Such items are counted as "height unknown" and the findings suggest running *Extract media info*. Playback is untouched either way.

### 4.4 Path mapping per target

For each gap and each enabled target where `OutputHeight <= max(gapCaps)`:

1. Take every version whose height is above `OutputHeight` (or unknown, with the flag), lowest height first. The lowest is the cheapest source. Listing the others is harmless: the encoder skips a file whose output already exists.
2. With `ResolveSymlinks`, apply `File.ResolveLinkTarget(path, returnFinalTarget: true)`. A failure falls back to the original path and is counted.
3. Match the path against the target's mappings with the longest matching `JellyfinPath`, using the shared rule extracted from `VersionGroupingResolver.IsUnderConfiguredRoot` (`Library/VersionGroupingResolver.cs:95-138`) into a new `Library/PathRoots` helper: equal to the root or root plus separator, trailing separators trimmed, both separators accepted on Windows only (on Linux `Path.AltDirectorySeparatorChar` is `/` too, and `\` is an ordinary filename character), case-insensitive only on Windows.
4. The entry is `EncoderPath` plus the remainder, joined with `/`, no leading slash, and byte-identical: no case folding and no Unicode normalisation, because the encoder compares exact path components.
5. No mapping matches: the gap is **unmapped**. Its library name and up to five example paths go into the findings.

Episodes are written as file entries, never as show folders. A folder entry sorts `Season 10` before `Season 2` on the encoder and pulls extras in between seasons.

Examples:

- One encoder per library, same directory mounted at different paths. Jellyfin sees `/media/tv`; the encoder's `SOURCE_FOLDER` is `/app/source`, mounted from the same share. Mapping: `JellyfinPath = /media/tv`, `EncoderPath` empty. `/media/tv/Show Name/Season 2/Show Name S02E05.mkv` becomes `Show Name/Season 2/Show Name S02E05.mkv`.
- One encoder for two libraries. The encoder's `SOURCE_FOLDER` is `/data`, which holds `movies/` and `tv/`; Jellyfin sees them as `/media/movies` and `/media/tv`. Mappings: `/media/movies → movies` and `/media/tv → tv`. `/media/movies/Film Title (2019)/Film Title (2019).mkv` becomes `movies/Film Title (2019)/Film Title (2019).mkv`.
- Two encoders at different heights. A 480p encoder and a 720p encoder over the same folder are two targets. A 1080p-only item watched by a 720p viewer is listed for both. The same item watched only by a 480p viewer is listed for the 480p encoder only, because a 720p copy would still be over that viewer's cap.

### 4.5 Order, dedupe, cut

Sort key per target: `(tier, depth, -distinctUsers, -lastActivity, path)`.

- `depth` before `users` means every show a capped viewer follows gets its next episode before any show gets its third. The encoder works one file at a time and a viewer watches about one episode per sitting, so covering what each viewer opens next matters more than going deep on one show.
- `path` last makes the order deterministic. The same inputs give a byte-identical file, and an unchanged file is not rewritten.
- Dedupe entries, keeping the first position (the encoder also keeps a duplicate's first rank).
- Cut to `MaxEntries`, recording how many were cut.

## 5. When and how it runs

| Trigger | Mechanism | Purpose |
|---|---|---|
| Scheduled | `EncodePriorityTask : IScheduledTask`, "Quality Gate: build encode priority lists", key `QualityGateEncodePriority`, category "Quality Gate". Default triggers: every hour and at startup. Editable under Scheduled Tasks. Found by export scan, so no registration is needed. | Baseline refresh, and state restore after a restart. |
| Library scan finished | `EncodePriorityPostScanTask : ILibraryPostScanTask` queues the task through `ITaskManager.QueueScheduledTask<EncodePriorityTask>()`. Found by export scan. | A new within-cap version arrives through a scan, so covered items drop off the list soon after. |
| Capped viewer starts playing | `EncodePriorityEvents : IHostedService`, registered with `AddHostedService` in `PluginServiceRegistrator`. It subscribes to the legacy `ISessionManager.PlaybackStart` event, which Jellyfin dispatches on `Task.Run`. It does not use `IEventConsumer<PlaybackStartEventArgs>`, which is awaited inline while the client's playback-start report waits. The handler does in-memory checks only (feature on, refresh on, user capped), then arms a fixed-window timer of `PriorityDebounceMinutes`. When the timer fires it queues the task. No DB access in the handler. `PlaybackProgress` is ignored. | A newly started show is ranked within minutes. |
| Config saved | The same hosted service subscribes to `Plugin.Instance.ConfigurationChanged` (a settable delegate property on `BasePlugin<T>`, `BasePluginOfT.cs:90`, so `+=`, never `=`) and queues the task. With the feature just switched off, the queued run performs only the cleanup pass. Keeping this in the hosted service avoids adding `ITaskManager` to the `Plugin` constructor, which ten test files build with `new Plugin(...)`. | The admin sees the effect of a change immediately. |
| Uninstall | `Plugin.OnUninstalling()` runs the cleanup pass synchronously, best effort. It needs only the state file and file IO, no DI. | No stale file left behind. |

Jellyfin's task manager never runs one task twice at once; a trigger arriving mid-run runs once afterwards. The encoder reads the list only when a worker frees up and picks the next file, so a refresh within minutes is all that helps. The hourly interval is the safety net if an event is missed.

Nothing runs on the request path. `ResolutionCapFilter` changes only by calling `IsCapGap`, and nothing in PlaybackInfo or delivery waits on the builder.

## 6. Output: format, location, the way out

### 6.1 Where the file goes

| `OutputMode` | Resolved path | Encoder setup text shown on the page |
|---|---|---|
| `SourceFolder` | `<JellyfinPath of the mapping with empty EncoderPath>/.encoder-priority.json`. This is jellyfin-encoder's default `PRIORITY_FILE`. Invalid when no mapping has an empty encoder path: "the encoder's source folder is not a folder Jellyfin sees; use Data folder or Custom". | "Nothing to set. jellyfin-encoder reads `<SOURCE_FOLDER>/.encoder-priority.json` by default." |
| `DataFolder` | `<IApplicationPaths.DataPath>/quality-gate/encode-priority/<Id[0..8]>.json`. The directory is created if missing. | "Jellyfin's media mount is read-only, so the file is written under Jellyfin's data folder. Mount `<resolved path>` into the encoder read-only and set `PRIORITY_FILE` to its path inside the encoder container. In the official and linuxserver images the data folder is `/config/data`." |
| `Custom` | `OutputPath`. The directory must already exist; the plugin never creates folders inside a library. | "Set `PRIORITY_FILE` to this file as the encoder sees it." |

`DataFolder` deliberately does not use `BasePlugin.DataFolderPath`. That path is `plugins/Jellyfin.Plugin.QualityGate`, or `plugins/Jellyfin.Plugin.QualityGate_<version>` when the unversioned folder does not exist (`BasePluginOfT.cs:50-55`), so on a normal install it carries the version and moves on every upgrade, which would break the encoder's bind mount. `IApplicationPaths.DataPath` stays put. A remote encoder can use `DataFolder` only when Jellyfin's data folder is shared to it over the network; an encoder that already mounts the media share needs no new mount with `SourceFolder`. The plugin's state file (section 7.1) lives there for the same reason.

The read-only library case is the common one in Docker: media is mounted `:ro`, and a write into it fails with `UnauthorizedAccessException` or `IOException`. The plugin reports it as a `WriteFailed` finding with the fix (switch to Data folder) and keeps the other targets writing.

### 6.2 Format

```json
{"generated":"2026-09-25T10:12:03Z","producer":"quality-gate","target":"Shows","paths":["Show Name/Season 2/Show Name S02E05.mkv","Show Name/Season 2/Show Name S02E06.mkv"]}
```

- Flat, no nesting. The encoder reads only `paths`; `producer` lets the plugin recognise its own files before deleting them; `generated` and `target` are for humans and for a future max-age check in the encoder.
- UTF-8 **without a BOM**. Serialise with `System.Text.Json` straight to a `FileStream`. Never `Encoding.UTF8` in a `StreamWriter`: that emits a BOM, `json.load` rejects it, and the encoder logs one warning (`unreadable or not JSON (JSONDecodeError: Unexpected UTF-8 BOM ...)`) and runs in arrival order (`app/monitor.py:1223-1244`).
- Paths are relative, `/`-separated, no leading slash. The encoder does tolerate a leading slash and `.` components (`_path_parts` drops empty and `.` parts, `app/monitor.py:1218-1220`); the rule keeps the file tidy, it is not what matching depends on.

### 6.3 Write rules

1. **Refuse a foreign file.** If the output exists and does not parse as JSON with `"producer":"quality-gate"`, the target is skipped with a `ForeignFile` finding: "a file the plugin did not write is at X; delete it or choose another output". This protects a hand-written list. Anything at the output that is not a regular file (a symlink, a named pipe, a device) or is empty or over 64 MiB counts as foreign and is never opened: the folder may be writable by others, and opening a pipe blocks the task for good.
2. **Write only when `paths` changes.** Compare with the existing file's `paths`. A rewrite moves the mtime, and every mtime change makes the encoder rebuild its queue and log a line. Exception: a heartbeat rewrite with a fresh `generated` when the file is older than 24 hours, so a max-age check in the encoder can work later.
3. **Atomic.** Write `.<name>.tmp` in the same directory, flush, then `File.Move(tmp, target, overwrite: true)`. Whatever sits at the temp name is deleted first and the temp file is opened with `FileMode.CreateNew` (`O_EXCL`): the output folder may be writable by others, and a symlink planted at the temp name must never be followed into another file. Both names start with a dot, so the encoder ignores them and Jellyfin's library monitor ignores them (`**/.*` is in the ignore list, and the monitor returns early for ignored paths).
4. **An empty result is still written**, as `"paths":[]`, when the build succeeded and found nothing. That is the truth: nothing needs priority.
5. **A failed or timed-out build never writes.** The previous file stays for every unfinished target and the status says so. A partial list would silently reorder the encoder.
6. Any IO failure deletes the temp file, keeps the previous file, and records the finding. There is no non-atomic fallback.

### 6.4 The way out

The encoder never expires a list it has read. It keeps reordering its queue by the last file until the file changes or disappears. So the plugin must remove what it wrote:

- The state file records every output path the plugin has written.
- The cleanup pass at the start of every run removes any recorded file not claimed by an enabled, non-dry-run target of an enabled feature. It deletes the file only if it parses and carries `"producer":"quality-gate"`. If the delete fails it writes `{"producer":"quality-gate","paths":[]}` instead. If both fail it logs and keeps the record for a retry.
- This covers: feature switched off, target disabled or removed, output mode or path changed (the old file goes), dry run switched on, and uninstall.
- Not covered: Jellyfin down, or the plugin removed by deleting its folder. The last list stays in force. The encoder-side fix is a `PRIORITY_MAX_AGE_HOURS` setting that ignores a list whose `generated` is too old; the 24-hour heartbeat exists so that check can be tight.

## 7. Status, preview and end-to-end verification

### 7.1 State file

`<IApplicationPaths.DataPath>/quality-gate/encode-priority/state.json`, written atomically at the end of every run that changed it. A run with nothing to build (the feature off, or no enabled encoder) leaves an unchanged state alone, so a server that never turns the feature on gets no folder and no hourly write; uninstall saves the state only if one exists. It is not config, so the page's whole-object save cannot overwrite it. Contents:

- Per target: last run (time, trigger, duration, result), last write, the written list with per-entry `itemId`, `firstListedAt` and reasons, counts, findings.
- Per covered item, bounded to 7 days or 500 rows: `itemId`, `listedAt`, `coveredAt`, `servedAt`, `servedVersionId`.
- Files written, for the cleanup pass.

It holds item and user ids, never names.

### 7.2 Findings

Each finding has a code, a one-line explanation and the fix. They appear in the page (via activity entries), in the state file, and in the log with the `QualityGate: ` prefix and structured placeholders.

| Code | Trigger | Message and fix |
|---|---|---|
| `NoAudience` | No user counts for this target | "No viewer benefits from this encoder's 720p output." Lists the policies and their caps. |
| `FolderMissing` | A `JellyfinPath` does not exist on the Jellyfin host | "Use the path Jellyfin sees inside its container, not the host path." |
| `FolderNotInLibrary` | A `JellyfinPath` is under no library location | "No library item can ever be under this folder." Lists the library locations. |
| `MostlyUnmapped` | Unmapped gaps exceed 25% | "N of M watched gaps are under `<folder>` (library *X*), which this encoder does not cover. If that is a tree of links, add the tree as a folder and turn on Resolve symlinks, or point the folder at the real files." Five example paths. |
| `UnknownHeights` | Candidates with no known height | "N watched items have never been probed. Run *Extract media info* or a library scan so Quality Gate can see their resolution." |
| `ForeignFile` | Section 6.3 rule 1 | Includes the path. |
| `WriteFailed` | Section 6.1 | Includes the exception message and the Data folder recipe. |
| `Stuck` | The top ten entries of an unchanged list have been listed more than 48 hours, none of the target's gaps was covered in that time, and the file is older than 48 hours | "The encoder does not seem to be working on this list. Check that `PRIORITY_FILE` points at this file as the encoder sees it, that `SOURCE_FOLDER` is the folder mapped here, and the INFO line the encoder logs when it reloads the list: `Priority list <file>: N entries, M of P pending files match` (`app/monitor.py:1401-1405`). Zero matching files means the folder mapping is wrong." This is the plugin's only view of an encoder that does not read the file, so it says *seem*. |
| `TimedOut` | Run budget exceeded | "Previous list kept. N of M viewers processed. Lower Next up shows per viewer or depth." |
| `AudienceBelowOutput` | `Users` mode names users capped below the output height | Says how many; their copies would still be over their cap. |
| `OutputInvalid` | The output mode cannot resolve a file (Source folder with no folder at the encoder root, a Custom path that is not absolute or is a visible file inside a library) | The reason, with the fix. |
| `OutputConflict` | A second enabled, writing target resolves to a file another already writes | Only the first writes. |
| `ServedOverCap` | A covered item was played by a capped viewer on a version above their cap (section 7.4) | Up to five item ids. Kept while the covered record is (7 days). |

Activity log entries (`IActivityManager.CreateAsync`, `Type = "QualityGate.EncodePriority"`, user id `Guid.Empty` because the `ActivityLog` constructor takes one) are written once per target on each run where the list changed or a finding appeared or cleared, so volume stays at most one per run per target. `ShortOverview` carries the counts: `Shows: 41 listed (+5, -3) · 7 viewers · 2 unmapped`.

### 7.3 Status endpoint (ask-first, approved)

New API endpoints are an ask-first item in this repo's `CLAUDE.md`. The page works without one (section 3.5), so the endpoint was a separate step; the owner approved it:

- `GET /QualityGate/EncodePriority/Status`, admin only (`RequiresElevation`), returns the state file with item titles looked up per request, plus Jellyfin's data folder so the page can show Data folder paths. It powers the list and preview tables and the covered and served trail.
- `POST /QualityGate/EncodePriority/Preview`, admin only, queues a run that builds every enabled target's list as a dry run, even with the feature saved as off. The preview writes and removes no file, writes no activity entry and leaves each target's real last run alone; it is kept in a separate `Preview` slot of the state.

It is read-only apart from queueing a dry run, it is not on the playback path, and it is not a revival of the removed `QualityGateController`. Controllers in a plugin assembly are found by assembly scan, so it needs no registration.

### 7.4 End-to-end proof: listed, covered, served

1. **Listed.** The item is in the file. The preview shows the encoder-side path and the reason.
2. **Covered.** A later run finds the item is no longer a gap because a within-cap version now exists. It gets `coveredAt`. The post-scan trigger makes this happen soon after the version appears. A new version with no height yet counts as within the cap, so the item can leave the list before the probe finishes; it is then kept as pending on its target and checked again on each run, for up to 7 days, until the height is known.
3. **Served.** The hosted service also watches `PlaybackStart` for a capped user playing an item in the covered set. `MediaSourceId` on the event is the version actually played. If it is the recorded within-cap version, the handler appends `(itemId, versionId, time)` to an in-memory queue, and the next run folds it into the state as `servedAt`. A covered item played on a source above the player's cap, when the item has a version at or below that cap, means Quality Gate offered no sibling; that is a real bug and is flagged `ServedOverCap`. Every capped viewer's play is noted, so a viewer capped below every version (a correct transcode) and a within-cap play of another version (a viewer with a higher cap on the original) prove nothing and are dropped. Only the task writes the state file, so there is no concurrent write.

The 7-day summary on the page is these counts: "34 listed · 29 covered (median 3 h 10 m) · 21 served within cap · 0 played over the cap". The docs page adds the manual recipe: pick a row in the preview, watch the encoder log for the reload line and its match count, wait for the encode, open the item in Jellyfin and see two versions, play it as a capped user, and check the dashboard shows direct play of the within-cap version.

### 7.5 Encoder-side changes before release

jellyfin-encoder PR #34 is merged (2026-09-24) and shipped as v1.5.4 (`drumsergio/jellyfin-encoder:1.5.4` on Docker Hub the same night). The two review findings it had are fixed in that release: a file resubmitted while it is encoding is queued again when the run ends (`EncodeQueue._requeue`, `app/monitor.py:1313-1326` and `1368-1373`), and the JSON load catches every exception, `RecursionError` included, so a bad file cannot kill the dispatcher (`app/monitor.py:1235-1239`). What the encoder still lacks, and needs a follow-up PR before this feature relies on it:

- Tolerance for a UTF-8 BOM (`utf-8-sig`) and NFC normalisation on both sides of the match. A forgiving consumer is cheaper than a perfect producer. Neither exists in 1.5.4.
- `PRIORITY_MAX_AGE_HOURS`, so a dead producer cannot pin the order forever. Not in 1.5.4.

## 8. Compatibility

- **Defaults keep 3.8.2.0 behaviour.** `EnableEncodePriority` is false, `EncodeTargets` is empty. An XML config saved by an earlier version has none of the new elements; `XmlSerializer` leaves the initialiser values in place, so the feature is off. A downgrade drops the new elements, because `XmlSerializer` ignores unknown ones.
- **Disabled means zero change.** No task work beyond the cleanup pass (no library read, and no state write unless the cleanup changed something), no event subscription work beyond an in-memory flag check, no request-path change beyond `CapPlaybackInfo` calling a predicate with the same truth table as before.
- **Lists start empty.** `XmlSerializer` appends loaded items to a list property that already holds items from its initialiser. `VersionGroupingSuffixes` (`PluginConfiguration.cs:75`) has this bug today: every save plus restart adds another `" - 720p"`, and a cleared list comes back. No new list gets initialiser items. Defaults are applied at read time in `EncodePriorityOptions.From` and as placeholders on the page. The fix for `VersionGroupingSuffixes` ships first, as its own change, with the first real `XmlSerializer` round-trip test in the repo; every existing test mocks the serializer.
- **Save path.** An admin save arrives as JSON, is deserialised into the config type and then written as XML. Enumerated fields are strings so an unknown value cannot fail the whole save. Integers are clamped when read, not on save.
- **Saved config migration.** None needed. There is no earlier shape of these fields.
- **Target ABI stays 12.0.0.0.** The design uses `Video.GetAllVersions()`, `ITVSeriesManager.GetNextUp`, `InternalItemsQuery` with `SeriesPresentationUniqueKey`, `MinParentAndIndexNumber`, `IsResumable`, `IsFavorite`, `ILibraryPostScanTask`, `IScheduledTask`, `ITaskManager`, `IHostedService` and `IActivityManager`, all present in 12.0. It does not use `InternalItemsQuery.IncludeAlternateVersions` or `ILibraryManager.GetTagNames`, which arrived in 12.1.
- **Plugin id** stays `9cab70ca-0af3-4d3a-adab-6a0df2496a33`.

## 9. Performance

The library is never scanned. Per audience user: one resumable query, one batched Next Up query (bounded by the date cutoff), one favourites query when enabled, and at most `PriorityNextUpShowsPerUser` lookahead queries shared through the per-run cache. Then `GetAllVersions` and `GetMediaStreams` for the deduplicated candidates only.

Worst case with 200 capped users and the defaults: about 400 to 600 small indexed queries plus one lookahead per distinct show in progress, then stream reads for a few thousand versions. That is tens of seconds in the background, under the 180-second budget. Users idle for more than `PriorityWatchedWithinDays` are skipped before any query.

Guards: the `CancellationToken` is checked between users and between candidates; progress is reported per user; a run that trips the budget writes nothing and says so; `MaxEntries` bounds the file.

Before release, a run is timed in dry-run mode against a large real library and the number goes into the docs. The figures above are estimates.

## 10. Out of scope

- **Serving the list over HTTP for the encoder to pull.** The encoder reads only a local path. A remote encoder mounts the Data folder output, or a script fetches the status endpoint and pushes the list.
- **Adapters for Tdarr, Unmanic or FileFlows.** Only Unmanic has a reorder API (`/api/v2/pending/reorder`). The status endpoint exposes the list if a script is wanted later.
- **Recently added items, collection order for movies, uncapped users' viewing, and the API-key policy as audience.** None of these is a capped viewer hitting a live transcode. The `Users` audience mode covers the admin who wants specific uncapped users counted.
- **Weighted scoring and a reorderable signal list.** Tiers plus breadth-first lookahead answer "why is X before Y" from the page; a weight formula does not.
- **Encoding at heights the encoder cannot produce.** `OutputHeight` states the truth and the page flags policies no encoder serves.
- **Pre-empting a running encode.** The list takes effect at the encoder's next pick.
- **Any change to how playback treats unknown heights.**
- **Stale page text about filename regex policies** (`configPage.html:479-493`) and the tracked `publish/` folder. Separate cleanups.

## 11. Open questions for the owner

1. Approve the admin-only status and preview endpoint (section 7.3)? Without it the page shows badge, counts and findings but no preview table or served trail. **Approved; shipped in step 12.**
2. Ship the `VersionGroupingSuffixes` fix first as its own change? Recommended yes. **Done as step 1.**
3. Open the jellyfin-encoder follow-up PR (section 7.5: BOM tolerance, NFC, `PRIORITY_MAX_AGE_HOURS`) before this feature ships? Recommended yes.
4. Confirm which folders the production libraries point at, the real files or a tree of links, before the first live run. `GET /Library/VirtualFolders` answers it.
5. Default scheduled interval: every hour is proposed. Should it be shorter for a server with events switched off?
6. Should `Favourites` be on by default? Proposed off, since a favourite is a weaker signal than a show in progress.

## 12. Implementation plan

Each step is one reviewed commit. Every step leaves the plugin releasable with the feature off.

1. **Fix `VersionGroupingSuffixes` duplication.** Initialise the list empty; apply `" - 720p"` at read time in the resolver and as the page placeholder. Tests: a real `XmlSerializer` round trip of `PluginConfiguration`, saved and reloaded three times, asserting the suffix list does not grow and a cleared list stays cleared; a 3.8.2.0-shaped XML loads with the same effective suffixes as before. This is the round-trip harness every later step reuses.
2. **Extract `Library/PathRoots`.** Move `IsUnderConfiguredRoot` into a static helper with `IsUnder(path, root)` and `TryRelative(path, root, out remainder)`; `VersionGroupingResolver` calls it. Tests: existing resolver tests unchanged; new tests for trailing separators, both separators, Windows case-insensitivity, a root that is a prefix of a sibling name (`/media/tv` must not match `/media/tv2/x`), longest-prefix selection.
3. **Add `QualityGateService.IsCapGap` and use it in `CapPlaybackInfo`.** Tests: truth table (no sources, all unknown, one unknown plus one over, one within, equal to cap, cap 0) for both flag values; existing `FallbackTranscodeTests` unchanged; a test asserting `CapPlaybackInfo` and `IsCapGap` agree on the same height sets.
4. **Config model.** `EncodeTarget`, `EncodeFolderMapping`, the plugin-wide fields, and `EncodePriorityOptions.From(config)` with clamping and enum-string fallback. Tests: XML round trip with two targets, every list non-empty then cleared, three reloads; old XML loads with the feature off; clamping and unknown enum strings.
5. **`PriorityFileWriter`.** Compare, atomic write, no BOM, producer marker, foreign-file refusal, delete-if-ours, the cleanup pass, and the state file. Tests on a real temp directory: first byte is `{`; unchanged list leaves mtime unchanged; changed list rewrites; heartbeat after 24 h; a read-only directory yields `WriteFailed` and keeps the old file; a foreign file is refused; cleanup deletes only marked files and writes an empty list when delete fails.
6. **`CandidateCollector`.** The demand queries behind an interface, built on the Jellyfin services. Tests with mocks: `EnableResumable` is true and `NextUpDateCutoff` is set on every Next Up query; lookahead uses the presentation key and `MinParentAndIndexNumber`; season 0 skipped unless configured; per-user limits applied; the lookahead cache serves a second user without a second query; idle users skipped.
7. **`PriorityListBuilder`.** Audience, gap test at each user's cap, mapping, ordering, dedupe, cut, findings. Pure given collector output. Tests: audience per mode including deny-all users excluded and `Users` mode treating uncapped as capped at the output height; a 2160p-only item listed for a 1080p viewer's target but not a 480p target; ordering by tier, depth, users, recency, path; unmapped counting with five samples; `MaxEntries` cut; determinism (same input, same output).
8. **`EncodePriorityTask`.** The scheduled task wiring steps 4 to 7, the run budget, activity entries, and progress. Tests: the class joins the `PluginInstance` collection; feature off runs only cleanup; budget exceeded writes nothing; an exception in one target does not stop the others; an activity entry is written only on change.
9. **Triggers.** `EncodePriorityPostScanTask`, `EncodePriorityEvents` (playback debounce, config change, served queue), `Plugin.OnUninstalling`, and `AddHostedService` in `PluginServiceRegistrator`. Tests: `PluginServiceRegistratorTests` asserts the hosted service registration; the handler queues nothing when the feature or refresh is off or the user is uncapped; two starts within the debounce window queue one run; config change queues a run; uninstall removes owned files.
10. **Settings page.** The new section, normalisation in `loadConfig`, mapping in `collectFromDOM`, validation, the derived lines, the status panel on `ScheduledTasks` and activity entries. Tests: node tests for `mapToEncoderPath`, `resolveOutputPath`, `targetAudience`, `validateEncodeTargets`; a stored height not in the list is still offered; a deleted policy id survives a load-save cycle.
11. **Docs and release.** `docs/encode-priority.md` (setup per output mode, the verification recipe, the encoder-side requirements), new rows in `docs/configuration.md`, version bump in the csproj (three places), `build.yaml` changelog entry stating the feature is off by default, `meta.json`.
12. **Status and preview endpoint** (after the owner's yes). `EncodePriorityController` with the two routes, and the preview table plus 7-day summary on the page. Tests: elevation required; status returns the state file; preview queues a dry run and writes no file.
13. **Served tracking.** Fold the served queue into the state, the `ServedOverCap` finding, and the 7-day summary. Tests: a capped play on the within-cap version records `servedAt`; a play on the over-cap version raises `ServedOverCap`; the queue survives a run and is drained by it.

## 13. Deviations

What shipped differs from the sections above in these ways. Each is also in the body of the commit that made it.

- **Shared folders across targets** (step 4). Two targets may share a Jellyfin folder, because the 480p plus 720p example in section 4.4 needs exactly that and nothing breaks. Only a repeated or overlapping folder within one target is rejected, by the page.
- **Cleanup when the delete fails** (step 5). The empty list is written in place by truncating the existing file. An atomic temp-file write needs the same folder permission the delete lacked, so an in-place write is the only one that can succeed. It happens only when the file is not already empty, so a retry does not move the mtime. Normal list writes stay atomic.
- **Demand details the design left open** (step 6). The episode on screen feeds the lookahead from the episode after it, at the Next Up tier, and only when the Next Up signal is on. Next Up and favourite signals carry the viewer's `LastActivityDate` as their recency, because Jellyfin's Next Up returns no per-show played date without one more query per show. A favourite show with no Next Up episode starts at its first unplayed episode, found by presentation key.
- **A 2160p-only item wanted by a 1080p viewer** (step 7). The plan's test wording says it is not listed for a 480p target; sections 4.3 and 4.4 say it is kept for every target whose output is at most 1080. The code follows 4.3 and 4.4: listed for 480p, 720p and 1080p targets, not for 1440p.
- **Findings the design did not name**: `AudienceBelowOutput` (step 7), `OutputInvalid` and `OutputConflict` (step 8). They are in the table in section 7.2.
- **Covered** (step 8) means an item dropped off the list and one of its versions now has a known height at or below the tallest cap it was a gap for. An item that dropped off because nobody asks for it any more is not covered.
- **Stuck** (step 8) measures from the last time the list's paths changed, not the file's mtime: the 24-hour heartbeat rewrites the file, so its mtime is never two days old.
- **Status badge without the endpoint** (step 10). Activity entries are written only when a list or its findings change, so a run with no new entry reads as "OK, unchanged".
- **Release items of step 11** (version bump in the csproj, `build.yaml` changelog, `meta.json`) are left for the release, which is a separate step. Step 11 shipped the user guide, the configuration rows and a README link.
- **Preview** (step 12) builds even with the feature saved as off, so an admin can check lists before switching it on, and it never runs the cleanup pass. A trigger that arrives together with a preview request still gets its real run afterwards. The status response also carries item titles and Jellyfin's data folder.
- **The 7-day summary** moved from step 12 to step 13, because it needs served tracking.
- **Served tracking** (step 13). The last count of the summary is "played over the cap" instead of "still live-transcoding": the plugin cannot see a live transcode of an item that was never covered, and a covered item played above the cap is what it can prove. Heights of played versions are measured the way playback measures them, and an unknown height counts as within the cap. A play that names no version, names a version the item does not have, or happened before the item was covered is dropped. Served needs a play of the recorded copy itself; an over-cap play counts only when the item has a version with a known height within the player's cap. Plays wait in memory until the next run; a run that ends without saving the state puts them back, and a restart in between loses them. `ServedOverCap` stays on the target while the covered record is kept, 7 days.
