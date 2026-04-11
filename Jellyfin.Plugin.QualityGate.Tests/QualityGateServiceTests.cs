using Jellyfin.Plugin.QualityGate.Configuration;
using Jellyfin.Plugin.QualityGate.Services;
using Xunit;

namespace Jellyfin.Plugin.QualityGate.Tests;

public class QualityGateServiceTests
{
    private static QualityPolicy CreatePolicy(
        List<string>? allowed = null,
        List<string>? blocked = null,
        List<string>? allowedFilenames = null,
        List<string>? blockedFilenames = null)
    {
        return new QualityPolicy
        {
            Id = "test-policy",
            Name = "Test Policy",
            Enabled = true,
            AllowedPathPrefixes = allowed ?? new List<string>(),
            BlockedPathPrefixes = blocked ?? new List<string>(),
            AllowedFilenamePatterns = allowedFilenames ?? new List<string>(),
            BlockedFilenamePatterns = blockedFilenames ?? new List<string>(),
        };
    }

    // ---- IsPathAllowed: Path Prefix Tests ----

    [Fact]
    public void IsPathAllowed_NullPath_ReturnsFalse()
    {
        var policy = CreatePolicy(allowed: new List<string> { "/media/" });
        Assert.False(QualityGateService.IsPathAllowed(policy, null));
    }

    [Fact]
    public void IsPathAllowed_EmptyPath_ReturnsFalse()
    {
        var policy = CreatePolicy(allowed: new List<string> { "/media/" });
        Assert.False(QualityGateService.IsPathAllowed(policy, ""));
    }

    [Fact]
    public void IsPathAllowed_MatchesAllowedPrefix_ReturnsTrue()
    {
        var policy = CreatePolicy(allowed: new List<string> { "/media-transcoded/" });
        Assert.True(QualityGateService.IsPathAllowed(policy, "/media-transcoded/Movies/Film.mkv"));
    }

    [Fact]
    public void IsPathAllowed_DoesNotMatchAllowedPrefix_ReturnsFalse()
    {
        var policy = CreatePolicy(allowed: new List<string> { "/media-transcoded/" });
        Assert.False(QualityGateService.IsPathAllowed(policy, "/media/Movies/Film.mkv"));
    }

    [Fact]
    public void IsPathAllowed_BlockedPrefix_ReturnsFalse()
    {
        var policy = CreatePolicy(blocked: new List<string> { "/media/4K/" });
        Assert.False(QualityGateService.IsPathAllowed(policy, "/media/4K/Film.mkv"));
    }

    [Fact]
    public void IsPathAllowed_BlockedTakesPriority_ReturnsFalse()
    {
        var policy = CreatePolicy(
            allowed: new List<string> { "/media/" },
            blocked: new List<string> { "/media/4K/" });

        Assert.False(QualityGateService.IsPathAllowed(policy, "/media/4K/Film.mkv"));
        Assert.True(QualityGateService.IsPathAllowed(policy, "/media/Movies/Film.mkv"));
    }

    [Fact]
    public void IsPathAllowed_NoRules_AllowsEverything()
    {
        var policy = CreatePolicy();
        Assert.True(QualityGateService.IsPathAllowed(policy, "/any/path/file.mkv"));
    }

    [Fact]
    public void IsPathAllowed_PrefixBoundary_PreventsFalseMatch()
    {
        // /media should NOT match /media2/file.mkv
        var policy = CreatePolicy(allowed: new List<string> { "/media" });
        Assert.False(QualityGateService.IsPathAllowed(policy, "/media2/file.mkv"));
    }

    [Fact]
    public void IsPathAllowed_PrefixWithTrailingSeparator_MatchesSubdir()
    {
        var policy = CreatePolicy(allowed: new List<string> { "/media/" });
        Assert.True(QualityGateService.IsPathAllowed(policy, "/media/file.mkv"));
    }

    [Fact]
    public void IsPathAllowed_ExactPathMatch_Allowed()
    {
        var policy = CreatePolicy(allowed: new List<string> { "/media/file.mkv" });
        Assert.True(QualityGateService.IsPathAllowed(policy, "/media/file.mkv"));
    }

    // ---- IsPathAllowed: Filename Pattern Tests ----

    [Fact]
    public void IsPathAllowed_AllowedFilenamePattern_MatchesCorrectly()
    {
        var policy = CreatePolicy(allowedFilenames: new List<string> { "- 720p" });
        Assert.True(QualityGateService.IsPathAllowed(policy, "/media/Movie (2021) - 720p.mkv"));
        Assert.False(QualityGateService.IsPathAllowed(policy, "/media/Movie (2021) - 2160p.mkv"));
    }

    [Fact]
    public void IsPathAllowed_BlockedFilenamePattern_BlocksCorrectly()
    {
        var policy = CreatePolicy(blockedFilenames: new List<string> { "- 2160p", "- 4K" });
        Assert.False(QualityGateService.IsPathAllowed(policy, "/media/Movie (2021) - 2160p.mkv"));
        Assert.False(QualityGateService.IsPathAllowed(policy, "/media/Movie (2021) - 4K.mkv"));
        Assert.True(QualityGateService.IsPathAllowed(policy, "/media/Movie (2021) - 1080p.mkv"));
    }

    [Fact]
    public void IsPathAllowed_BlockedFilename_TakesPriorityOverAllowedFilename()
    {
        var policy = CreatePolicy(
            allowedFilenames: new List<string> { @"\d+p" },
            blockedFilenames: new List<string> { "2160p" });

        Assert.True(QualityGateService.IsPathAllowed(policy, "/media/Movie - 720p.mkv"));
        Assert.False(QualityGateService.IsPathAllowed(policy, "/media/Movie - 2160p.mkv"));
    }

    [Fact]
    public void IsPathAllowed_CombinedPathAndFilename_BothMustPass()
    {
        var policy = CreatePolicy(
            allowed: new List<string> { "/media/" },
            blockedFilenames: new List<string> { "- 4K" });

        Assert.True(QualityGateService.IsPathAllowed(policy, "/media/Movie - 1080p.mkv"));
        Assert.False(QualityGateService.IsPathAllowed(policy, "/media/Movie - 4K.mkv"));
        Assert.False(QualityGateService.IsPathAllowed(policy, "/other/Movie - 1080p.mkv"));
    }

    [Fact]
    public void IsPathAllowed_CaseInsensitive_FilenamePatterns()
    {
        var policy = CreatePolicy(blockedFilenames: new List<string> { "remux" });
        Assert.False(QualityGateService.IsPathAllowed(policy, "/media/Movie.REMUX.mkv"));
        Assert.False(QualityGateService.IsPathAllowed(policy, "/media/Movie.Remux.mkv"));
    }

    // ---- IsPathAllowed: Regex safety ----

    [Fact]
    public void IsPathAllowed_InvalidRegex_BlockedPattern_FailsClosed()
    {
        // Invalid regex in blocked patterns should fail-closed (block)
        var policy = CreatePolicy(blockedFilenames: new List<string> { "[invalid" });
        Assert.False(QualityGateService.IsPathAllowed(policy, "/media/Movie.mkv"));
    }

    [Fact]
    public void IsPathAllowed_InvalidRegex_AllowedPattern_FailsOpen()
    {
        // Invalid regex in allowed patterns should not match (fail-open per implementation)
        var policy = CreatePolicy(allowedFilenames: new List<string> { "[invalid" });
        Assert.False(QualityGateService.IsPathAllowed(policy, "/media/Movie.mkv"));
    }

    // ---- DenyAllPolicy ----

    [Fact]
    public void DenyAllPolicy_BlocksEverything()
    {
        var policy = QualityGateService.DenyAllPolicy;
        Assert.True(policy.Enabled);
        Assert.False(QualityGateService.IsPathAllowed(policy, "/media/Movie.mkv"));
        Assert.False(QualityGateService.IsPathAllowed(policy, "/any/path.mkv"));
    }

    // ---- ResolvePath ----

    [Fact]
    public void ResolvePath_RegularFile_ReturnsSamePath()
    {
        var path = "/some/nonexistent/path.mkv";
        var resolved = QualityGateService.ResolvePath(path);
        Assert.Equal(path, resolved);
    }

    // ---- Multiple allowed prefixes ----

    [Fact]
    public void IsPathAllowed_MultipleAllowedPrefixes_AnyOneSuffices()
    {
        var policy = CreatePolicy(allowed: new List<string>
        {
            "/media-transcoded/",
            "/mnt/remotes/transcodes/"
        });

        Assert.True(QualityGateService.IsPathAllowed(policy, "/media-transcoded/Movie.mkv"));
        Assert.True(QualityGateService.IsPathAllowed(policy, "/mnt/remotes/transcodes/Movie.mkv"));
        Assert.False(QualityGateService.IsPathAllowed(policy, "/media/Movie.mkv"));
    }

    [Fact]
    public void IsPathAllowed_MultipleBlockedPrefixes_AnyOneBlocks()
    {
        var policy = CreatePolicy(blocked: new List<string>
        {
            "/media/4K/",
            "/media/UHD/"
        });

        Assert.False(QualityGateService.IsPathAllowed(policy, "/media/4K/Movie.mkv"));
        Assert.False(QualityGateService.IsPathAllowed(policy, "/media/UHD/Movie.mkv"));
        Assert.True(QualityGateService.IsPathAllowed(policy, "/media/1080p/Movie.mkv"));
    }
}
