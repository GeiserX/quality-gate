<p align="center"><img src="docs/images/banner.svg" alt="Quality Gate banner" width="900"/></p>

<h1 align="center">Quality Gate</h1>

<p align="center">

  <a href="https://github.com/GeiserX/quality-gate/releases"><img src="https://img.shields.io/github/v/release/GeiserX/quality-gate?style=flat-square&logo=github" alt="GitHub Release"></a>
  <a href="https://jellyfin.org"><img src="https://img.shields.io/badge/Jellyfin-10.11+-00a4dc?style=flat-square&logo=jellyfin" alt="Jellyfin Version"></a>
  <a href="https://dotnet.microsoft.com"><img src="https://img.shields.io/badge/.NET-9.0-512bd4?style=flat-square&logo=dotnet" alt=".NET"></a>
  <a href="LICENSE"><img src="https://img.shields.io/github/license/GeiserX/quality-gate?style=flat-square" alt="License"></a>
  <a href="https://github.com/GeiserX/quality-gate/actions"><img src="https://img.shields.io/github/actions/workflow/status/GeiserX/quality-gate/build.yml?style=flat-square&logo=github-actions&logoColor=white&label=CI" alt="CI"></a>
  <a href="https://github.com/GeiserX/quality-gate/actions/workflows/build.yml"><img src="https://img.shields.io/github/actions/workflow/status/GeiserX/quality-gate/build.yml?branch=main&style=flat-square&label=tests" alt="Tests"></a>

</p>

<p align="center"><strong>Intelligent media access control for Jellyfin</strong></p>

---

## Features

- **Path-Based Policies** -- Define granular access rules based on file path prefixes
- **Filename Regex Patterns** -- Match against filenames with regex for [Jellyfin multi-version](https://jellyfin.org/docs/general/server/media/movies/#multiple-versions) setups
- **Per-User Assignments** -- Assign different policies to different users
- **Web Configuration** -- Easy-to-use admin interface in Jellyfin dashboard
- **Multi-Version Support** -- Seamlessly filter available media versions per user
- **Detailed Logging** -- Full audit trail of access decisions

## Use Cases

This plugin is perfect for scenarios where you have:

| Scenario | Solution |
|----------|----------|
| **Bandwidth Management** | Restrict remote users to lower-bitrate versions |
| **Tiered Access** | Premium users get 4K, standard users get 1080p |
| **Device Optimization** | Mobile users automatically get mobile-optimized versions |
| **Storage Tiers** | Keep originals on slow storage, transcodes on fast storage |

### Example Setup

```text
/media/Movies/              ← High-quality originals (4K/Remux)
/media-transcoded/Movies/   ← Transcoded versions (1080p/720p)
```

Create a "Standard Access" policy allowing only `/media-transcoded/` and assign it to users who should be restricted.

## Installation

### Method 1: Plugin Repository (Recommended)

Add this repository to your Jellyfin instance for automatic updates:

1. Go to **Dashboard → Plugins → Repositories**
2. Click **Add** and enter:
   - **Name**: `Quality Gate`
   - **URL**: `https://geiserx.github.io/quality-gate/manifest.json`
3. Go to **Catalog** and install **Quality Gate**
4. Restart Jellyfin

### Method 2: Manual Installation

<details>
<summary><b>Docker</b></summary>

```bash
# Download the latest release (replace VERSION with actual version, e.g. 3.0.0.0)
VERSION="3.0.0.0"
curl -L -o QualityGate.zip \
  "https://github.com/GeiserX/quality-gate/releases/download/v${VERSION}/quality-gate_${VERSION}.zip"

# Extract to your plugins volume
unzip QualityGate.zip -d /path/to/jellyfin/plugins/QualityGate/

# Restart your container
docker restart jellyfin
```

Or add to your `docker-compose.yml`:
```yaml
volumes:
  - ./plugins/QualityGate:/config/plugins/QualityGate
```

</details>

<details>
<summary><b>Linux (Native)</b></summary>

```bash
# Download the latest release (replace VERSION with actual version, e.g. 3.0.0.0)
VERSION="3.0.0.0"
curl -L -o QualityGate.zip \
  "https://github.com/GeiserX/quality-gate/releases/download/v${VERSION}/quality-gate_${VERSION}.zip"

# Extract to plugins directory
sudo unzip QualityGate.zip -d /var/lib/jellyfin/plugins/QualityGate/

# Set permissions
sudo chown -R jellyfin:jellyfin /var/lib/jellyfin/plugins/QualityGate/

# Restart Jellyfin
sudo systemctl restart jellyfin
```

</details>

<details>
<summary><b>Windows</b></summary>

1. Download the [latest release](https://github.com/GeiserX/quality-gate/releases/latest)
2. Extract to `%LOCALAPPDATA%\jellyfin\plugins\QualityGate\`
3. Restart Jellyfin from Services or the tray icon

</details>

<details>
<summary><b>macOS</b></summary>

```bash
# Download the latest release (replace VERSION with actual version, e.g. 3.0.0.0)
VERSION="3.0.0.0"
curl -L -o QualityGate.zip \
  "https://github.com/GeiserX/quality-gate/releases/download/v${VERSION}/quality-gate_${VERSION}.zip"

# Extract to plugins directory
unzip QualityGate.zip -d ~/.local/share/jellyfin/plugins/QualityGate/

# Restart Jellyfin
```

</details>

## Configuration

Navigate to **Dashboard → Quality Gate** (or **Dashboard → Plugins → Quality Gate**) to configure the plugin.

### Step 1: Create Policies

Policies define which paths are allowed or blocked. Click **"Add Policy"** to create one.

| Field | Description |
|-------|-------------|
| **Policy Name** | A descriptive name (e.g., "720p Only", "No 4K") |
| **Allowed Path Prefixes** | Paths users CAN access. Each prefix gets its own row; use **Add Allowed Path** for more. |
| **Blocked Path Prefixes** | Paths that will be blocked. Each prefix gets its own row; use **Add Blocked Path** for more. |
| **Allowed Filename Patterns** | Regex patterns matched against the filename. Files must match at least one pattern. |
| **Blocked Filename Patterns** | Regex patterns matched against the filename. Matching files are always blocked. |
| **Custom Intro Video** | Optional intro video for users under this policy. **Note:** Jellyfin aggregates all intro providers — disable the built-in "Local Intros" plugin if you only want Quality Gate intros. |
| **Enabled** | Toggle policy on/off |

### Step 2: Set Default Policy

Choose a policy from the **Default Policy** dropdown. This applies to ALL users who don't have a specific override.

- Select **(No default — Full Access)** to allow unrestricted access by default
- Select a policy to restrict all users by default

### Step 3: Configure User Access

The **User Access** table shows all Jellyfin users and their current policy. For each user, select a policy from the dropdown:

- **Use Default** — inherits the default policy above
- **Full Access** — no restrictions
- Any named policy — applies that policy's path rules

The **Effective Access** column shows what each user actually gets after considering overrides and defaults. Click **Save** to apply changes.

If an override points to a deleted or disabled policy, the dropdown stays on an explicit **DENIED** option until you choose a replacement. This preserves the server-side fail-closed behavior instead of silently widening access.

### Policy Logic

The plugin evaluates in this order:

1. **Blocked Path Prefixes**: If file path starts with any blocked prefix -- **BLOCKED**
2. **Blocked Filename Patterns**: If filename matches any blocked regex -- **BLOCKED**
3. **Allowed Path Prefixes**: If defined and file doesn't match any -- **BLOCKED**
4. **Allowed Filename Patterns**: If defined and filename doesn't match any -- **BLOCKED**
5. Otherwise -- **ALLOWED**

Path prefixes and filename patterns can be used separately or together. When both are configured in the same policy they are evaluated cumulatively — a file must pass **all** applicable gates (blocked paths, blocked patterns, allowed paths, allowed patterns) to be allowed.

| Allowed Paths | Blocked Paths | File Path | Result |
|---------------|---------------|-----------|--------|
| `/transcodes/` | -- | `/transcodes/Movie.mkv` | Allowed |
| `/transcodes/` | -- | `/originals/Movie.mkv` | Blocked |
| (empty) | `/originals/4K/` | `/originals/1080p/Film.mkv` | Allowed |
| (empty) | `/originals/4K/` | `/originals/4K/Film.mkv` | Blocked |
| `/media/` | `/media/4K/` | `/media/Movies/Film.mkv` | Allowed |
| `/media/` | `/media/4K/` | `/media/4K/Film.mkv` | Blocked |

| Allowed Filename | Blocked Filename | Filename | Result |
|------------------|------------------|-----------|--------|
| `- 720p` | -- | `Movie (2021) - 720p.mkv` | Allowed |
| `- 720p` | -- | `Movie (2021) - 2160p.mkv` | Blocked |
| (empty) | `- 2160p\|- 4K` | `Movie (2021) - 1080p.mkv` | Allowed |
| (empty) | `- 2160p\|- 4K` | `Movie (2021) - 2160p.mkv` | Blocked |

> **Tip**: If no Allowed Paths/Patterns are set, all files are allowed except those explicitly blocked. Patterns are case-insensitive regex. Jellyfin also supports bracketed labels (e.g. `Movie (2021) - [1080p].mkv`), so account for brackets in your patterns if needed (e.g. `\[?1080p\]?`).

---

## Policy Examples

### Example 1: Restrict to 720p Transcodes Only

**Use case**: You have originals in `/mnt/originals/` and 720p transcodes in `/mnt/transcodes/`. Restrict some users to only see transcoded versions.

```text
Policy Name: 720p Only
Allowed Path Prefixes:
  /mnt/transcodes/
  /mnt/remotes/transcodes/

Blocked Path Prefixes:
  (leave empty)
```

### Example 2: Block 4K Content

**Use case**: Allow access to everything except 4K content stored in a specific folder.

```text
Policy Name: No 4K
Allowed Path Prefixes:
  (leave empty - allows all by default)

Blocked Path Prefixes:
  /media/4K/
  /media/UHD/
  /mnt/storage/4K/
```

### Example 3: Multi-Version Filename Patterns (Recommended)

**Use case**: You use Jellyfin's [multi-version naming](https://jellyfin.org/docs/general/server/media/movies/#multiple-versions) where all versions are in the same folder:

```text
movies/Movie (2021)/Movie (2021) - 2160p.mkv
movies/Movie (2021)/Movie (2021) - 1080p.mkv
movies/Movie (2021)/Movie (2021) - 720p.mkv
```

Since all files share the same directory, path prefixes can't distinguish them. Use filename patterns instead:

```text
Policy Name: Standard Quality (No 4K)
Blocked Filename Patterns:
  - 2160p
  - 4K

(Leave all other fields empty)
```

Or restrict to a specific resolution:

```text
Policy Name: 720p Only
Allowed Filename Patterns:
  - 720p

(Leave all other fields empty)
```

### Example 4: Multi-Version Path Setup

**Use case**: You have a Jellyfin library with multi-version support where originals and transcodes are in the same folder. Originals come from `/mnt/originals/` and transcodes come from `/mnt/transcodes/`.

```text
Policy Name: Standard Quality
Allowed Path Prefixes:
  /mnt/transcodes/

Blocked Path Prefixes:
  /mnt/originals/
```

Then set this as the **Default Policy** and add **Full Access** overrides for admin users.

### Example 5: Tiered Access

**Use case**: Premium users get full access, standard users get 1080p max.

1. Create policy **"Standard (1080p max)"**:
   ```text
   Blocked Path Prefixes:
     /media/4K/
     /media/2160p/
   ```

2. Create policy **"Premium (Full Access)"** or use the built-in Full Access

3. Set **Default Policy** to "Standard (1080p max)"

4. Add **User Overrides** for premium users → "Full Access"

---

## How It Works

1. **Result Filter**: The plugin uses an ASP.NET Core `IAsyncResultFilter` that intercepts API responses **before serialization**. This operates on C# objects directly, avoiding the response compression issues that broke the previous middleware approach.

2. **MediaSource Filtering**: When Jellyfin returns media sources/versions to the client, the filter removes blocked versions so they don't appear in the UI. This applies to both `PlaybackInfo` and item detail responses.

3. **Path & Filename Matching**: The plugin matches each media version's **full file path** against your policy path prefixes and/or its **filename** against your regex patterns. Symlinks are resolved and both the original and resolved filenames are checked. When both path prefixes and filename patterns are configured, a file must pass all applicable gates.

### Identifying Your Paths

> **Important**: Jellyfin resolves symlinks when indexing media. If your files are symlinks, the paths stored internally will be the **resolved target paths**, not the symlink paths. Your policies must use the resolved paths shown in Media Info -- not the mount point names you configured in your library.

To see what paths your files have:

1. Go to a movie/show in Jellyfin
2. Click the **⋮** menu → **Media Info**
3. Look at the **Path** field for each version -- **use these exact paths in your policies**

Common path patterns:
- Docker: `/media/Movies/Title (2024)/Title.mkv`
- NFS mounts: `/mnt/nfs/media/Movies/...`
- SMB remotes: `/mnt/remotes/server/media/...`
- Transcodes on separate server: `/mnt/user/ShareMedia/Movies/...`

### Library Setup

Both high-quality originals and lower-quality transcodes must be in the **same Jellyfin library** (with multiple path entries). Do not create separate libraries per quality tier -- Jellyfin needs all versions as MediaSources on the same merged item for the plugin to filter them.

## Building from Source

### Prerequisites

- [.NET 9.0 SDK](https://dotnet.microsoft.com/download/dotnet/9.0)
- Git

### Build

```bash
git clone https://github.com/GeiserX/quality-gate.git
cd quality-gate/Jellyfin.Plugin.QualityGate
dotnet build -c Release
```

The compiled plugin will be in `bin/Release/net9.0/`.

## Security

- This plugin handles access control — review your policies carefully
- Only administrators can configure policies
- See [SECURITY.md](SECURITY.md) for vulnerability reporting

## Contributing

Contributions are welcome! Please feel free to submit a Pull Request.

1. Fork the repository
2. Create your feature branch (`git checkout -b feature/AmazingFeature`)
3. Commit your changes (`git commit -m 'Add some AmazingFeature'`)
4. Push to the branch (`git push origin feature/AmazingFeature`)
5. Open a Pull Request

## Other Jellyfin Projects by GeiserX

- [smart-covers](https://github.com/GeiserX/smart-covers) — Cover extraction for books, audiobooks, comics, magazines, and music libraries with online fallback
- [whisper-subs](https://github.com/GeiserX/whisper-subs) — Automatically generates subtitles using local AI models powered by Whisper
- [jellyfin-encoder](https://github.com/GeiserX/jellyfin-encoder) — Automatic 720p HEVC/AV1 transcoding service
- [jellyfin-telegram-channel-sync](https://github.com/GeiserX/jellyfin-telegram-channel-sync) — Sync Jellyfin access with Telegram channel membership

## License

This project is licensed under the GPL-3.0 License — see the [LICENSE](LICENSE) file for details.

## Acknowledgments

- [Jellyfin](https://jellyfin.org) — The Free Software Media System
- The Jellyfin plugin development community

---

<div align="center">

**[Back to Top](#quality-gate)**

</div>