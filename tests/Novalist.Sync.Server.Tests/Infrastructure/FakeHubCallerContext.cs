using System.Security.Claims;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.SignalR;

namespace Novalist.Sync.Server.Tests.Infrastructure;

// A stand-in for a SignalR connection that only records whether it was aborted.
public sealed class FakeHubCallerContext : HubCallerContext
{
    private readonly CancellationTokenSource _aborted = new();

    public bool Aborted => _aborted.IsCancellationRequested;

    public override string ConnectionId { get; } = Guid.NewGuid().ToString();

    public override string? UserIdentifier => null;

    public override ClaimsPrincipal? User => null;

    public override IDictionary<object, object?> Items { get; } = new Dictionary<object, object?>();

    public override IFeatureCollection Features { get; } = new FeatureCollection();

    // Like a real connection, aborting cancels ConnectionAborted.
    public override CancellationToken ConnectionAborted => _aborted.Token;

    public override void Abort() => _aborted.Cancel();
}
