using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.QualityGate.Tests;

/// <summary>
/// Covers the resolution dropdowns on the admin page.
///
/// The page is shipped as an embedded resource and is what a browser actually runs, so these
/// tests run it too: the resource is pulled out of the built assembly, written next to a tiny
/// driver, and executed with node. Asserting on the source text instead would prove only that
/// some words are present in a file.
///
/// The behaviour under test is narrow and load-bearing: MaxHeight is a plain int on the
/// server, so a policy can hold a height the dropdown has no preset for. If the dropdown
/// omitted it the browser would fall back to its first option — "No limit" — and the next
/// save would write 0 back, lifting the cap off every user on that policy.
/// </summary>
public sealed class ConfigPageHeightOptionsTests : IDisposable
{
    private readonly string _tempDir;

    public ConfigPageHeightOptionsTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "qg-configpage-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
        File.WriteAllText(Path.Combine(_tempDir, "configPage.mjs"), ReadShippedConfigPage());
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, true);
        }
    }

    [Fact]
    public void MaxHeightSelect_OffersAConfiguredHeightThatIsNotAPreset()
    {
        var options = Evaluate("page.buildMaxHeightOptions(1000)");

        Assert.Contains("<option value=\"1000\" selected>1000p</option>", options, StringComparison.Ordinal);
        Assert.Equal("1000", SelectedValue(options));
    }

    /// <summary>
    /// The full round trip a save makes: the configured height reaches the browser as the
    /// selected option, and the value the browser reports is read back as the same height.
    /// </summary>
    [Fact]
    public void MaxHeightSelect_RoundTripsAConfiguredHeightThroughASave()
    {
        var selected = SelectedValue(Evaluate("page.buildMaxHeightOptions(1000)"));

        Assert.Equal("1000", Evaluate($"page.toHeight('{selected}')"));
    }

    [Fact]
    public void MaxHeightSelect_SelectsAPresetWithoutDuplicatingIt()
    {
        var options = Evaluate("page.buildMaxHeightOptions(720)");

        Assert.Equal("720", SelectedValue(options));
        Assert.Equal(6, Regex.Matches(options, "<option ").Count);
    }

    [Fact]
    public void MaxHeightSelect_SelectsNoLimitWhenThePolicyHasNoCap()
    {
        Assert.Equal("0", SelectedValue(Evaluate("page.buildMaxHeightOptions(0)")));
    }

    [Fact]
    public void FallbackSelect_OffersAConfiguredHeightThatIsNotAPreset()
    {
        var options = Evaluate(
            "page.buildFallbackOptions({ FallbackTranscode: true, FallbackMaxHeight: 1000 })");

        Assert.Contains("<option value=\"1000\" selected>Transcode to 1000p</option>", options, StringComparison.Ordinal);
        Assert.Equal("1000", SelectedValue(options));
    }

    [Fact]
    public void FallbackSelect_SelectsBlockPlaybackWhenFallbackIsOff()
    {
        var options = Evaluate(
            "page.buildFallbackOptions({ FallbackTranscode: false, FallbackMaxHeight: 0 })");

        Assert.Equal("off", SelectedValue(options));
    }

    // The within-cap toggle sits in the same Playback Behavior footer as the dropdowns above.
    [Fact]
    public void KeepDirectToggle_ShowsTheSavedValue()
    {
        var on = Evaluate("page.buildKeepDirectToggle({ KeepWithinCapVersionsDirect: true }, 2)");
        var off = Evaluate("page.buildKeepDirectToggle({ KeepWithinCapVersionsDirect: false }, 2)");
        var older = Evaluate("page.buildKeepDirectToggle({}, 2)");

        Assert.Contains("Play within-cap versions as they are", on, StringComparison.Ordinal);
        Assert.Contains("id=\"policy-keep-direct-2\" checked", on, StringComparison.Ordinal);
        Assert.DoesNotContain("checked", off, StringComparison.Ordinal);
        Assert.DoesNotContain("checked", older, StringComparison.Ordinal);
    }

    /// <summary>Reads the value the browser would report for a rendered dropdown.</summary>
    private static string SelectedValue(string options)
    {
        var selected = Assert.Single(Regex.Matches(options, "<option value=\"([^\"]*)\" selected>"));

        return selected.Groups[1].Value;
    }

    /// <summary>Reads configPage.js out of the built plugin assembly, as Jellyfin serves it.</summary>
    private static string ReadShippedConfigPage()
    {
        var assembly = typeof(Plugin).Assembly;
        var name = assembly.GetManifestResourceNames()
            .Single(n => n.EndsWith("configPage.js", StringComparison.Ordinal));

        using var stream = assembly.GetManifestResourceStream(name)!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    /// <summary>Evaluates an expression against the shipped page and returns its result.</summary>
    private string Evaluate(string expression)
    {
        var driver = "driver-" + Guid.NewGuid().ToString("N")[..8] + ".mjs";
        File.WriteAllText(
            Path.Combine(_tempDir, driver),
            "import * as page from './configPage.mjs';\nprocess.stdout.write(String(" + expression + "));\n");

        var startInfo = new ProcessStartInfo("node", driver)
        {
            WorkingDirectory = _tempDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        using var process = StartNode(startInfo);
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();

        Assert.True(process.ExitCode == 0, $"node exited {process.ExitCode} evaluating {expression}:\n{error}");
        return output;
    }

    private static Process StartNode(ProcessStartInfo startInfo)
    {
        try
        {
            return Process.Start(startInfo)!;
        }
        catch (Exception ex)
        {
            // Not a skip: the admin page is only covered while this can run.
            throw new InvalidOperationException(
                "node is required to run the config page tests. Install Node.js and put it on PATH.", ex);
        }
    }
}
