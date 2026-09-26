# Encode priority

Have your encoder make the copies capped viewers are about to watch first.

## What it does

A capped viewer who opens an item with no version within their cap gets a live transcode. An
encoder such as [jellyfin-encoder](https://github.com/GeiserX/jellyfin-encoder) removes those
transcodes by making a smaller copy of each file, but it works through the library in folder
order. On a large library the next episode of the show someone is following can sit thousands
of files down its queue.

With encode priority on, Quality Gate writes a list of the files capped viewers will reach
next and that still have no version within their cap. The encoder reads the list and encodes
those files before the rest.

- It is off by default. While it is off the plugin behaves exactly as before.
- Playback is not changed. The list only decides what the encoder does next.
- The library is never scanned. The list comes from what each capped viewer is playing, has
  in Continue Watching, has in Next Up and, if you turn it on, has marked as a favourite.
- A running encode is never interrupted. The encoder applies a new list the next time it
  picks a file.

## Requirements

- **jellyfin-encoder 1.5.4 or newer.** 1.5.4 is the first release that reads a priority file,
  set with `PRIORITY_FILE`. Older releases ignore the file and encode in folder order.
- Jellyfin 12, like the rest of the plugin.
- Your encoder's output height. jellyfin-encoder always produces 720p.

Any encoder that reads the same format works. The file is UTF-8 JSON with no byte order mark:

```json
{"generated":"2026-01-01T10:12:03Z","producer":"quality-gate","target":"Shows","paths":["Show A (2001)/Season 02/Show A (2001) S02E05.mkv","Show A (2001)/Season 02/Show A (2001) S02E06.mkv"]}
```

`paths` are relative to the encoder's source folder, separated by `/`, highest priority first.
Every entry is one file, never a folder. `producer` is how the plugin recognises its own files:
it never overwrites or deletes a file without it.

## Words used on the settings page

| Term | Meaning |
|---|---|
| **Encoder** | One encoder process. It reads one list and sees your media through one source folder. |
| **Folder mapping** | "Jellyfin sees this folder as X; inside the encoder's source folder it is Y." Y is usually empty, because the two are the same directory mounted at different paths. |
| **Output height** | The height the encoder produces. |
| **Gap** | An item a capped viewer gets as a live transcode: at least one version has a known height above their cap, and no version is within it. The same test playback uses. |
| **Covered** | A listed item that has since gained a version within the cap. It leaves the list. |
| **Unmapped** | A gap whose file is under none of the encoder's folders, and that no other enabled encoder writing its list will list: that encoder must count a viewer who asked for the item, its output height must fit the gap's cap, and one of its folders must hold the file. It is counted and reported, never guessed. A gap another encoder lists is that encoder's gap, so with one encoder per library neither flags the other's. |

Two things you never set, because they follow from settings that already exist:

- **Which viewers an encoder serves.** A copy at height H helps a viewer capped at C only when
  H is at most C. A 720p encoder serves viewers on 720p and 1080p policies and cannot help a
  viewer on a 480p policy. The encoder card says so.
- **Which libraries an encoder covers.** A file belongs to an encoder when it is under one of
  that encoder's folders.

## Setting it up

In **Dashboard, Plugins, Quality Gate**, under **Encode Priority**:

1. Tick **Prioritise encodes for capped viewers**.
2. Leave the signals as they are for now: playing right now, Continue Watching, and Next Up
   three episodes ahead, counting activity from the last 30 days.
3. Click **Add encoder** and give it a name, for example `Shows`.
4. Under **Folders**, pick the library folder the encoder works on, as Jellyfin sees it. The
   suggestions are your libraries' folders. A subfolder is allowed.
5. Check the example line under the folders. It shows a sample file as Jellyfin sees it and as
   it will be written in the list. The written form must be the file's path inside the
   encoder's `SOURCE_FOLDER`.
6. Set **Encoder output height** to what the encoder produces: 720 for jellyfin-encoder.
7. Choose **Where to write**, described in the next section, and follow the **Encoder setup**
   text under it.
8. Read the **Serves** line. It lists the policies whose viewers this encoder can help and how
   many viewers each has. **Cannot help** lists policies capped below the output height.
9. Click **Save**. A run starts straight away, and the Status block shows the result.

### Folder mappings

Most setups need one folder with an empty encoder path: Jellyfin sees `/media/tv`, the encoder
sees the same share as `/app/source`, and `/media/tv/Show A (2001)/Season 02/Show A (2001)
S02E05.mkv` is written as `Show A (2001)/Season 02/Show A (2001) S02E05.mkv`.

When one encoder covers two libraries, give each folder its place inside the encoder's source
folder. Open **Advanced** on the card to see the second column. With a `SOURCE_FOLDER` of
`/data` holding `movies/` and `tv/`, and Jellyfin seeing them as `/media/movies` and
`/media/tv`:

| Jellyfin folder | Inside the encoder's source folder |
|---|---|
| `/media/movies` | `movies` |
| `/media/tv` | `tv` |

`/media/movies/Film B (2002)/Film B (2002).mkv` is then written as
`movies/Film B (2002)/Film B (2002).mkv`.

When folders nest, the most specific one wins. The encoder path must be relative, use `/`, and
contain no `..`. Paths are written byte for byte, with no case folding.

If your library points at a tree of symlinks and the encoder reads the real files, add the
real folder and tick **Resolve symlinks before mapping** under Advanced. Each file is then
resolved to its final target before it is mapped.

### Where to write, per output mode

**Beside the media (source folder).** The default. The file is
`<the folder with an empty encoder path>/.encoder-priority.json`, which is where jellyfin-encoder
looks when `PRIORITY_FILE` is not set.

- Encoder side: nothing to set.
- Jellyfin must be able to write to that folder. Docker setups often mount media read-only.
  The run then reports `WriteFailed`, and the fix is the Data folder.
- This mode needs one folder with an empty encoder path. Without one, the page and the run
  both say to use Data folder or Custom.
- The file name starts with a dot, so neither the encoder nor Jellyfin's library scan picks
  it up as media.

**Jellyfin data folder.** For a read-only media mount. The file is
`<Jellyfin data folder>/quality-gate/encode-priority/<first 8 characters of the encoder id>.json`,
and the folder is created if missing. In the official and linuxserver images the data folder
is `/config/data`.

- Encoder side: mount that file or its folder into the encoder read-only, and set
  `PRIORITY_FILE` to its path inside the encoder container. For example, with Jellyfin's
  `/config` on the host at `/srv/jellyfin/config`:

  ```yaml
  services:
    encoder:
      volumes:
        - /srv/jellyfin/config/data/quality-gate/encode-priority:/priority:ro
      environment:
        PRIORITY_FILE: /priority/<file name shown on the card>
  ```

- The path does not change when the plugin is upgraded. It deliberately does not use the
  plugin's own folder, whose name carries the plugin version.
- A remote encoder can use this mode only if Jellyfin's data folder is shared to it.

**Custom file.** Any absolute path, as Jellyfin sees it.

- Encoder side: set `PRIORITY_FILE` to the same file as the encoder sees it.
- The folder must already exist; the plugin never creates folders outside its data folder.
- A file inside a library folder must have a name starting with a dot, so the library scan
  does not pick it up.

Two encoders may not write the same file. The page refuses to save that, and a run that finds
it anyway writes only the first and reports `OutputConflict` on the other.

### Two encoders at different heights

A 480p encoder and a 720p encoder over the same folder are two encoders with the same folder
and different output heights. An item that is a gap for a 720p viewer is listed for both. An
item only a 480p viewer wants is listed for the 480p encoder only, because a 720p copy would
still be over that viewer's cap.

## How the list is ordered

Everything from a higher line comes before anything from a lower one:

1. Playing right now.
2. Continue Watching.
3. Next Up. The next episode of every followed show comes before the second episode of any
   show, and so on up to the depth you set.
4. Favourites: favourite films, and the next unwatched episodes of favourite shows. Off by
   default. Each favourite show costs up to about 3 queries per viewer per run, so keep
   Favourites per viewer modest on a server with many viewers, or a run can time out.

Within a line, items more viewers want come first, then the most recent activity, then the
path, so the same inputs always give the same file. A viewer idle for longer than the activity
window is skipped. Each query runs as that viewer, so their library access and parental rules
apply.

For each listed item, every version above the encoder's output height is written, lowest
first. The encoder skips a file whose copy already exists, so the extra entries cost nothing.

The list stops at **Longest list written**, 300 entries by default, set under Advanced on the
card.

## When it runs

| Trigger | When |
|---|---|
| Schedule | Every hour and at startup. Edit under Dashboard, Scheduled Tasks, *Quality Gate: build encode priority lists*. |
| Playback | A capped viewer starts something. Needs **Refresh when a capped viewer starts playing**, on by default. The first start arms a timer of **minimum gap** minutes, 10 by default, and the run starts when it fires. Later starts inside that window add nothing. |
| Settings saved | Every save. |
| Library scan | After every scan, so a new copy takes its item off the list soon after it appears. |
| **Run now** | The button in the Refresh block. It uses the saved settings, so save first. |
| **Preview (dry run)** | Builds every enabled encoder's list and writes nothing, even while encode priority is saved as off. The result shows under Status. A real run it coincides with still happens. |

A run that goes over its budget writes nothing and keeps every previous list. The budget is
180 seconds by default, set under Advanced. So does a run that fails. The file is rewritten only when its list
changes, plus once a day with a fresh `generated` time.

## Turning it off

Untick the switch, disable an encoder, remove it, switch it to dry run or change where it
writes, then save. The next run, which the save starts, deletes every list the plugin wrote and
no longer claims. If it cannot delete one it writes an empty list in its place. Uninstalling
the plugin does the same.

The encoder keeps obeying the last list it read until the file changes or disappears. If
Jellyfin is down, or the plugin folder is deleted by hand, the last list stays in force. 1.5.4 has
no expiry for a list. If your encoder version has `PRIORITY_MAX_AGE_HOURS`, set it to `48`. The
plugin rewrites the file at least once a day, so a list older than two days means the plugin
stopped.

## Checking it works

The Status block on the page shows, per encoder, the last run and what started it, the counts,
the findings with their fixes, and the current list: rank, title, the path as the encoder sees
it, the height and why each entry is there. After **Preview (dry run)** it also shows the list
a run would write now. To follow one item end to end:

1. **Listed.** Open **Current list** under Status and pick one entry. To try a configuration
   first, click **Preview (dry run)**, or tick **Dry run** under the card's Advanced so that
   encoder's runs report and write no file.
2. **Picked up.** Watch the encoder's log for the line it writes when it reloads the list:

   ```text
   Priority list /app/source/.encoder-priority.json: 12 entries, 12 of 4803 pending files match; first: Show A (2001)/Season 02/Show A (2001) S02E05.mkv
   ```

   The number of matching files is the check that the folder mapping is right. Zero matches
   means the list's paths are not paths inside `SOURCE_FOLDER`: fix the mapping.
3. **Encoded.** Wait for that file's encode to finish.
4. **Covered.** After the next library scan, open the item in Jellyfin. It shows two versions,
   and the next run takes it off the list.
5. **Served.** Play it as a capped viewer. The dashboard's active sessions show direct play of
   the smaller version, not a transcode.

The plugin tracks the last three steps itself. A run that finds a listed item has gained a
version within the cap records it as covered. When a capped viewer then starts that item, the
plugin notes which version was played, and the next run records it as served if that version is
the new copy and it is within the viewer's cap. Each encoder's status shows the result:

```text
Last 7 days: 34 listed · 29 covered (median 3 h 10 m) · 21 served within cap · 0 played over the cap
```

"Played over the cap" should stay at 0. A covered item played on a version above the viewer's
cap, when the item has a version within it, means Quality Gate did not offer that version, and
the run reports `ServedOverCap`. A viewer capped below every version (a 480p viewer on an item
whose smallest copy is 720p) gets a transcode, which is correct and is not counted.
Plays are held in memory until the next run, so a restart in between loses them; the item still
counts as covered.

## Findings

| Code | Meaning and fix |
|---|---|
| `NoAudience` | No viewer benefits from this encoder's output. Check the output height against your policies' caps. |
| `AudienceBelowOutput` | Some viewers chosen under "These users" are capped below the output height. Their copies would still be over their cap. |
| `FolderMissing` | A folder does not exist on the Jellyfin host. Use the path Jellyfin sees inside its container, not the host path. |
| `FolderNotInLibrary` | A folder is under no library location, so no item can ever be under it. |
| `MostlyUnmapped` | Over a quarter of the watched gaps are under a folder this encoder does not cover. Add that folder, or turn on Resolve symlinks if it is a tree of links. |
| `UnknownHeights` | Watched items have never been probed. Run *Extract media info* or a library scan. |
| `OutputInvalid` | The output mode cannot produce a file, for example Beside the media with no folder at the encoder root. |
| `OutputConflict` | Another encoder already writes this file. |
| `ForeignFile` | A file the plugin did not write is at the output path. Delete it or choose another output. |
| `WriteFailed` | The file could not be written, often a read-only media mount. Switch to the Data folder. |
| `Stuck` | The same list has sat unchanged for two days and none of its items was covered. Check `PRIORITY_FILE`, `SOURCE_FOLDER` and the encoder's reload line above. |
| `TimedOut` | The run went over its budget and kept the previous list. Lower Next Up shows per viewer or depth. |
| `ServedOverCap` | A capped viewer played a covered item on a version above their cap, although a version within their cap exists. That is a Quality Gate bug; report it with the item ids the finding lists. It stays for 7 days after the item was covered. |

The page reads two admin-only routes the plugin adds. `GET /QualityGate/EncodePriority/Status`
returns what the scheduled task keeps in
`<Jellyfin data folder>/quality-gate/encode-priority/state.json`, with titles added.
`POST /QualityGate/EncodePriority/Preview` queues the preview. A script can read the status the
same way, with an administrator's token.

Every finding is also written to the Jellyfin log with the `QualityGate: ` prefix, and to the
activity log under the type `QualityGate.EncodePriority`, once per change.

## Settings reference

Every field, its default and its range are in [configuration](configuration.md#encode-priority).
