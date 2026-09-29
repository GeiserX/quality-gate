<p align="center"><img src="docs/images/banner.svg" alt="Quality Gate" width="900"/></p>

<h1 align="center">Quality Gate</h1>

<p align="center">
  <a href="https://github.com/GeiserX/quality-gate/releases"><img src="https://img.shields.io/github/v/release/GeiserX/quality-gate?style=flat-square&logo=github" alt="GitHub Release"></a>
  <a href="https://github.com/GeiserX/quality-gate/actions/workflows/build.yml"><img src="https://img.shields.io/github/actions/workflow/status/GeiserX/quality-gate/build.yml?style=flat-square&logo=github-actions&logoColor=white&label=CI" alt="CI"></a>
  <a href="LICENSE"><img src="https://img.shields.io/github/license/GeiserX/quality-gate?style=flat-square" alt="License"></a>
  <a href="https://jellyfin.org"><img src="https://img.shields.io/badge/Jellyfin-12.0+-00a4dc?style=flat-square&logo=jellyfin" alt="Jellyfin Version"></a>
  <a href="https://codecov.io/gh/GeiserX/quality-gate"><img src="https://codecov.io/gh/GeiserX/quality-gate/graph/badge.svg" alt="codecov"></a>
</p>

<p align="center"><strong>Cap the resolution a Jellyfin user may be served</strong></p>

Quality Gate is a Jellyfin plugin that caps how tall a video a user can be served. The cap is
checked against the media's real height, read from its video stream, so it survives a rename, a
re-encode or a symlink. Media above the cap is served as a capped transcode or as a
lower-resolution version of the same item, and the original file is refused.

**`Maximum Resolution` is the only setting that restricts playback.** Since 3.4.0.0 the filename
patterns, fallback-transcode and blocked-message fields enforce nothing;
[Configuration](docs/configuration.md#fields-that-do-not-restrict-playback) lists them exactly.

## Features

- **A measured resolution cap.** 480p, 720p, 1080p, 1440p or 4K, checked against the item's video stream rather than its name.
- **Both delivery paths.** It shapes playback negotiation, and refuses the direct stream, HLS, universal and original-file routes; one legacy HLS segment route is a documented exception.
- **Per-user policies.** Assign policies individually, set a default, or mark a user explicitly unrestricted.
- **Version grouping.** Optional, movies libraries only: groups `Film - 720p.mp4` with `Film.mkv` wherever the two sit, rebuilt on every scan.
- **Per-policy intro videos.** Optional. A different pre-roll for restricted users.
- **Logging you can debug from.** Every decision names the cap, the user and the policy.
- **Encode priority** for [quality-gate-encoder](https://github.com/GeiserX/quality-gate-encoder), so the copies capped viewers are about to watch get made first.

## Quick start

Needs Jellyfin 12. Add this repository under **Dashboard, Plugins, Repositories**, install **QualityGate** from the catalogue, restart, and confirm it shows **Active**:

```text
https://geiserx.github.io/quality-gate/manifest.json
```

Then add a policy with **Maximum Resolution** `720p`, assign a user, and prove the cap holds: [Getting started](docs/getting-started.md) has the steps and the `curl` check. It is not DRM, and if the plugin fails to load every user is unrestricted, so monitor that it stays **Active**.

## Documentation

| Guide | What it covers |
|---|---|
| [Getting started](docs/getting-started.md) | Install (repository or manual), cap one user at 720p, prove the cap holds, upgrade |
| [Configuration](docs/configuration.md) | Every setting, how a user's policy is chosen, **and the fields that do not restrict playback** |
| [How it works](docs/how-it-works.md) | The routes covered, the one that is not, what to trust, and security |
| [One library, two qualities](docs/one-library.md) | Keeping a smaller encode beside each original, and the companion encoder |
| [Encode priority](docs/encode-priority.md) | Having the encoder make the copies capped viewers are about to watch first |
| [Troubleshooting](docs/troubleshooting.md) | When it is not behaving |
| [Development](docs/development.md) | Building from source, tests, releases |

Pull requests are welcome. CI has to be green and the patch covered; [building from source](docs/development.md#building-from-source) has the build and test commands.

## Related projects

- [smart-covers](https://github.com/GeiserX/smart-covers) provides cover extraction for books, audiobooks, comics, magazines and music libraries, with online fallback
- [whisper-subs](https://github.com/GeiserX/whisper-subs) generates subtitles locally using Whisper
- [quality-gate-encoder](https://github.com/GeiserX/quality-gate-encoder) (formerly jellyfin-encoder) does automatic 720p HEVC, H.264 or AV1 transcoding, with optional symlinks for multi-version support
- [jellyfin-telegram-channel-sync](https://github.com/GeiserX/jellyfin-telegram-channel-sync) syncs Jellyfin access with Telegram channel membership

## License

[GPL-3.0-or-later](LICENSE). Thanks to [Jellyfin](https://jellyfin.org) and its plugin community.
