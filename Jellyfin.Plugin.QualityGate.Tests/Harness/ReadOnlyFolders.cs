using System;

namespace Jellyfin.Plugin.QualityGate.Tests.Harness;

/// <summary>
/// Whether a folder with its write bit cleared really refuses a write here. It does not on
/// Windows, where the Unix mode is never applied, nor for root, which ignores it. A test that
/// needs such a write to fail returns early when it would succeed, rather than failing on the
/// machine instead of the code.
/// </summary>
public static class ReadOnlyFolders
{
    /// <summary>Gets a value indicating whether a read-only folder refuses writes.</summary>
    public static bool AreEnforced => !OperatingSystem.IsWindows() && !Environment.IsPrivilegedProcess;
}
