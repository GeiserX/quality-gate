# Jellyfin Quality Gate Plugin

A Jellyfin plugin that restricts users to specific media versions based on configurable path-based policies.

## Use Case

This plugin is designed for scenarios where you have:
- **High-quality original files** stored in one location (e.g., `/media/`)
- **Lower-bitrate transcoded files** stored in another location (e.g., `/media-transcoded/`)

You want to allow some users full access to all versions, while restricting others to only the transcoded/lower-quality versions to save bandwidth or manage access tiers.

## Features

- **Path-based Policies**: Define policies that allow or block access based on file path prefixes
- **User Assignments**: Assign policies to specific users
- **Web Configuration**: Easy-to-use configuration page in Jellyfin admin
- **Works with Multi-Version**: Filters available media sources/versions per user

## Installation

1. Download the latest release from the [Releases](https://github.com/GeiserX/jellyfin-quality-gate/releases) page
2. Extract `Jellyfin.Plugin.QualityGate.dll` to your Jellyfin plugins folder:
   - Linux: `/var/lib/jellyfin/plugins/QualityGate/`
   - Docker: Mount a volume to `/config/plugins/QualityGate/`
3. Restart Jellyfin

## Configuration

1. Go to **Dashboard → Plugins → Quality Gate**
2. Create a policy:
   - Give it a name (e.g., "Transcoded Only")
   - Set **Allowed Path Prefixes** to paths users CAN access (e.g., `/media-transcoded/`)
   - Optionally set **Blocked Path Prefixes** to explicitly deny paths
3. Assign the policy to users in the **User Policy Assignments** section
4. Save configuration

### Example Configuration

**Policy: "Low Bandwidth Users"**
- Allowed Paths: `/media-transcoded/`
- Blocked Paths: `/media/`

This ensures assigned users can only play files from the transcoded library.

## Multi-Version Support

Jellyfin supports having multiple versions of the same media (e.g., 4K and 1080p). This plugin filters which versions are visible to each user based on their assigned policy.

To use with multi-version:
1. Store original files in one library path (e.g., `/media/Movies/`)
2. Store transcoded versions in another path (e.g., `/media-transcoded/Movies/`)
3. Add both paths to your Jellyfin library
4. Create a policy that only allows `/media-transcoded/`
5. Assign the policy to restricted users

## Building from Source

```bash
cd Jellyfin.Plugin.QualityGate
dotnet build -c Release
```

The compiled DLL will be in `bin/Release/net8.0/`.

## License

MIT License - See [LICENSE](LICENSE) file.

## Contributing

Issues and pull requests are welcome on [GitHub](https://github.com/GeiserX/jellyfin-quality-gate).

