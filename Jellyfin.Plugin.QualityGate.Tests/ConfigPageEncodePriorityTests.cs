using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.QualityGate.Configuration;
using Jellyfin.Plugin.QualityGate.EncodePriority;
using Jellyfin.Plugin.QualityGate.Tests.Harness;

namespace Jellyfin.Plugin.QualityGate.Tests;

/// <summary>
/// The Encode Priority section of the admin page, run with node exactly as a browser gets it.
/// The mapping, output and audience helpers must agree with the server, so several cases here
/// are checked against the server's own code.
/// </summary>
public sealed class ConfigPageEncodePriorityTests : IDisposable
{
    private const string Mappings = "[{JellyfinPath:'/media/tv',EncoderPath:''},{JellyfinPath:'/media/movies/',EncoderPath:'movies/'},{JellyfinPath:'/media/tv/Show A',EncoderPath:'a'}]";

    private const string Config =
        "page.normalizeEncodePriority({EnableEncodePriority:true," +
        "Policies:[{Id:'p480',Name:'Mobile',MaxHeight:480},{Id:'p720',Name:'HD',MaxHeight:720},{Id:'p1080',Name:'Full',MaxHeight:1080},{Id:'poff',Name:'Off',MaxHeight:720,Enabled:false}]," +
        "DefaultPolicyId:'p720'," +
        "UserPolicies:[{UserId:'u480',PolicyId:'p480'},{UserId:'u1080',PolicyId:'p1080'},{UserId:'ufree',PolicyId:'__FULL_ACCESS__'},{UserId:'udeny',PolicyId:'poff'}]," +
        "EncodeTargets:[{Id:'t1',Name:'Shows',Folders:[{JellyfinPath:'/media/tv',EncoderPath:''}]}]})";

    private const string Users =
        "[{Id:'u720',Name:'a'},{Id:'u720b',Name:'b'},{Id:'u480',Name:'c'},{Id:'u1080',Name:'d'},{Id:'ufree',Name:'e'},{Id:'udeny',Name:'f'},{Id:'uoff',Name:'g',Policy:{IsDisabled:true}}]";

    private readonly ConfigPageScript _page = new();

    public void Dispose() => _page.Dispose();

    private T Eval<T>(string expression) => JsonSerializer.Deserialize<T>(_page.Json(expression))!;

    [Theory]
    [InlineData("/media/tv/Show B/Season 2/Show B S02E05.mkv", "Show B/Season 2/Show B S02E05.mkv")]
    [InlineData("/media/tv/Show A/Season 1/x.mkv", "a/Season 1/x.mkv")]
    [InlineData("/media/movies/Film A (2001)/Film A (2001).mkv", "movies/Film A (2001)/Film A (2001).mkv")]
    [InlineData("/media/tv2/Show B/x.mkv", null)]
    [InlineData("/media/tv", null)]
    [InlineData("/srv/x.mkv", null)]
    public void MapToEncoderPath_MatchesTheServer(string path, string? expected)
    {
        var page = Eval<string?>($"page.mapToEncoderPath({JsonSerializer.Serialize(path)}, {Mappings}, false)");

        var server = PriorityListBuilder.MapToEncoderPath(path, new[]
        {
            new FolderMapping("/media/tv", string.Empty),
            new FolderMapping("/media/movies", "movies"),
            new FolderMapping("/media/tv/Show A", "a"),
        });
        Assert.Equal(expected, page);
        Assert.Equal(server, page);
    }

    [Fact]
    public void MapToEncoderPath_OnWindows_AcceptsBothSeparatorsAndIgnoresCase()
    {
        Assert.Equal(
            "Show A/Season 1/x.mkv",
            Eval<string>(@"page.mapToEncoderPath('D:/Media/TV/Show A/Season 1/x.mkv', [{JellyfinPath:'d:\\media\\tv',EncoderPath:''}], true)"));
        Assert.Null(Eval<string?>(@"page.mapToEncoderPath('/media/tv\\x.mkv', [{JellyfinPath:'/media/tv/x.mkv',EncoderPath:''}], false)"));
    }

    [Fact]
    public void ResolveOutputPath_PerMode()
    {
        var source = Eval<Dictionary<string, string>>("page.resolveOutputPath({Id:'0123456789ab',OutputMode:'SourceFolder',Folders:[{JellyfinPath:'/media/movies',EncoderPath:'movies'},{JellyfinPath:'/media/tv/',EncoderPath:''}]}, '')");
        Assert.Equal("/media/tv/.encoder-priority.json", source["path"]);

        var data = Eval<Dictionary<string, string>>("page.resolveOutputPath({Id:'0123456789ab',OutputMode:'DataFolder',Folders:[]}, '/config/data')");
        Assert.Equal("/config/data/quality-gate/encode-priority/01234567.json", data["path"]);

        var unknownData = Eval<Dictionary<string, string>>("page.resolveOutputPath({Id:'0123456789ab',OutputMode:'DataFolder',Folders:[]}, '')");
        Assert.Equal("<Jellyfin data folder>/quality-gate/encode-priority/01234567.json", unknownData["path"]);

        var custom = Eval<Dictionary<string, string>>("page.resolveOutputPath({Id:'x',OutputMode:'Custom',OutputPath:'/lists/shows.json',Folders:[]}, '')");
        Assert.Equal("/lists/shows.json", custom["path"]);

        var noRoot = Eval<Dictionary<string, string>>("page.resolveOutputPath({Id:'x',OutputMode:'SourceFolder',Folders:[{JellyfinPath:'/media/tv',EncoderPath:'tv'}]}, '')");
        Assert.Equal(string.Empty, noRoot["path"]);
        Assert.Contains("Data folder or Custom", noRoot["error"], StringComparison.Ordinal);
    }

    [Fact]
    public void TargetAudience_Auto_ServesCappedPoliciesAtOrAboveTheOutput()
    {
        var audience = JsonDocument.Parse(_page.Json($"(function () {{ var c = {Config}; return page.targetAudience(c.EncodeTargets[0], c, {Users}); }})()")).RootElement;

        Assert.Equal(3, audience.GetProperty("viewers").GetInt32());
        var serves = audience.GetProperty("serves").EnumerateArray().Select(g => (g.GetProperty("name").GetString() ?? string.Empty, g.GetProperty("viewers").GetInt32())).ToList();
        Assert.Equal(new[] { ("HD", 2), ("Full", 1) }, serves);
        Assert.Equal("Mobile", Assert.Single(audience.GetProperty("cannotHelp").EnumerateArray()).GetProperty("name").GetString());
    }

    [Fact]
    public void TargetAudience_Users_CountsUncappedChoicesAndSkipsLowerCapsAndDenyAll()
    {
        var audience = JsonDocument.Parse(_page.Json(
            $"(function () {{ var c = {Config}; var t = c.EncodeTargets[0]; t.AudienceMode = 'Users'; t.AudienceUserIds = ['ufree','u480','udeny','u1080']; return page.targetAudience(t, c, {Users}); }})()")).RootElement;

        Assert.Equal(2, audience.GetProperty("viewers").GetInt32());
        Assert.Equal(1, audience.GetProperty("belowOutput").GetInt32());
    }

    [Fact]
    public void TargetAudience_Policies_OnlyTheChosenOnes()
    {
        var audience = JsonDocument.Parse(_page.Json(
            $"(function () {{ var c = {Config}; var t = c.EncodeTargets[0]; t.AudienceMode = 'Policies'; t.AudiencePolicyIds = ['p1080','p480']; return page.targetAudience(t, c, {Users}); }})()")).RootElement;

        Assert.Equal(1, audience.GetProperty("viewers").GetInt32());
        Assert.Equal("Full", Assert.Single(audience.GetProperty("serves").EnumerateArray()).GetProperty("name").GetString());
    }

    [Fact]
    public void Validate_AGoodConfig_HasNoErrors()
    {
        Assert.Empty(Eval<string[]>($"page.validateEncodeTargets({Config}, ['/media/tv'])"));
    }

    [Fact]
    public void Validate_WithTheFeatureOff_ChecksNothing()
    {
        Assert.Empty(Eval<string[]>("page.validateEncodeTargets({EnableEncodePriority:false,EncodeTargets:[{Name:'',Folders:[]}]}, [])"));
    }

    [Theory]
    [InlineData("{Name:'',Folders:[{JellyfinPath:'/media/tv',EncoderPath:''}]}", "a name of 1 to 64")]
    [InlineData("{Name:'A',Folders:[]}", "at least one folder")]
    [InlineData("{Name:'A',Folders:[{JellyfinPath:'media/tv',EncoderPath:''}]}", "absolute path")]
    [InlineData("{Name:'A',Folders:[{JellyfinPath:'/media/tv',EncoderPath:'../up'}]}", "must be relative")]
    [InlineData("{Name:'A',Folders:[{JellyfinPath:'/media/tv',EncoderPath:'/abs'}]}", "must be relative")]
    [InlineData("{Name:'A',Folders:[{JellyfinPath:'/media/tv',EncoderPath:''},{JellyfinPath:'/media/tv/Show A',EncoderPath:'a'}]}", "overlap")]
    [InlineData("{Name:'A',Folders:[{JellyfinPath:'/media/tv',EncoderPath:'tv'}]}", "Data folder or Custom")]
    [InlineData("{Name:'A',OutputMode:'Custom',OutputPath:'/media/tv/list.json',Folders:[{JellyfinPath:'/media/tv',EncoderPath:''}]}", "must start with a dot")]
    [InlineData("{Name:'A',AudienceMode:'Policies',Folders:[{JellyfinPath:'/media/tv',EncoderPath:''}]}", "at least one policy")]
    [InlineData("{Name:'A',AudienceMode:'Users',Folders:[{JellyfinPath:'/media/tv',EncoderPath:''}]}", "at least one user")]
    public void Validate_CatchesEachProblem(string target, string message)
    {
        var errors = Eval<string[]>($"page.validateEncodeTargets(page.normalizeEncodePriority({{EnableEncodePriority:true,EncodeTargets:[{target}]}}), ['/media/tv'])");

        Assert.Contains(errors, e => e.Contains(message, StringComparison.Ordinal));
    }

    [Fact]
    public void RemovingAnEmptyFolderRow_RemovesThatRowAndNotTheNextOne()
    {
        var folders = Eval<EncodeFolderMapping[]>(
            "(() => { var rows = page.readFolderRows([{value:''},{value:' /media/tv '}], [{value:''},{value:''}]); rows.splice(0, 1); return rows; })()");

        Assert.Equal("/media/tv", Assert.Single(folders).JellyfinPath);
    }

    [Fact]
    public void EmptyFolderRows_AreDroppedBeforeSaving()
    {
        var saved = Eval<string[]>(
            "(() => { var cfg = page.normalizeEncodePriority({EnableEncodePriority:true,EncodeTargets:[{Name:'A',Folders:[{JellyfinPath:'',EncoderPath:''},{JellyfinPath:'/media/tv',EncoderPath:''},{JellyfinPath:'',EncoderPath:''}]}]});" +
            " page.dropEmptyFolders(cfg); return page.validateEncodeTargets(cfg, ['/media/tv']).concat(cfg.EncodeTargets[0].Folders.map(f => f.JellyfinPath)); })()");

        Assert.Equal(new[] { "/media/tv" }, saved);
    }

    [Fact]
    public void AHandEditedModeInAnotherCase_IsReadAsTheServerReadsIt()
    {
        // The server ignores case and spaces; the page compares exactly. Without this a stored
        // "datafolder" fell through to Source folder and blocked every save.
        var target = JsonDocument.Parse(_page.Json(
            "page.normalizeEncodeTarget({Id:'abcdef12',Name:'X',OutputMode:' datafolder ',AudienceMode:'policies',Folders:[{JellyfinPath:'/media/tv',EncoderPath:'tv'}]})")).RootElement;

        Assert.Equal("DataFolder", target.GetProperty("OutputMode").GetString());
        Assert.Equal("Policies", target.GetProperty("AudienceMode").GetString());
        Assert.Equal("SourceFolder", _page.Json("page.normalizeEncodeTarget({OutputMode:'nonsense'}).OutputMode").Trim('"'));
        Assert.Empty(Eval<string[]>(
            "page.validateEncodeTargets({EnableEncodePriority:true,Policies:[],EncodeTargets:[page.normalizeEncodeTarget({Id:'abcdef12',Name:'X',OutputMode:'datafolder',Folders:[{JellyfinPath:'/media/tv',EncoderPath:'tv'}]})]}, ['/media/tv'])"));
    }

    [Theory]
    [InlineData(0, 144)]
    [InlineData(-5, 144)]
    [InlineData(9000, 4320)]
    [InlineData(1080, 1080)]
    public void AStoredOutputHeightOutsideTheServersRange_IsClampedAsTheServerClampsIt(int stored, int shown)
    {
        // Out of range, no option was selected and the select fell back to its first, 480p,
        // which a save then wrote over a field the admin never touched.
        Assert.Equal(shown, Eval<int>($"page.normalizeEncodeTarget({{OutputHeight:{stored}}}).OutputHeight"));
        Assert.Contains($"value=\"{shown}\" selected", Eval<string>($"page.outputHeightOptions(page.normalizeEncodeTarget({{OutputHeight:{stored}}}).OutputHeight)"), StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_NamesAndOutputFilesMustBeUnique()
    {
        var errors = Eval<string[]>(
            "page.validateEncodeTargets(page.normalizeEncodePriority({EnableEncodePriority:true,EncodeTargets:[" +
            "{Name:'Shows',Folders:[{JellyfinPath:'/media/tv',EncoderPath:''}]}," +
            "{Name:'shows',Folders:[{JellyfinPath:'/media/tv',EncoderPath:''}]}," +
            "{Name:'Low',OutputHeight:480,Folders:[{JellyfinPath:'/media/tv',EncoderPath:''}]}," +
            "{Name:'Dry',DryRun:true,Folders:[{JellyfinPath:'/media/tv',EncoderPath:''}]}]}), ['/media/tv'])");

        Assert.Contains(errors, e => e.Contains("same name", StringComparison.Ordinal));
        Assert.Equal(2, errors.Count(e => e.Contains("already writes /media/tv/.encoder-priority.json", StringComparison.Ordinal)));
        Assert.DoesNotContain(errors, e => e.Contains("\"Dry\"", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_ADisabledEncoderIsOnlyCheckedForAName()
    {
        Assert.Empty(Eval<string[]>("page.validateEncodeTargets(page.normalizeEncodePriority({EnableEncodePriority:true,EncodeTargets:[{Name:'Parked',Enabled:false,Folders:[]}]}), [])"));
    }

    [Fact]
    public void OutputHeight_AStoredHeightNotInTheList_IsStillOffered()
    {
        var options = Eval<string>("page.outputHeightOptions(1000)");

        Assert.Contains("<option value=\"1000\" selected>1000p</option>", options, StringComparison.Ordinal);
        Assert.Equal(7, Regex.Matches(options, "<option ").Count);
        Assert.Single(Regex.Matches(options, " selected"));
    }

    [Fact]
    public void OutputHeight_APreset_IsSelectedWithoutDuplicating()
    {
        var options = Eval<string>("page.outputHeightOptions(720)");

        Assert.Contains("<option value=\"720\" selected>720p</option>", options, StringComparison.Ordinal);
        Assert.Equal(6, Regex.Matches(options, "<option ").Count);
    }

    /// <summary>
    /// A deleted policy id survives a load and a save: normalisation keeps it, and the checklist
    /// renders it checked, so collecting the checked boxes writes it back.
    /// </summary>
    [Fact]
    public void ADeletedPolicyId_SurvivesALoadSaveCycle()
    {
        var html = Eval<string>(
            "(function () { var c = page.normalizeEncodePriority({Policies:[{Id:'p720',Name:'HD',MaxHeight:720}],EncodeTargets:[{Id:'t1',Name:'A',AudienceMode:'Policies',AudiencePolicyIds:['gone-policy-id','p720']}]}); " +
            "return page.buildAudiencePolicyChecklist(c.EncodeTargets[0], c.Policies, 0); })()");

        Assert.Contains("value=\"gone-policy-id\" checked", html, StringComparison.Ordinal);
        Assert.Contains("Deleted policy (gone-pol…)", html, StringComparison.Ordinal);
        Assert.Contains("value=\"p720\" checked", html, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnknownUserId_IsKeptCheckedAsUnknown()
    {
        var html = Eval<string>("page.buildUserChecklist(['gone-user-id'], [{Id:'u1',Name:'Viewer'}], 0, 'ep-excluded-user')");

        Assert.Contains("value=\"gone-user-id\" checked", html, StringComparison.Ordinal);
        Assert.Contains("Unknown user (gone-use…)", html, StringComparison.Ordinal);
        Assert.Contains("value=\"u1\" />", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Normalize_FillsTheDesignDefaultsAndKeepsUnknownFields()
    {
        var config = JsonDocument.Parse(_page.Json("page.normalizeEncodePriority({EncodeTargets:[{Id:'t1',Future:'kept'}]})")).RootElement;

        Assert.False(config.GetProperty("EnableEncodePriority").GetBoolean());
        Assert.True(config.GetProperty("PriorityNowPlaying").GetBoolean());
        Assert.Equal(3, config.GetProperty("PriorityNextUpDepth").GetInt32());
        Assert.Equal(180, config.GetProperty("PriorityRunBudgetSeconds").GetInt32());
        var target = config.GetProperty("EncodeTargets")[0];
        Assert.Equal("t1", target.GetProperty("Id").GetString());
        Assert.Equal(720, target.GetProperty("OutputHeight").GetInt32());
        Assert.Equal("SourceFolder", target.GetProperty("OutputMode").GetString());
        Assert.Equal("Auto", target.GetProperty("AudienceMode").GetString());
        Assert.Equal(300, target.GetProperty("MaxEntries").GetInt32());
        Assert.Equal("kept", target.GetProperty("Future").GetString());
    }

    /// <summary>The page's defaults are the server's defaults, so a fresh page save changes nothing.</summary>
    [Fact]
    public void Normalize_DefaultsMatchTheServer()
    {
        var page = JsonDocument.Parse(_page.Json("page.normalizeEncodePriority({})")).RootElement;
        var server = new PluginConfiguration();

        Assert.Equal(server.PriorityNowPlaying, page.GetProperty("PriorityNowPlaying").GetBoolean());
        Assert.Equal(server.PriorityContinueWatching, page.GetProperty("PriorityContinueWatching").GetBoolean());
        Assert.Equal(server.PriorityContinueWatchingPerUser, page.GetProperty("PriorityContinueWatchingPerUser").GetInt32());
        Assert.Equal(server.PriorityNextUp, page.GetProperty("PriorityNextUp").GetBoolean());
        Assert.Equal(server.PriorityNextUpDepth, page.GetProperty("PriorityNextUpDepth").GetInt32());
        Assert.Equal(server.PriorityNextUpShowsPerUser, page.GetProperty("PriorityNextUpShowsPerUser").GetInt32());
        Assert.Equal(server.PriorityNextUpIncludeSpecials, page.GetProperty("PriorityNextUpIncludeSpecials").GetBoolean());
        Assert.Equal(server.PriorityFavourites, page.GetProperty("PriorityFavourites").GetBoolean());
        Assert.Equal(server.PriorityFavouritesPerUser, page.GetProperty("PriorityFavouritesPerUser").GetInt32());
        Assert.Equal(server.PriorityWatchedWithinDays, page.GetProperty("PriorityWatchedWithinDays").GetInt32());
        Assert.Equal(server.PriorityUnprobedNeedsEncode, page.GetProperty("PriorityUnprobedNeedsEncode").GetBoolean());
        Assert.Equal(server.PriorityRefreshOnPlayback, page.GetProperty("PriorityRefreshOnPlayback").GetBoolean());
        Assert.Equal(server.PriorityDebounceMinutes, page.GetProperty("PriorityDebounceMinutes").GetInt32());
        Assert.Equal(server.PriorityRunBudgetSeconds, page.GetProperty("PriorityRunBudgetSeconds").GetInt32());

        var pageTarget = JsonDocument.Parse(_page.Json("page.normalizeEncodeTarget({})")).RootElement;
        var serverTarget = new EncodeTarget();
        Assert.Equal(serverTarget.OutputHeight, pageTarget.GetProperty("OutputHeight").GetInt32());
        Assert.Equal(serverTarget.OutputMode, pageTarget.GetProperty("OutputMode").GetString());
        Assert.Equal(serverTarget.AudienceMode, pageTarget.GetProperty("AudienceMode").GetString());
        Assert.Equal(serverTarget.MaxEntries, pageTarget.GetProperty("MaxEntries").GetInt32());
        Assert.Equal(serverTarget.Enabled, pageTarget.GetProperty("Enabled").GetBoolean());
    }

    [Fact]
    public void StatusPanels_ShowTheBadgeCountsAndFindingsFromWhatJellyfinShips()
    {
        var html = Eval<string>(
            "page.buildStatusPanels({EnableEncodePriority:true,EncodeTargets:[{Name:'Shows'},{Name:'Films'},{Name:'Parked',Enabled:false}]}," +
            "{LastExecutionResult:{Status:'Completed',StartTimeUtc:'2026-09-25T10:00:00Z',EndTimeUtc:'2026-09-25T10:00:12Z'}}," +
            "[{Type:'QualityGate.EncodePriority',ShortOverview:'Shows: 41 listed (+5, -3) · 7 viewers · 2 unmapped',Overview:'MostlyUnmapped: 9 of 20 watched gaps',Severity:'Warning',Date:'2026-09-25T10:00:12Z'}])");

        Assert.Contains("Shows: 41 listed (+5, -3) · 7 viewers · 2 unmapped", html, StringComparison.Ordinal);
        Assert.Contains(">Warning<", html, StringComparison.Ordinal);
        Assert.Contains("MostlyUnmapped: 9 of 20 watched gaps", html, StringComparison.Ordinal);
        Assert.Contains(">OK, unchanged<", html, StringComparison.Ordinal);
        Assert.Contains(">Off<", html, StringComparison.Ordinal);
        Assert.Contains("(12 s)", html, StringComparison.Ordinal);
    }

    private const string StatusJson =
        "{DataPath:'/config/data',Targets:[{Id:'t1',Name:'Shows',OutputPath:'/media/tv/.encoder-priority.json',LastRunUtc:'2026-09-25T10:00:00Z',Trigger:'playback',DurationMs:4200,Result:'Unchanged'," +
        "LastWriteUtc:'2026-09-25T09:00:00Z',Counts:{Viewers:7,DemandItems:30,Gaps:12,Listed:2,Covered:1,HeightUnknown:0,Unmapped:3,CutByLimit:0}," +
        "Findings:[{Code:'MostlyUnmapped',Message:'3 of 12 watched gaps are elsewhere.',Examples:['/media/other/x.mkv']}]," +
        "Entries:[{Rank:1,Title:'Show A S01E02 <b>Pilot</b>',Path:'Show A/Season 1/Show A S01E02.mkv',Height:1080,Users:2,Reasons:['next up']}," +
        "{Rank:2,Title:null,Path:'Gone/x.mkv',Height:null,Users:1,Reasons:['continue watching']}]}]," +
        "Preview:{RanAtUtc:'2026-09-25T11:00:00Z',Result:'OK',Targets:[{Id:'t1',Name:'Shows',Result:'DryRun',Counts:{Listed:1},Findings:[],Entries:[{Rank:1,Title:'Film A (2001)',Path:'Film A (2001)/Film A (2001).mkv',Height:2160,Users:1,Reasons:['favourite']}]}]},Covered:[]}";

    [Fact]
    public void StatusPanels_WithTheStatusEndpoint_ShowTheRunCountsFindingsAndList()
    {
        var html = Eval<string>(
            "page.buildStatusPanels({EnableEncodePriority:true,EncodeTargets:[{Id:'t1',Name:'Shows'},{Id:'t2',Name:'Films'}]}, " +
            "{LastExecutionResult:{Status:'Completed',EndTimeUtc:'2026-09-25T10:00:04Z'}}, [], " + StatusJson + ")");

        Assert.Contains(">OK, unchanged<", html, StringComparison.Ordinal);
        Assert.Contains("(4 s, playback)", html, StringComparison.Ordinal);
        Assert.Contains("7 viewers · 30 demand items · 12 gaps · 2 listed · 1 covered · 0 height unknown · 3 unmapped · 0 cut by the limit", html, StringComparison.Ordinal);
        Assert.Contains("<strong>MostlyUnmapped</strong>: 3 of 12 watched gaps are elsewhere.", html, StringComparison.Ordinal);
        Assert.Contains("<code>/media/other/x.mkv</code>", html, StringComparison.Ordinal);
        Assert.Contains("Current list (2 entries)", html, StringComparison.Ordinal);
        Assert.Contains("<code>Show A/Season 1/Show A S01E02.mkv</code>", html, StringComparison.Ordinal);
        Assert.Contains("Show A S01E02 &lt;b&gt;Pilot&lt;/b&gt;", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<b>Pilot", html, StringComparison.Ordinal);
        Assert.Contains("<td>1080p</td>", html, StringComparison.Ordinal);
        Assert.Contains("<span class=\"ep-chip\">2 viewers</span>", html, StringComparison.Ordinal);
        Assert.Contains("Item no longer in the library", html, StringComparison.Ordinal);
        Assert.Contains("<td>unknown</td>", html, StringComparison.Ordinal);
        Assert.Contains("(1 entries, nothing written)", html, StringComparison.Ordinal);
        Assert.Contains("Last 7 days: 0 listed · 0 covered · 0 served within cap · 0 played over the cap", html, StringComparison.Ordinal);
        Assert.Contains("<code>Film A (2001)/Film A (2001).mkv</code>", html, StringComparison.Ordinal);

        // The encoder the status does not know yet falls back to the scheduled task and activity log.
        Assert.Contains("<strong>Films</strong><span class=\"ep-badge ep-badge-ok\">OK, unchanged</span>", html, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("OK", "OK")]
    [InlineData("Warning", "Warning")]
    [InlineData("Error", "Error")]
    [InlineData("TimedOut", "Timed out, previous list kept")]
    [InlineData("DryRun", "Dry run")]
    public void StatusPanels_TheBadgeComesFromTheLastRun(string result, string badge)
    {
        var html = Eval<string>(
            "page.buildStatusPanels({EnableEncodePriority:true,EncodeTargets:[{Id:'t1',Name:'Shows'}]}, null, [], " +
            "{Targets:[{Id:'t1',Name:'Shows',Result:'" + result + "',Counts:{},Findings:[],Entries:[]}],Preview:null,Covered:[]})");

        Assert.Contains(">" + badge + "<", html, StringComparison.Ordinal);
        Assert.Contains("The list is empty", html, StringComparison.Ordinal);
    }

    [Fact]
    public void StatusPanels_ForAnEncoderThatIsOff_SayOffWhateverTheLastRun()
    {
        var html = Eval<string>(
            "page.buildStatusPanels({EnableEncodePriority:true,EncodeTargets:[{Id:'t1',Name:'Shows',Enabled:false}]}, null, [], " +
            "{Targets:[{Id:'t1',Name:'Shows',Result:'OK',Counts:{},Findings:[],Entries:[]}],Preview:null,Covered:[]})");

        Assert.Contains(">Off<", html, StringComparison.Ordinal);
    }

    [Fact]
    public void SevenDaySummary_CountsListedCoveredServedAndOverCap()
    {
        var summary = Eval<string>(
            "page.buildSevenDaySummary({Id:'t1',Entries:[{ItemId:'a',FirstListedAt:'2026-09-24T00:00:00Z'},{ItemId:'old',FirstListedAt:'2026-09-01T00:00:00Z'}]}, [" +
            "{ItemId:'b',TargetId:'t1',ListedAt:'2026-09-24T00:00:00Z',CoveredAt:'2026-09-24T02:00:00Z',ServedAt:'2026-09-24T05:00:00Z'}," +
            "{ItemId:'c',TargetId:'t1',ListedAt:'2026-09-24T00:00:00Z',CoveredAt:'2026-09-24T04:20:00Z',ServedOverCapAt:'2026-09-24T06:00:00Z'}," +
            "{ItemId:'d',TargetId:'t1',ListedAt:'2026-09-23T00:00:00Z',CoveredAt:'2026-09-23T03:10:00Z'}," +
            "{ItemId:'e',TargetId:'t2',ListedAt:'2026-09-24T00:00:00Z',CoveredAt:'2026-09-24T01:00:00Z',ServedAt:'2026-09-24T02:00:00Z'}," +
            "{ItemId:'f',TargetId:'t1',ListedAt:'2026-09-10T00:00:00Z',CoveredAt:'2026-09-11T00:00:00Z',ServedAt:'2026-09-11T02:00:00Z'}]," +
            "Date.parse('2026-09-25T10:00:00Z'))");

        Assert.Equal("Last 7 days: 4 listed · 3 covered (median 3 h 10 m) · 1 served within cap · 1 played over the cap", summary);
    }

    [Fact]
    public void SevenDaySummary_WithNothingCovered_HasNoMedian()
    {
        Assert.Equal(
            "Last 7 days: 0 listed · 0 covered · 0 served within cap · 0 played over the cap",
            Eval<string>("page.buildSevenDaySummary({Id:'t1',Entries:[]}, [], Date.parse('2026-09-25T10:00:00Z'))"));
    }

    [Fact]
    public void StatusPanels_BeforeTheFirstRun_SayWaiting()
    {
        var html = Eval<string>("page.buildStatusPanels({EnableEncodePriority:true,EncodeTargets:[{Name:'Shows'}]}, null, [])");

        Assert.Contains("Waiting for first run", html, StringComparison.Ordinal);
    }
}
