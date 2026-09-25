using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.QualityGate.Configuration;
using Jellyfin.Plugin.QualityGate.Filters;
using Jellyfin.Plugin.QualityGate.Services;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;
using Microsoft.Extensions.Logging;
using Moq;

namespace Jellyfin.Plugin.QualityGate.Tests;

/// <summary>
/// <see cref="QualityGateService.IsCapGap"/> is the single "every version is over the cap" test.
/// Playback decides on a forced transcode with it, and the encode priority list decides what
/// needs an encode with it.
/// </summary>
public class CapGapTests
{
    private const int Cap = 720;

    public static IEnumerable<object?[]> TruthTable()
    {
        // heights, cap, gap by default, gap when an unknown height counts as over the cap
        yield return new object?[] { Array.Empty<int?>(), Cap, false, false };
        yield return new object?[] { new int?[] { null }, Cap, false, true };
        yield return new object?[] { new int?[] { null, null }, Cap, false, true };
        yield return new object?[] { new int?[] { null, 1080 }, Cap, false, true };
        yield return new object?[] { new int?[] { 1080 }, Cap, true, true };
        yield return new object?[] { new int?[] { 2160, 1080 }, Cap, true, true };
        yield return new object?[] { new int?[] { 2160, 720 }, Cap, false, false };
        yield return new object?[] { new int?[] { 720 }, Cap, false, false };
        yield return new object?[] { new int?[] { 721 }, Cap, true, true };
        yield return new object?[] { new int?[] { 480 }, Cap, false, false };
        yield return new object?[] { new int?[] { 2160 }, 0, false, false };
        yield return new object?[] { new int?[] { null }, 0, false, false };
        yield return new object?[] { new int?[] { 2160 }, -1, false, false };
    }

    [Theory]
    [MemberData(nameof(TruthTable))]
    public void IsCapGap_TruthTable(int?[] heights, int cap, bool gap, bool gapWithUnprobedOver)
    {
        Assert.Equal(gap, QualityGateService.IsCapGap(heights, cap));
        Assert.Equal(gap, QualityGateService.IsCapGap(heights, cap, unprobedIsOverCap: false));
        Assert.Equal(gapWithUnprobedOver, QualityGateService.IsCapGap(heights, cap, unprobedIsOverCap: true));
    }

    [Fact]
    public void IsCapGap_NullList_IsNoGap()
    {
        Assert.False(QualityGateService.IsCapGap(null!, Cap, unprobedIsOverCap: true));
    }

    public static IEnumerable<object[]> HeightSets()
    {
        yield return new object[] { new int?[] { 1080 } };
        yield return new object[] { new int?[] { 2160, 1080 } };
        yield return new object[] { new int?[] { 2160, 720 } };
        yield return new object[] { new int?[] { 720 } };
        yield return new object[] { new int?[] { 480, 1080 } };
        yield return new object[] { new int?[] { null } };
        yield return new object[] { new int?[] { null, 1080 } };
        yield return new object[] { new int?[] { 721 } };
    }

    /// <summary>
    /// Playback and the list share the predicate: PlaybackInfo forces a transcode of every source
    /// exactly when <see cref="QualityGateService.IsCapGap"/> says the item is a gap.
    /// </summary>
    [Theory]
    [MemberData(nameof(HeightSets))]
    public void CapPlaybackInfo_AgreesWithIsCapGap(int?[] heights)
    {
        var filter = new ResolutionCapFilter(Mock.Of<ILogger<ResolutionCapFilter>>(), Mock.Of<IMediaSourceManager>());
        var policy = new QualityPolicy { Id = "p1", Name = "Capped", MaxHeight = Cap };
        var response = new PlaybackInfoResponse
        {
            MediaSources = heights.Select((h, i) => new MediaSourceInfo
            {
                Id = "s" + i,
                Path = "/media/x" + i + ".mkv",
                SupportsDirectPlay = true,
                SupportsDirectStream = true,
                MediaStreams = new List<MediaStream> { new() { Type = MediaStreamType.Video, Height = h } },
            }).ToArray(),
        };

        filter.CapPlaybackInfo(response, policy, Guid.NewGuid());

        var forcedTranscode = response.MediaSources.All(s => !s.SupportsDirectPlay && !s.SupportsDirectStream);
        Assert.Equal(QualityGateService.IsCapGap(heights, Cap), forcedTranscode);
    }
}
