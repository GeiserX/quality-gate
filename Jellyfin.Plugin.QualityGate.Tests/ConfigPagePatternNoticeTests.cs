using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using Jellyfin.Plugin.QualityGate.Tests.Harness;

namespace Jellyfin.Plugin.QualityGate.Tests;

/// <summary>
/// The filename patterns have enforced nothing since 3.4.0.0, so the admin page must not present
/// them, or the fallback fields that went with them, as what restricts playback. The policy card
/// is rendered by the shipped script, so the notice is checked where an admin actually sees it.
/// </summary>
public sealed class ConfigPagePatternNoticeTests : IDisposable
{
    private readonly ConfigPageScript _page = new();

    public void Dispose() => _page.Dispose();

    private string PolicyCard() => JsonSerializer.Deserialize<string>(_page.Json(
        "page.buildPolicyCard({ Id: 'p1', Name: 'HD', MaxHeight: 720, AllowedFilenamePatterns: ['- 720p'], " +
        "BlockedFilenamePatterns: [], FallbackTranscode: true, FallbackMaxHeight: 720, FallbackMaxBitrateKbps: 4000 }, 0)"))!;

    [Fact]
    public void PolicyCard_CarriesTheNotice_NextToThePatterns()
    {
        var card = PolicyCard();

        Assert.Contains("These patterns do not restrict playback.", card, StringComparison.Ordinal);
        Assert.Contains("<strong>Maximum Resolution</strong> is the setting that restricts playback.", card, StringComparison.Ordinal);
        Assert.Contains("href=\"https://github.com/GeiserX/quality-gate/blob/main/docs/how-it-works.md\"", card, StringComparison.Ordinal);
        Assert.True(
            card.IndexOf("These patterns do not restrict playback.", StringComparison.Ordinal)
                < card.IndexOf("policy-fn-allowed", StringComparison.Ordinal),
            "the notice must sit above the pattern fields");
    }

    [Fact]
    public void PolicyCard_NoLongerDescribesTheFallbackAsATranscode()
    {
        var card = PolicyCard();

        Assert.DoesNotContain("Override transcode bitrate in kbps", card, StringComparison.Ordinal);
        Assert.DoesNotContain("transcode at the selected resolution instead of blocking", card, StringComparison.Ordinal);
        Assert.DoesNotContain(">Transcode to ", card, StringComparison.Ordinal);
        Assert.DoesNotContain("Transcode (no resolution cap)", card, StringComparison.Ordinal);
        Assert.Contains("Play the intro (stored as 720p)", card, StringComparison.Ordinal);
    }

    [Fact]
    public void Page_NoLongerClaimsThePatternsRestrictUsers()
    {
        var page = ReadShippedResource("configPage.html");

        Assert.DoesNotContain("based on filename regex patterns", page, StringComparison.Ordinal);
        Assert.DoesNotContain("Each policy defines which filename patterns are allowed or blocked.", page, StringComparison.Ordinal);
    }

    private static string ReadShippedResource(string fileName)
    {
        var assembly = typeof(Plugin).Assembly;
        var name = assembly.GetManifestResourceNames().Single(n => n.EndsWith(fileName, StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(name)!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
