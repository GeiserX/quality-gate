using Jellyfin.Plugin.QualityGate.Configuration;
using Jellyfin.Plugin.QualityGate.Services;

namespace Jellyfin.Plugin.QualityGate.Tests;

public class IsPathAllowedTests
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
    public void NullPath_IsDenied()
    {
        var policy = MakePolicy();
        Assert.False(QualityGateService.IsPathAllowed(policy, null));
    }

    [Fact]
    public void EmptyPath_IsDenied()
    {
        var policy = MakePolicy();
        Assert.False(QualityGateService.IsPathAllowed(policy, string.Empty));
    }

    [Fact]
    public void NoPatterns_AllowsEverything()
    {
        var policy = MakePolicy();
        Assert.True(QualityGateService.IsPathAllowed(policy, "/media/Movie (2021) - 1080p.mkv"));
        Assert.True(QualityGateService.IsPathAllowed(policy, "/media/Movie (2021) - 720p.mkv"));
    }

    [Fact]
    public void AllowedPattern_MatchingFile_Allowed()
    {
        var policy = MakePolicy(allowedPatterns: new List<string> { "- 720p" });
        Assert.True(QualityGateService.IsPathAllowed(policy, "/media/Movie (2021) - 720p.mkv"));
    }

    [Fact]
    public void AllowedPattern_NonMatchingFile_Blocked()
    {
        var policy = MakePolicy(allowedPatterns: new List<string> { "- 720p" });
        Assert.False(QualityGateService.IsPathAllowed(policy, "/media/Movie (2021) - 2160p.mkv"));
    }

    [Fact]
    public void BlockedPattern_MatchingFile_Blocked()
    {
        var policy = MakePolicy(blockedPatterns: new List<string> { "- 2160p" });
        Assert.False(QualityGateService.IsPathAllowed(policy, "/media/Movie (2021) - 2160p.mkv"));
    }

    [Fact]
    public void BlockedPattern_NonMatchingFile_Allowed()
    {
        var policy = MakePolicy(blockedPatterns: new List<string> { "- 2160p" });
        Assert.True(QualityGateService.IsPathAllowed(policy, "/media/Movie (2021) - 1080p.mkv"));
    }

    [Fact]
    public void BlockedBeforeAllowed_BlockWins()
    {
        // A file matching both blocked and allowed should be blocked
        // (blocked is evaluated first)
        var policy = MakePolicy(
            allowedPatterns: new List<string> { "- 720p" },
            blockedPatterns: new List<string> { "- 720p" });
        Assert.False(QualityGateService.IsPathAllowed(policy, "/media/Movie (2021) - 720p.mkv"));
    }

    [Fact]
    public void MultipleAllowedPatterns_AnyMatch_Allowed()
    {
        var policy = MakePolicy(allowedPatterns: new List<string> { "- 720p", "- 1080p" });
        Assert.True(QualityGateService.IsPathAllowed(policy, "/media/Movie - 720p.mkv"));
        Assert.True(QualityGateService.IsPathAllowed(policy, "/media/Movie - 1080p.mkv"));
        Assert.False(QualityGateService.IsPathAllowed(policy, "/media/Movie - 2160p.mkv"));
    }

    [Fact]
    public void MultipleBlockedPatterns_AnyMatch_Blocked()
    {
        var policy = MakePolicy(blockedPatterns: new List<string> { "- 2160p", "- 4K" });
        Assert.False(QualityGateService.IsPathAllowed(policy, "/media/Movie - 2160p.mkv"));
        Assert.False(QualityGateService.IsPathAllowed(policy, "/media/Movie - 4K.mkv"));
        Assert.True(QualityGateService.IsPathAllowed(policy, "/media/Movie - 1080p.mkv"));
    }

    [Fact]
    public void PatternsAreCaseInsensitive()
    {
        var policy = MakePolicy(allowedPatterns: new List<string> { "- 720P" });
        Assert.True(QualityGateService.IsPathAllowed(policy, "/media/Movie - 720p.mkv"));
    }

    [Fact]
    public void BracketedLabels_MatchWithOptionalBracketPattern()
    {
        var policy = MakePolicy(allowedPatterns: new List<string> { @"\[?720p\]?" });
        Assert.True(QualityGateService.IsPathAllowed(policy, "/media/Movie - 720p.mkv"));
        Assert.True(QualityGateService.IsPathAllowed(policy, "/media/Movie - [720p].mkv"));
    }

    [Fact]
    public void InvalidRegex_BlockedPattern_FailsClosed()
    {
        // Invalid regex in blocked patterns should fail closed (block the file)
        var policy = MakePolicy(blockedPatterns: new List<string> { "[invalid" });
        Assert.False(QualityGateService.IsPathAllowed(policy, "/media/Movie.mkv"));
    }

    [Fact]
    public void InvalidRegex_AllowedPattern_DoesNotMatch()
    {
        // Invalid regex in allowed patterns should not match (file is blocked)
        var policy = MakePolicy(allowedPatterns: new List<string> { "[invalid" });
        Assert.False(QualityGateService.IsPathAllowed(policy, "/media/Movie.mkv"));
    }

    [Fact]
    public void DenyAllPolicy_BlocksEverything()
    {
        var policy = QualityGateService.DenyAllPolicy;
        Assert.False(QualityGateService.IsPathAllowed(policy, "/media/Movie - 720p.mkv"));
        Assert.False(QualityGateService.IsPathAllowed(policy, "/media/Movie.mkv"));
    }
}
