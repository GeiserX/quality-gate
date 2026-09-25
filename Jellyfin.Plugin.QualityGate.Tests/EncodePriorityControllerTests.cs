using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using Jellyfin.Plugin.QualityGate.EncodePriority;
using MediaBrowser.Common.Api;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Moq;

namespace Jellyfin.Plugin.QualityGate.Tests;

/// <summary>The admin-only status and preview routes the settings page reads.</summary>
[Collection(PluginInstanceCollection.Name)]
public sealed class EncodePriorityControllerTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 9, 25, 10, 0, 0, DateTimeKind.Utc);

    private readonly string _data = Path.Combine(Path.GetTempPath(), "qg-api-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly Mock<ILibraryManager> _library = new();
    private readonly Mock<ITaskManager> _tasks = new();

    public EncodePriorityControllerTests()
    {
        EncodePriorityRuntime.Reset();
    }

    public void Dispose()
    {
        EncodePriorityRuntime.Reset();
        if (Directory.Exists(_data))
        {
            Directory.Delete(_data, true);
        }
    }

    private EncodePriorityController NewController()
    {
        var paths = new Mock<IApplicationPaths>();
        paths.Setup(p => p.DataPath).Returns(_data);
        return new EncodePriorityController(paths.Object, _library.Object, _tasks.Object);
    }

    private JsonElement Status() => JsonDocument.Parse(NewController().GetStatus().Content!).RootElement;

    [Fact]
    public void EveryRoute_RequiresAnAdministrator()
    {
        var type = typeof(EncodePriorityController);
        var authorize = Assert.Single(type.GetCustomAttributes<AuthorizeAttribute>());
        Assert.Equal(Policies.RequiresElevation, authorize.Policy);
        Assert.Empty(type.GetCustomAttributes<AllowAnonymousAttribute>());

        var actions = type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);
        Assert.Equal(new[] { "GetStatus", "Preview" }, actions.Select(a => a.Name).OrderBy(n => n, StringComparer.Ordinal));
        foreach (var action in actions)
        {
            Assert.Empty(action.GetCustomAttributes<AllowAnonymousAttribute>());
            Assert.Empty(action.GetCustomAttributes<AuthorizeAttribute>());
        }
    }

    [Fact]
    public void Routes_AreAGetForStatusAndAPostForPreview()
    {
        Assert.Equal("QualityGate/EncodePriority", typeof(EncodePriorityController).GetCustomAttribute<RouteAttribute>()!.Template);
        Assert.Equal("Status", Route<HttpGetAttribute>(nameof(EncodePriorityController.GetStatus)));
        Assert.Equal("Preview", Route<HttpPostAttribute>(nameof(EncodePriorityController.Preview)));
    }

    [Fact]
    public void Status_WithNoStateYet_IsEmpty()
    {
        var status = Status();

        Assert.Equal(_data, status.GetProperty("DataPath").GetString());
        Assert.Equal(0, status.GetProperty("Targets").GetArrayLength());
        Assert.Equal(JsonValueKind.Null, status.GetProperty("Preview").ValueKind);
        Assert.Equal(0, status.GetProperty("Covered").GetArrayLength());
    }

    [Fact]
    public void Status_ReturnsTheStateFileWithTitles()
    {
        var film = Guid.NewGuid();
        var episode = Guid.NewGuid();
        var gone = Guid.NewGuid();
        _library.Setup(l => l.GetItemById(film)).Returns(new Movie { Name = "Film A", ProductionYear = 2001 });
        _library.Setup(l => l.GetItemById(episode)).Returns(new Episode { Name = "Pilot", SeriesName = "Show B", ParentIndexNumber = 1, IndexNumber = 2 });

        var state = new EncodePriorityState();
        state.Targets["t1"] = new TargetState
        {
            Name = "Shows",
            OutputPath = "/media/tv/.encoder-priority.json",
            LastRunUtc = Now,
            Trigger = "scheduled",
            Result = "OK",
            Counts = new TargetCounts { Listed = 3, Viewers = 2 },
            Findings = new List<Finding> { new("UnknownHeights", "1 watched items have never been probed.") },
            Entries = new List<StateEntry>
            {
                new() { Path = "Show B/Season 1/Show B S01E02.mkv", ItemId = episode, Height = 1080, GapCap = 720, Tier = 2, Depth = 1, Users = 2, FirstListedAt = Now, Reasons = new List<string> { "next up" } },
                new() { Path = "Film A (2001)/Film A (2001).mkv", ItemId = film, Height = 2160, GapCap = 720, Tier = 3, Users = 1, FirstListedAt = Now },
                new() { Path = "Gone/x.mkv", ItemId = gone, GapCap = 720, FirstListedAt = Now },
            },
        };
        state.Preview = new PreviewState { RanAtUtc = Now, Result = "OK" };
        state.Preview.Targets["t1"] = new TargetState { Name = "Shows", Result = "DryRun" };
        state.Covered.Add(new CoveredRecord { ItemId = film, TargetId = "t1", ListedAt = Now.AddHours(-3), CoveredAt = Now, GapCap = 720 });
        state.Save(EncodePriorityPaths.StateFile(_data));

        var status = Status();

        var target = Assert.Single(status.GetProperty("Targets").EnumerateArray());
        Assert.Equal("t1", target.GetProperty("Id").GetString());
        Assert.Equal("OK", target.GetProperty("Result").GetString());
        Assert.Equal("/media/tv/.encoder-priority.json", target.GetProperty("OutputPath").GetString());
        Assert.Equal(3, target.GetProperty("Counts").GetProperty("Listed").GetInt32());
        Assert.Equal("UnknownHeights", target.GetProperty("Findings")[0].GetProperty("Code").GetString());
        var entries = target.GetProperty("Entries").EnumerateArray().ToList();
        Assert.Equal(new[] { 1, 2, 3 }, entries.Select(e => e.GetProperty("Rank").GetInt32()));
        Assert.Equal("Show B S01E02 Pilot", entries[0].GetProperty("Title").GetString());
        Assert.Equal("next up", entries[0].GetProperty("Reasons")[0].GetString());
        Assert.Equal("Film A (2001)", entries[1].GetProperty("Title").GetString());
        Assert.Equal(JsonValueKind.Null, entries[2].GetProperty("Title").ValueKind);
        Assert.Equal("DryRun", status.GetProperty("Preview").GetProperty("Targets")[0].GetProperty("Result").GetString());
        Assert.Equal("Film A (2001)", status.GetProperty("Covered")[0].GetProperty("Title").GetString());
        _library.Verify(l => l.GetItemById(film), Times.Once);
    }

    [Fact]
    public void Preview_QueuesADryRunAndWritesNothingItself()
    {
        var result = NewController().Preview();

        Assert.Equal(202, result.StatusCode);
        _tasks.Verify(t => t.QueueScheduledTask<EncodePriorityTask>(), Times.Once);
        Assert.True(EncodePriorityRuntime.TakePreview());
        Assert.Equal("preview", EncodePriorityRuntime.TakeTrigger());
        Assert.False(Directory.Exists(_data));
    }

    private static string? Route<T>(string action)
        where T : HttpMethodAttribute
        => typeof(EncodePriorityController).GetMethod(action)!.GetCustomAttribute<T>()!.Template;
}
