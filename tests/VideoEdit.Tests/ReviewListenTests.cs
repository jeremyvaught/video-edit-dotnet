using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using VideoEdit.Services;

namespace VideoEdit.Tests;

public class ReviewListenTests
{
    [Fact]
    public void PrepareServesAndPrepareOnlyDoesNot()
    {
        Assert.True(ReviewListen.WillServe(["--prepare"]));
        Assert.False(ReviewListen.WillServe(["--prepare-only"]));
        Assert.False(ReviewListen.WillServe(["--prepare", "--prepare-only"]));
        Assert.False(ReviewListen.WillServe(["--align-script"]));
        Assert.True(ReviewListen.WillServe([]));
    }

    [Fact]
    public void ConflictMessageNamesTheOccupyingProject()
    {
        var message = ReviewListen.ConflictMessage(
            new ListenConflict("http://127.0.0.1:5081", "/tmp/edits/example"));
        Assert.Equal(
            "http://127.0.0.1:5081 is already serving /tmp/edits/example",
            message);
        Assert.Equal(
            "http://127.0.0.1:5081 is already serving an unknown project",
            ReviewListen.ConflictMessage(new ListenConflict("http://127.0.0.1:5081", null)));
    }

    [Fact]
    public void ProjectDirectoryIsTheFolderNotTheJsonFile()
    {
        Assert.Equal(
            "/tmp/edits/sample",
            ReviewListen.ProjectDirectoryFromPath("/tmp/edits/sample/edit/project.json"));
    }

    [Fact]
    public async Task BusyPortPrintsTheConflictAndMovesToTheNextFreePort()
    {
        var held = BindLoopback();
        try
        {
            var url = ReviewListen.Format("127.0.0.1", ((IPEndPoint)held.LocalEndpoint).Port);
            var plan = await ReviewListen.ResolveAsync(url);
            Assert.NotNull(plan.Conflict);
            Assert.Equal(url, plan.Conflict!.Url);
            Assert.NotNull(plan.Url);
            Assert.NotEqual(url, plan.Url);
            Assert.Contains("is already serving", ReviewListen.ConflictMessage(plan.Conflict));
            Assert.True(ReviewListen.TryParse(plan.Url!, out _, out var next));
            Assert.True(ReviewListen.PortIsFree("127.0.0.1", next));
        }
        finally
        {
            held.Stop();
        }
    }

    [Fact]
    public async Task FreePortKeepsTheConfiguredUrl()
    {
        var probe = BindLoopback();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        var url = ReviewListen.Format("127.0.0.1", port);
        var plan = await ReviewListen.ResolveAsync(url);
        Assert.Null(plan.Conflict);
        Assert.Equal(url, plan.Url);
    }

    [Fact]
    public void BracketedIpv6LoopbackRoundTrips()
    {
        Assert.True(ReviewListen.TryParse("http://[::1]:5099", out var host, out var port));
        Assert.Equal("::1", host);
        Assert.Equal(5099, port);
        Assert.Equal("http://[::1]:5099", ReviewListen.Format(host, port));
        Assert.True(ReviewListen.TryParse(ReviewListen.Format(host, port), out var again, out var portAgain));
        Assert.Equal(host, again);
        Assert.Equal(port, portAgain);
    }

    [Fact]
    public async Task ExplicitIpv6ReviewUrlServesTheProject()
    {
        var (store, _, _, root) = TestSupport.CreateStore();
        store.SaveNew(TestSupport.SampleProject());
        var held = BindIpv6Only();
        var port = ((IPEndPoint)held.LocalEndpoint).Port;
        held.Stop();
        var url = ReviewListen.Format("::1", port);
        using var host = await TestSupport.StartReviewAsync(root, url);
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        using var response = await client.GetAsync(url + "/api/project");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("\"revision\"", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LocalhostIsBusyWhenIpv6LoopbackIsHeld()
    {
        var held = BindIpv6Only();
        try
        {
            var port = ((IPEndPoint)held.LocalEndpoint).Port;
            Assert.False(ReviewListen.PortIsFree("localhost", port));
            Assert.True(ReviewListen.PortIsFree("127.0.0.1", port));
            var plan = await ReviewListen.ResolveAsync($"http://localhost:{port}");
            Assert.NotNull(plan.Conflict);
            Assert.Equal($"http://localhost:{port}", plan.Conflict!.Url);
            Assert.NotNull(plan.Url);
            Assert.NotEqual($"http://localhost:{port}", plan.Url);
            Assert.StartsWith("http://localhost:", plan.Url);
        }
        finally
        {
            held.Stop();
        }
    }

    private static TcpListener BindLoopback()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return listener;
    }

    private static TcpListener BindIpv6Only()
    {
        var listener = new TcpListener(IPAddress.IPv6Loopback, 0);
        listener.Server.DualMode = false;
        listener.Start();
        return listener;
    }
}
