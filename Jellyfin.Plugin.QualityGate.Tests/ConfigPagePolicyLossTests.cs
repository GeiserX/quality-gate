using System;
using System.Text.Json;
using Jellyfin.Plugin.QualityGate.Tests.Harness;

namespace Jellyfin.Plugin.QualityGate.Tests;

/// <summary>
/// The warning the admin page shows before a policy is deleted or disabled. A policy that stops
/// resolving refuses playback to everyone who depends on it, so the confirm has to say who that is.
/// </summary>
public sealed class ConfigPagePolicyLossTests : IDisposable
{
    private const string Config =
        "{Policies:[{Id:'p1'},{Id:'p2'}],DefaultPolicyId:'p1',ApiKeyPolicyId:'p2'," +
        "UserPolicies:[{UserId:'a',PolicyId:'p1'},{UserId:'b',PolicyId:'p1'},{UserId:'c',PolicyId:'p2'},{UserId:'d',PolicyId:'__FULL_ACCESS__'}]}";

    private readonly ConfigPageScript _page = new();

    public void Dispose() => _page.Dispose();

    private string Warning(string policyId) =>
        JsonSerializer.Deserialize<string>(_page.Json($"page.lockoutWarning({Config}, '{policyId}')"))!;

    [Fact]
    public void CountsAssignedUsers_AndNamesTheDefault()
    {
        var warning = Warning("p1");

        Assert.Contains("2 users are assigned to it and will lose playback.", warning, StringComparison.Ordinal);
        Assert.Contains("all unassigned users will lose playback", warning, StringComparison.Ordinal);
        Assert.DoesNotContain("API key", warning, StringComparison.Ordinal);
    }

    [Fact]
    public void CountsOneUser_AndNamesTheApiKeyPolicy()
    {
        var warning = Warning("p2");

        Assert.Contains("1 user is assigned to it and will lose playback.", warning, StringComparison.Ordinal);
        Assert.Contains("API key and anonymous requests will be refused", warning, StringComparison.Ordinal);
        Assert.DoesNotContain("unassigned", warning, StringComparison.Ordinal);
    }

    [Fact]
    public void SaysNothing_WhenNobodyDependsOnThePolicy()
    {
        Assert.Equal(string.Empty, Warning("unused"));
    }
}
