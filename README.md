<![CDATA[<div align="center">

# 🎬 Jellyfin Quality Gate

**Intelligent media access control for Jellyfin**

[![GitHub Release](https://img.shields.io/github/v/release/GeiserX/jellyfin-quality-gate?style=flat-square&logo=github)](https://github.com/GeiserX/jellyfin-quality-gate/releases)
[![Jellyfin Version](https://img.shields.io/badge/Jellyfin-10.10+-00a4dc?style=flat-square&logo=jellyfin)](https://jellyfin.org)
[![.NET](https://img.shields.io/badge/.NET-9.0-512bd4?style=flat-square&logo=dotnet)](https://dotnet.microsoft.com)
[![License](https://img.shields.io/github/license/GeiserX/jellyfin-quality-gate?style=flat-square)](LICENSE)
[![CI](https://img.shields.io/github/actions/workflow/status/GeiserX/jellyfin-quality-gate/build.yml?style=flat-square&logo=github-actions&logoColor=white&label=CI)](https://github.com/GeiserX/jellyfin-quality-gate/actions)

*Restrict users to specific media versions based on configurable path-based policies*

[Installation](#-installation) • [Configuration](#-configuration) • [Use Cases](#-use-cases) • [Contributing](#-contributing)

</div>

---

## ✨ Features

- **🎯 Path-Based Policies** — Define granular access rules based on file path prefixes
- **👥 Per-User Assignments** — Assign different policies to different users
- **🖥️ Web Configuration** — Easy-to-use admin interface in Jellyfin dashboard
- **🎞️ Multi-Version Support** — Seamlessly filter available media versions per user
- **⚡ Real-Time Enforcement** — Policies are enforced at playback time
- **📝 Detailed Logging** — Full audit trail of access decisions

## 🎯 Use Cases

This plugin is perfect for scenarios where you have:

| Scenario | Solution |
|----------|----------|
| **Bandwidth Management** | Restrict remote users to lower-bitrate versions |
| **Tiered Access** | Premium users get 4K, standard users get 1080p |
| **Device Optimization** | Mobile users automatically get mobile-optimized versions |
| **Storage Tiers** | Keep originals on slow storage, transcodes on fast storage |

### Example Setup

```
/media/Movies/              ← High-quality originals (4K/Remux)
/media-transcoded/Movies/   ← Transcoded versions (1080p/720p)
```

Create a "Standard Access" policy allowing only `/media-transcoded/` and assign it to users who should be restricted.

## 📦 Installation

### Method 1: Plugin Repository (Recommended)

Add this repository to your Jellyfin instance for automatic updates:

1. Go to **Dashboard → Plugins → Repositories**
2. Click **Add** and enter:
   - **Name**: `Quality Gate`
   - **URL**: `https://raw.githubusercontent.com/GeiserX/jellyfin-quality-gate/main/manifest.json`
3. Go to **Catalog** and install **Quality Gate**
4. Restart Jellyfin

### Method 2: Manual Installation

<details>
<summary><b>🐳 Docker</b></summary>

```bash
# Download the latest release
curl -L -o QualityGate.zip \
  https://github.com/GeiserX/jellyfin-quality-gate/releases/latest/download/quality-gate.zip

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
<summary><b>🐧 Linux (Native)</b></summary>

```bash
# Download the latest release
curl -L -o QualityGate.zip \
  https://github.com/GeiserX/jellyfin-quality-gate/releases/latest/download/quality-gate.zip

# Extract to plugins directory
sudo unzip QualityGate.zip -d /var/lib/jellyfin/plugins/QualityGate/

# Set permissions
sudo chown -R jellyfin:jellyfin /var/lib/jellyfin/plugins/QualityGate/

# Restart Jellyfin
sudo systemctl restart jellyfin
```

</details>

<details>
<summary><b>🪟 Windows</b></summary>

1. Download the [latest release](https://github.com/GeiserX/jellyfin-quality-gate/releases/latest)
2. Extract to `%LOCALAPPDATA%\jellyfin\plugins\QualityGate\`
3. Restart Jellyfin from Services or the tray icon

</details>

<details>
<summary><b>🍎 macOS</b></summary>

```bash
# Download the latest release
curl -L -o QualityGate.zip \
  https://github.com/GeiserX/jellyfin-quality-gate/releases/latest/download/quality-gate.zip

# Extract to plugins directory
unzip QualityGate.zip -d ~/.local/share/jellyfin/plugins/QualityGate/

# Restart Jellyfin
```

</details>

## ⚙️ Configuration

Navigate to **Dashboard → Plugins → Quality Gate** to configure the plugin.

### Step 1: Create Policies

Policies define which paths are allowed or blocked. Click **"+ Add Policy"** to create one.

| Field | Description |
|-------|-------------|
| **Policy Name** | A descriptive name (e.g., "720p Only", "No 4K") |
| **Allowed Path Prefixes** | Paths users CAN access. One per line. |
| **Blocked Path Prefixes** | Paths that will be blocked. One per line. |
| **Blocked Message** | Custom message shown when playback is blocked |
| **Enabled** | Toggle policy on/off |

### Step 2: Set Default Policy

Choose a policy from the **Default Policy** dropdown. This applies to ALL users who don't have a specific override.

- Select **(No default - Full Access)** to allow unrestricted access by default
- Select a policy to restrict all users by default

### Step 3: Add User Overrides

Override the default for specific users:

1. Select a **user** from the dropdown
2. Select a **policy** (or "Full Access" for no restrictions)
3. Click **"Add Override"**

### Policy Logic

The plugin evaluates paths in this order:

1. **Blocked Paths**: If file path starts with any blocked prefix → ❌ **BLOCKED**
2. **Allowed Paths**: If allowed paths are defined and file doesn't match any → ❌ **BLOCKED**
3. Otherwise → ✅ **ALLOWED**

| Allowed Paths | Blocked Paths | File Path | Result |
|---------------|---------------|-----------|--------|
| `/transcodes/` | — | `/transcodes/Movie.mkv` | ✅ Allowed |
| `/transcodes/` | — | `/originals/Movie.mkv` | ❌ Blocked |
| (empty) | `/originals/4K/` | `/originals/1080p/Film.mkv` | ✅ Allowed |
| (empty) | `/originals/4K/` | `/originals/4K/Film.mkv` | ❌ Blocked |
| `/media/` | `/media/4K/` | `/media/Movies/Film.mkv` | ✅ Allowed |
| `/media/` | `/media/4K/` | `/media/4K/Film.mkv` | ❌ Blocked |

> **Tip**: If no Allowed Paths are set, all paths are allowed except those explicitly blocked.

---

## 📚 Policy Examples

### Example 1: Restrict to 720p Transcodes Only

**Use case**: You have originals in `/mnt/originals/` and 720p transcodes in `/mnt/transcodes/`. Restrict some users to only see transcoded versions.

```
Policy Name: 720p Only
Allowed Path Prefixes:
  /mnt/transcodes/
  /mnt/remotes/transcodes/

Blocked Path Prefixes:
  (leave empty)
```

### Example 2: Block 4K Content

**Use case**: Allow access to everything except 4K content stored in a specific folder.

```
Policy Name: No 4K
Allowed Path Prefixes:
  (leave empty - allows all by default)

Blocked Path Prefixes:
  /media/4K/
  /media/UHD/
  /mnt/storage/4K/
```

### Example 3: Multi-Version Setup (Recommended)

**Use case**: You have a Jellyfin library with multi-version support where originals and transcodes are in the same folder. Originals come from `/mnt/originals/` and transcodes come from `/mnt/transcodes/`.

```
Policy Name: Standard Quality
Allowed Path Prefixes:
  /mnt/transcodes/

Blocked Path Prefixes:
  /mnt/originals/

Blocked Message Header: Quality Restricted
Blocked Message Text: Please select the 720p version to play this content.
```

Then set this as the **Default Policy** and add **Full Access** overrides for admin users.

### Example 4: Tiered Access

**Use case**: Premium users get full access, standard users get 1080p max.

1. Create policy **"Standard (1080p max)"**:
   ```
   Blocked Path Prefixes:
     /media/4K/
     /media/2160p/
   ```

2. Create policy **"Premium (Full Access)"** or use the built-in Full Access

3. Set **Default Policy** to "Standard (1080p max)"

4. Add **User Overrides** for premium users → "Full Access"

---

## 🔄 How It Works

1. **Middleware Filtering**: When Jellyfin returns media sources/versions to the client, the plugin filters out blocked versions so they don't appear in the UI.

2. **Playback Interception**: If a user somehow attempts to play a blocked version, the plugin intercepts the playback and stops it with a custom message.

3. **Path Matching**: The plugin matches the **full file path** of each media version against your policy prefixes.

### Identifying Your Paths

To see what paths your files have:

1. Go to a movie/show in Jellyfin
2. Click the **⋮** menu → **Media Info**
3. Look at the **Path** field for each version

Common path patterns:
- Docker: `/media/Movies/Title (2024)/Title.mkv`
- NFS mounts: `/mnt/nfs/media/Movies/...`
- Remote transcodes: `/mnt/remotes/server/transcodes/...`

## 🏗️ Building from Source

### Prerequisites

- [.NET 9.0 SDK](https://dotnet.microsoft.com/download/dotnet/9.0)
- Git

### Build

```bash
git clone https://github.com/GeiserX/jellyfin-quality-gate.git
cd jellyfin-quality-gate/Jellyfin.Plugin.QualityGate
dotnet build -c Release
```

The compiled plugin will be in `bin/Release/net9.0/`.

### Development

```bash
# Run with hot reload (for development)
dotnet watch build -c Debug

# Run tests
dotnet test
```

## 🔒 Security

- This plugin handles access control — review your policies carefully
- Only administrators can configure policies
- See [SECURITY.md](SECURITY.md) for vulnerability reporting

## 🤝 Contributing

Contributions are welcome! Please feel free to submit a Pull Request.

1. Fork the repository
2. Create your feature branch (`git checkout -b feature/AmazingFeature`)
3. Commit your changes (`git commit -m 'Add some AmazingFeature'`)
4. Push to the branch (`git push origin feature/AmazingFeature`)
5. Open a Pull Request

## 📜 License

This project is licensed under the GPL-3.0 License — see the [LICENSE](LICENSE) file for details.

## 🙏 Acknowledgments

- [Jellyfin](https://jellyfin.org) — The Free Software Media System
- The Jellyfin plugin development community

---

<div align="center">

**[⬆ Back to Top](#-jellyfin-quality-gate)**

Made with ❤️ for the Jellyfin community

</div>
]]>