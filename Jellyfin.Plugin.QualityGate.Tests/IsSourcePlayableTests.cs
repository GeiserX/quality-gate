using Jellyfin.Plugin.QualityGate.Configuration;
using Jellyfin.Plugin.QualityGate.Services;

namespace Jellyfin.Plugin.QualityGate.Tests;

public class IsSourcePlayableTests
{
    private static QualityPolicy MakePolicy(
        List<string>? allowedPatterns = null,
        List<string>? blockedPatterns = null)
    {
        return new QualityPolicy
        {
            Id = "test",
            Name = "Test Policy",
            Enabled = true,
            AllowedFilenamePatterns = allowedPatterns ?? new List<string>(),
            BlockedFilenamePatterns = blockedPatterns ?? new List<string>(),
        };
    }

    [Fact]
    public void NullPath_NotPlayable()
    {
        var policy = MakePolicy();
        Assert.False(QualityGateService.IsSourcePlayable(policy, null));
    }

    [Fact]
    public void EmptyPath_NotPlayable()
    {
        var policy = MakePolicy();
        Assert.False(QualityGateService.IsSourcePlayable(policy, string.Empty));
    }

    [Fact]
    public void ExistingFile_NoPatterns_Playable()
    {
        var tmp = Path.GetTempFileName();
        try
        {
            var policy = MakePolicy();
            Assert.True(QualityGateService.IsSourcePlayable(policy, tmp));
        }
        finally
        {
            File.Delete(tmp);
        }
    }

    [Fact]
    public void NonExistentFile_NoPatterns_NotPlayable()
    {
        var path = Path.Combine(Path.GetTempPath(), "nonexistent_" + Guid.NewGuid() + ".mkv");
        var policy = MakePolicy();
        Assert.False(QualityGateService.IsSourcePlayable(policy, path));
    }

    [Fact]
    public void ExistingFile_MatchesAllowed_Playable()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "Movie (2021) - 720p.mkv");
        File.WriteAllBytes(tmp, Array.Empty<byte>());
        try
        {
            var policy = MakePolicy(allowedPatterns: new List<string> { "- 720p" });
            Assert.True(QualityGateService.IsSourcePlayable(policy, tmp));
        }
        finally
        {
            File.Delete(tmp);
        }
    }

    [Fact]
    public void ExistingFile_BlockedByPattern_NotPlayable()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "Movie (2021) - 2160p.mkv");
        File.WriteAllBytes(tmp, Array.Empty<byte>());
        try
        {
            var policy = MakePolicy(blockedPatterns: new List<string> { "- 2160p" });
            Assert.False(QualityGateService.IsSourcePlayable(policy, tmp));
        }
        finally
        {
            File.Delete(tmp);
        }
    }

    [Fact]
    public void NonExistentFile_EvenIfAllowed_NotPlayable()
    {
        // File doesn't exist on disk — dangling symlink scenario
        var path = Path.Combine(Path.GetTempPath(), "Movie (2021) - 720p_" + Guid.NewGuid() + ".mkv");
        var policy = MakePolicy(allowedPatterns: new List<string> { "- 720p" });
        Assert.False(QualityGateService.IsSourcePlayable(policy, path));
    }

    [Fact]
    public void DenyAllPolicy_ExistingFile_NotPlayable()
    {
        var tmp = Path.GetTempFileName();
        try
        {
            Assert.False(QualityGateService.IsSourcePlayable(QualityGateService.DenyAllPolicy, tmp));
        }
        finally
        {
            File.Delete(tmp);
        }
    }
}
