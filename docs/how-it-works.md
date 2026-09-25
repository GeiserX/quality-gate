# How it works

What QualityGate registers, which requests it inspects, and what it does to them. Read this if
you want to know whether the cap actually holds, or you are deciding how much to trust it.

## What is registered

`PluginServiceRegistrator` registers exactly two things:

- `ResolutionCapFilter`, a global MVC filter that enforces the resolution cap.
- `QualityGateIntroProvider`, an `IIntroProvider` that supplies per-policy intro videos.

`MediaSourceResultFilter` is in the assembly but deliberately not registered. Its
filename-pattern rewriting has not been revalidated against the Jellyfin 12 ABI, and the
resolution cap does not need it. Everything it used to read is listed in
[the fields that do not restrict playback](configuration.md#fields-that-do-not-restrict-playback).

The filter is added with `PostConfigure<MvcOptions>` rather than `Configure`. A plugin's
`RegisterServices` runs from `ApplicationHost.Init`, before the web host runs
`Startup.ConfigureServices` and its `AddJellyfinApi`. A `Configure` delegate registered there
would be queued ahead of Jellyfin's own MVC setup and dropped. `PostConfigure` runs after every
`IConfigureOptions<MvcOptions>` has been applied, so the filter is still on the collection once
MVC is built.

## The cap is measured, not inferred

The height comes from the item's video `MediaStream`. Never from the filename.

A filename is a label an operator controls. On a library whose lower-quality tree symlinks to
originals under their original names, the label is absent from the path the server stores, so
filename patterns cannot express "no more than 720p" at all. Measuring the stream is the only
approach that survives a rename, a re-encode or a symlink.

Media Jellyfin has never probed has no height. Those items are allowed and logged as a warning,
and negotiation still transcodes them at the cap.

## Two ways video reaches a client

The filter covers both, because covering only the polite one is worthless.

### Negotiation, `POST /Items/{id}/PlaybackInfo`

**Before model binding**, the filter adds a required `Height <= cap` video condition to the
`DeviceProfile` in the request body. Jellyfin's `StreamBuilder` turns a failed `Height`
condition into `TranscodeReason.VideoResolutionNotSupported`, which rules out direct play, and
maps the same `LessThanEqual` condition onto the transcode's own `MaxHeight`. An over-cap
source therefore comes back as a capped transcode instead of the original.

The body rewrite is skipped when the request carries no `DeviceProfile` at all. Inventing one is
worse than doing nothing, because a profile with no direct-play and no transcoding entries plays
nothing. For such a client the cap rests entirely on the response rewrite below and on the
delivery refusals.

**Before serialization**, the filter guarantees the response itself rather than trusting the
profile injection to have worked. This second phase is not gated on the path or the method, so
`GET /Items/{id}/PlaybackInfo` responses are capped too, even though the body rewrite only
applies to `POST`:

- If a source within the cap exists, over-cap sources are dropped and the smaller sibling is
  offered instead.
- If every source is over the cap, they are kept but marked transcode-only, so Jellyfin must
  transcode and the required `Height` condition holds that transcode to the cap. They are
  ordered cheapest first, because they all transcode down to the same picture and the smallest
  source costs the least CPU.

Unrestricted users get their sources reordered too, best first. Without that, a client shown
the 720p encode ahead of the original plays the encode, which is the opposite of what an
unrestricted user should get. The same order is applied to the item response, because that list
is what fills the version picker.

### Within-cap versions played as they are

A per-policy option, off by default, changes one thing about negotiation. It is
`KeepWithinCapVersionsDirect` in the config and **Play within-cap versions as they are** on the
policy card.

Without it, a client whose maximum streaming bitrate is below a within-cap file's bitrate gets
that file as a smaller transcode of itself. Jellyfin's `StreamBuilder` checks the bitrate before
it looks at codecs, and rules the file out of direct play for `ContainerBitrateExceedsLimit`
alone. A `VideoBitrate` or `AudioBitrate` codec condition does the same per stream, as
`VideoBitrateNotSupported` or `AudioBitrateNotSupported`.

With it on, the body rewrite also raises every bitrate ceiling the request sets to exactly what
the item's within-cap versions need: the `maxStreamingBitrate` query value, the body's
`MaxStreamingBitrate`, the device profile's, and any `VideoBitrate` or `AudioBitrate` codec
condition. A ceiling already high enough is left as it is, and none is lowered. The file then
direct plays unless a codec, container, audio or subtitle reason remains. When one does, it
still transcodes, at up to the file's own bitrate rather than the client's lower ceiling, because
`StreamBuilder` uses one ceiling for both decisions.

Only versions with a known height within the cap set the ceiling, and only versions the answer
will contain: the one named by `mediaSourceId`, or otherwise every version the user can see. An
over-cap version in the same answer is dropped by the response rewrite below, so the raised
ceiling never reaches it. A request naming an over-cap version, and an item with no within-cap
version, are negotiated exactly as before. If measuring the versions or raising a ceiling fails,
for example on a malformed condition in the client's profile, the error is logged, the ceilings
stay as far as they got, and the `Height` cap is still written.

The option has a small cost on each play start of a user under that policy: one item lookup, one
user lookup and one read of the item's media sources, streams included, before Jellyfin reads
the same sources again to build its answer. Policies without the option pay nothing.

The option cannot lift Jellyfin's remote client bitrate limit, set per user or for the whole
server. That limit is not part of the request. The server applies it after reading the request,
so a remote viewer still gets a within-cap file as a bitrate transcode when the file is above
the limit. Raise or clear the limit if within-cap files should reach remote viewers as they are.

Delivery has no matching refusal. A transcode URL carries its `TranscodeReasons`, so the filter
could recognise a bitrate-only HLS request for a within-cap file and refuse it. It does not.
Jellyfin negotiates exactly that URL whenever the remote limit applies, so a refusal would turn
a smaller picture into playback that fails. Such a transcode also never delivers more than the
cap, so a refusal would protect nothing.

A player's manual quality choice reaches the server as the same ceiling, so with the option on,
picking a lower quality no longer shrinks a within-cap file.

### Direct delivery

These routes hand back bytes without asking anything, so negotiation cannot gate them. They are
separate MVC actions a client can call directly, and the GET form of `PlaybackInfo` applies no
limits at all.

The filter refuses an over-cap item with `403` on:

| Route | Why it is covered |
|---|---|
| `/Videos/.../stream`, `/stream.{container}` | The direct video stream |
| `/Videos/.../universal` | Client-chosen delivery |
| `*.m3u8`, `/hls1/...` | HLS playlists and modern segments |
| `/Audio/.../stream` | The audio routes never check item type, so asking for a video item through them returns that video's original file |
| `/Items/{id}/File`, `/Items/{id}/Download` | Return the original outright, with no transcode parameter that could bring it down |

For `File` and `Download` the route id is the only identity that matters, because those actions
take no media source parameter at all. Measuring a caller-supplied `mediaSourceId` there would
let a capped user name a 480p sibling and be handed the 4K original. On every other delivery
route, all candidate items are measured and the tallest one decides, which holds the cap
whichever identifier the action eventually settles on.

A properly negotiated request carries `MaxHeight` at or below the cap and passes untouched.

### The legacy `params` blob

Jellyfin binds the query string first, then inside the action overwrites the bound values from
a semicolon-separated `params` blob, by position. Index 3 sets the static flag and index 13 sets
the max height. A gate that read only the named query parameters would be walked straight past
by `?params=;;;true`. The filter reads the blob.

## The one route that is not covered

```text
/Videos/{itemId}/hls/{playlistId}/{segmentId}.{container}
```

Its `itemId` is declared but never read. The file is located purely by `segmentId`, an MD5 of
the media path, user agent, device id and play session id. A request cannot be mapped back to
an item, so the filter has nothing to check.

This is an accepted, known bypass. In practice it can only return segments that already exist
in the transcode folder, and for a restricted user those are segments this filter already
capped. Closing it properly would mean reversing the segment hash or tracking play sessions,
which is a much larger change than the exposure warrants.

## API keys are not capped

The filter identifies the caller from the `Jellyfin-UserId` claim, falling back to
`ClaimTypes.NameIdentifier`. It never accepts a caller-supplied `userId` from the query or the
route, because on the routes it gates the caller does not get to say who they are.

Jellyfin's API-key authentication sets that claim to an empty GUID, so such a request matches no
assignment and takes no default. Left alone it is **unrestricted on every route**, as is an
anonymous one. That is right for a server-to-server integration and wrong once the key reaches
something a capped viewer drives.

Since 3.8.0.0 you can close that. **API key and anonymous requests** in the admin page names the
policy those requests are held to. It is unset by default, so upgrading changes nothing and no
existing integration starts transcoding without you asking for it.

It is deliberately separate from the default policy. A default policy is about people, and folding
API keys into it would silently cap every media-serving integration the moment someone set one.
For the same reason, an API-key policy that has been deleted or disabled leaves those requests
uncapped rather than denying them: the operator's intent is no longer knowable, and guessing would
take out an integration rather than a person.

## Fail open, except once

An unreadable body, an item the filter cannot resolve, or an unexpected exception is logged and
allowed. A defect in a plugin must not take playback away from everybody.

There is one exception. On a delivery request from a user the filter has already established is
capped, it fails **closed**. By that point the only question left is how tall the media is, so a
throw is a defect in this plugin rather than something the library said, and allowing the
request would hand over exactly the bytes the cap exists to withhold.

## What this means for your threat model

QualityGate shapes what the server delivers through its own API. It is not DRM. A user who can
read the filesystem, or who already holds a copy, is out of scope.

The more useful question is what happens when the plugin is not loaded. There is no
fail-safe outside the plugin: if it does not load, every user is unrestricted and Jellyfin
reports nothing unusual. If you are relying on the cap rather than on separate libraries, treat
"is QualityGate Active" as something to monitor, not something to assume. See
[troubleshooting](troubleshooting.md#the-plugin-vanished-after-an-update) for how it can vanish
without warning.
