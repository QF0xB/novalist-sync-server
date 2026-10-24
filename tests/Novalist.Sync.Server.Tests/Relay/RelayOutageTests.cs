using System.Net;

namespace Novalist.Sync.Server.Tests.Relay;

public sealed class RelayOutageTests
{
    [Fact]
    public async Task AFailedPeriodicCheckKeepsTheHostUntilTheGraceDeadlineAndCanRecover()
    {
        var unavailable = 0;
        var clock = new RelayTestClock();
        await using var server = new RelayTestServer(clock: clock,
            introspection: options => { options.CheckIntervalSeconds = 5; options.GracePeriodSeconds = 60; },
            response: (_, _) => Task.FromResult(new HttpResponseMessage(Volatile.Read(ref unavailable) == 0
                ? HttpStatusCode.OK : HttpStatusCode.ServiceUnavailable)
            { Content = new StringContent("{\"active\":true}") }));
        using var control = server.Socket();
        await server.ConnectAsync(control, "host");
        var session = await server.ReadIdAsync(control);
        await clock.NextDelayAsync();
        await clock.NextDelayAsync();
        Interlocked.Exchange(ref unavailable, 1);
        clock.Advance(TimeSpan.FromSeconds(5));
        await clock.NextDelayAsync();
        await clock.NextDelayAsync();
        await server.RejectedAsync("host", HttpStatusCode.ServiceUnavailable);
        Interlocked.Exchange(ref unavailable, 0);
        using var guest = server.Socket("guest");
        var joining = server.ConnectAsync(guest, $"sessions/{session}/guest");
        var route = await server.ReadIdAsync(control);
        using var host = server.Socket();
        await server.ConnectAsync(host, $"sessions/{session}/routes/{route}");
        await joining;
    }
}
