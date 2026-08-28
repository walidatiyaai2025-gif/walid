using PCCExecutive.Browser;
using Xunit;

namespace PCCExecutive.Browser.Tests;

public sealed class PersistentChromeProfileTests
{
    [Fact]
    public void Manager_reuses_one_persistent_profile_across_runtime_and_project_ids()
    {
        var root = Path.Combine(Path.GetTempPath(), "pcc-persistent-profile-tests", Guid.NewGuid().ToString("N"));
        var host = new PlaywrightChromeRuntimeHost(root, new NoopChromeLocator());

        var first = host.ResolvePersistentProfilePath(new BrowserSessionRequest("project-a", "manager-a", RuntimeId: "runtime-a"));
        var second = host.ResolvePersistentProfilePath(new BrowserSessionRequest("project-b", "manager-b", RuntimeId: "runtime-b"));

        Assert.Equal(Path.Combine(Path.GetFullPath(root), "manager"), first);
        Assert.Equal(first, second);
        Assert.DoesNotContain("runtime-a", first, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("runtime-b", second, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Worker_profiles_are_persistent_by_slot_and_isolated_from_each_other()
    {
        var root = Path.Combine(Path.GetTempPath(), "pcc-persistent-profile-tests", Guid.NewGuid().ToString("N"));
        var host = new PlaywrightChromeRuntimeHost(root, new NoopChromeLocator());

        var worker1a = host.ResolvePersistentProfilePath(new BrowserSessionRequest("project-a", "worker-a", "1", RuntimeId: "runtime-a"));
        var worker1b = host.ResolvePersistentProfilePath(new BrowserSessionRequest("project-b", "worker-b", "1", RuntimeId: "runtime-b"));
        var worker2 = host.ResolvePersistentProfilePath(new BrowserSessionRequest("project-b", "worker-c", "2", RuntimeId: "runtime-c"));

        Assert.Equal(worker1a, worker1b);
        Assert.Equal(Path.Combine(Path.GetFullPath(root), "worker-1"), worker1a);
        Assert.Equal(Path.Combine(Path.GetFullPath(root), "worker-2"), worker2);
        Assert.NotEqual(worker1a, worker2);
    }

    [Fact]
    public void GPTDesktop_existing_profile_has_a_canonical_source_identity()
    {
        Assert.Equal("__GPTDESKTOP__", PlaywrightChromeRuntimeHost.GptDesktopProfileSource);
    }

    [Fact]
    public void Launch_preparation_retires_only_runtime_endpoint_metadata_and_preserves_profile_state()
    {
        var root = Path.Combine(Path.GetTempPath(), "pcc-persistent-profile-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var endpoint = Path.Combine(root, "DevToolsActivePort");
        var cookieState = Path.Combine(root, "Cookies");
        File.WriteAllText(endpoint, "58760");
        File.WriteAllText(cookieState, "persistent-auth-state");

        PlaywrightChromeRuntimeHost.ClearStaleEndpointMetadata(root);

        Assert.False(File.Exists(endpoint));
        Assert.Equal("persistent-auth-state", File.ReadAllText(cookieState));
    }

    private sealed class NoopChromeLocator : IChromeExecutableLocator
    {
        public string LocateChrome() => throw new NotSupportedException("This regression test never launches Chrome.");
    }
}
