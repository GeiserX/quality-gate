# Configuration

Every setting QualityGate stores, what it does, and which ones do not restrict playback.

Configuration lives in `config/plugins/configurations/Jellyfin.Plugin.QualityGate.xml` on the
server, outside the plugin folder. That matters: it survives the plugin being removed,
reinstalled or purged. An unloaded plugin returns an empty policy list from the API, which
looks like data loss and is not.

## Fields that do not restrict playback

Read this first. It is the single thing most likely to waste your afternoon.

QualityGate started as a filename-pattern filter. In 3.4.0.0 the port to Jellyfin 12 stopped
registering `MediaSourceResultFilter`, the component that read those patterns. The class is
still in the assembly and the fields are still in the config page, but nothing calls them any
more.

| Field | Status |
|---|---|
| `AllowedFilenamePatterns` | Does not restrict playback |
| `BlockedFilenamePatterns` | Does not restrict playback |
| `FallbackTranscode` | Does not change how media is delivered |
| `FallbackMaxHeight` | No effect at all |
| `FallbackMaxBitrateKbps` | No effect at all |
| `BlockedMessageHeader` | No effect at all |
| `BlockedMessageText` | No effect at all |
| `BlockedMessageTimeoutMs` | No effect at all |

The filename patterns and `FallbackTranscode` are not entirely unread. The intro provider
still consults them to decide whether to skip an intro video, on the theory that playing an
intro before a refusal is a poor experience. They influence intros and nothing else.

So a policy that blocks `- 2160p` by filename pattern and sets no maximum resolution restricts
nobody. If you are migrating from 3.3.x or earlier, translate your patterns into a
**Maximum Resolution** value.

## Fields that work

### Plugin-wide

| Field | Type | Default | Effect |
|---|---|---|---|
| `Policies` | list | empty | The policies you define |
| `UserPolicies` | list | empty | Which user gets which policy |
| `DefaultPolicyId` | string | empty | Policy for users with no explicit assignment. Empty means unrestricted |
| `DefaultIntroVideoPath` | string | empty | Intro played when a user's policy names none |
| `ApiKeyPolicyId` | string | empty | Policy applied to API-key and anonymous requests, which carry no user. Empty leaves them uncapped |
| `EnableVersionGrouping` | bool | `false` | Group an encoded copy with its original. See [one library, two qualities](one-library.md) |
| `VersionGroupingRoots` | list | empty | Library paths where grouping applies. Empty means every movie library |
| `VersionGroupingSuffixes` | list | empty | Suffixes that mark a file as an encoded copy. Empty means `" - 720p"` |

### Per policy

| Field | Type | Default | Effect |
|---|---|---|---|
| `Id` | string | new GUID | Identifier referenced by assignments |
| `Name` | string | empty | Shown in the admin page and written to the server log |
| `Description` | string | empty | Your own note |
| `MaxHeight` | int | `0` | **The cap.** Maximum video height in pixels. `0` disables enforcement |
| `Enabled` | bool | `true` | A disabled policy does not resolve. See the warning below |
| `IntroVideoPath` | string | empty | Intro for users under this policy |

`MaxHeight` is measured against the item's actual video stream height, never its filename.
Rename a file and the cap is unchanged. That is deliberate: on a library whose lower-quality
tree symlinks to originals under their original names, the filename carries no quality
information at all.

### Per user

| Field | Effect |
|---|---|
| `UserId` | The Jellyfin user |
| `Username` | Display only |
| `PolicyId` | A policy `Id`, or `__FULL_ACCESS__` for explicitly unrestricted, or empty to fall through to the default |

## How a user's policy is chosen

A request that resolves to no user at all, which is what an API key and an unauthenticated
request both produce, does not enter this table. It takes `ApiKeyPolicyId`, or no policy when that
is unset. See [how it works](how-it-works.md#api-keys-are-not-capped).

```text
explicit assignment for this user?
├── __FULL_ACCESS__      -> unrestricted
├── a policy id           -> that policy, if it exists AND is enabled
│                            otherwise the deny-all sentinel (see below)
└── empty                 -> fall through
no assignment?
├── DefaultPolicyId set   -> that policy, if it exists AND is enabled
│                            otherwise the deny-all sentinel
└── not set               -> unrestricted
```

### A gap worth knowing about

When an assignment points at a policy that was deleted, disabled or mistyped, the code returns
an internal deny-all sentinel. The intent is fail-closed, so that an admin mistake cannot widen
access.

That sentinel no longer denies anything. It carries no maximum resolution, and it expresses its
restriction purely through the filename patterns that stopped being enforced in 3.4.0.0. The
resolution cap looks at the sentinel, sees no height, and treats the user as unrestricted.

The practical consequence: **disabling or deleting a policy that users are assigned to grants
those users full access rather than removing it.** If you want to take access away, point the
users at a policy with a low `MaxHeight`. Do not rely on deleting the policy they are on.

## Checking the live configuration

The admin page is not the only view, and it is not the one to trust when you are debugging.
Ask the server:

```bash
curl -s -H 'Authorization: MediaBrowser Token="<admin-token>"' \
  'https://your-server/Plugins/9cab70ca0af34d3aadab6a0df2496a33/Configuration' \
  | python3 -m json.tool
```

Jellyfin 12 accepts only that header form. `X-Emby-Token` and `?api_key=` both return 401,
which from outside looks the same as the plugin being absent.

Two things to look at. `Policies` should contain your policy with the `MaxHeight` you expect,
and `Enabled` true. `UserPolicies` should have an entry per capped user whose `PolicyId`
matches a policy that is present in that same response. An assignment pointing at an id that is
not in `Policies` is the gap described above.
