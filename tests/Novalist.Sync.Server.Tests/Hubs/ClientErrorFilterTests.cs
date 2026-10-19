using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Novalist.Sync.Contracts.Exceptions;
using Novalist.Sync.Server.Access;
using Novalist.Sync.Server.Hubs;
using Novalist.Sync.Server.Tests.Infrastructure;
using Novalist.Sync.Server.Updates;

namespace Novalist.Sync.Server.Tests.Hubs;

public class ClientErrorFilterTests
{
    private readonly ClientErrorFilter _filter = new();

    public static TheoryData<Exception> ClientErrors =>
    [
        new AccessDeniedException("denied"),
        new ConflictException("taken"),
        new PushRejectedException("rejected"),
        new FormatException("bad header"),
        new UnsupportedProtocolVersionException(2),
    ];

    [Theory]
    [MemberData(nameof(ClientErrors))]
    public async Task ClientErrors_ReachTheClientWithTheirMessage(Exception error)
    {
        var ex = await Assert.ThrowsAsync<HubException>(() => InvokeThrowingAsync(error));

        Assert.Equal(error.Message, ex.Message);
    }

    [Fact]
    public async Task ServerErrors_AreNotTranslated()
    {
        var error = new InvalidOperationException("connection string with a password");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => InvokeThrowingAsync(error));

        Assert.Same(error, ex);
    }

    [Fact]
    public async Task OnConnected_TranslatesClientErrorsToo()
    {
        var context = new HubLifetimeContext(new FakeHubCallerContext(), new ServiceCollection().BuildServiceProvider(), new DummyHub());

        var ex = await Assert.ThrowsAsync<HubException>(
            () => _filter.OnConnectedAsync(context, _ => throw new AccessDeniedException("revoked")));

        Assert.Equal("revoked", ex.Message);
    }

    [Fact]
    public async Task SuccessfulCalls_PassTheResultThrough()
    {
        var result = await _filter.InvokeMethodAsync(InvocationContext(), _ => ValueTask.FromResult<object?>(42L));

        Assert.Equal(42L, result);
    }

    private async Task InvokeThrowingAsync(Exception error) =>
        await _filter.InvokeMethodAsync(InvocationContext(), _ => throw error);

    private static HubInvocationContext InvocationContext() =>
        new(
            new FakeHubCallerContext(),
            new ServiceCollection().BuildServiceProvider(),
            new DummyHub(),
            typeof(DummyHub).GetMethod(nameof(DummyHub.Ping))!,
            []);

    private sealed class DummyHub : Hub
    {
        public static void Ping()
        {
        }
    }
}
