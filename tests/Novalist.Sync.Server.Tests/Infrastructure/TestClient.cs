using System.Threading.Channels;
using Microsoft.AspNetCore.SignalR.Client;
using Novalist.Sync.Contracts;

namespace Novalist.Sync.Server.Tests.Infrastructure;

// A connected device: its hub connection plus everything the server sent to it.
public sealed class TestClient : IAsyncDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private readonly Channel<SequencedEnvelope> _updates = Channel.CreateUnbounded<SequencedEnvelope>();
    private readonly TaskCompletionSource<Exception?> _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public TestClient(HubConnection connection, Guid userId, Guid deviceId)
    {
        Connection = connection;
        UserId = userId;
        DeviceId = deviceId;

        connection.On<SequencedEnvelope>(nameof(ISyncClient.ReceiveUpdate), update => _updates.Writer.TryWrite(update));
        connection.Closed += exception =>
        {
            _closed.TrySetResult(exception);
            return Task.CompletedTask;
        };
    }

    public HubConnection Connection { get; }

    public Guid UserId { get; }

    public Guid DeviceId { get; }

    public Task<Exception?> Closed => _closed.Task.WaitAsync(Timeout);

    // The timeout cancels the read itself, so a timed-out call cannot swallow a later update.
    public async Task<SequencedEnvelope> NextUpdateAsync()
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(Timeout);
        return await _updates.Reader.ReadAsync(timeout.Token);
    }

    public bool TryTakeUpdate(out SequencedEnvelope? update) => _updates.Reader.TryRead(out update);

    public Task CreateProjectAsync(Guid projectId) =>
        Connection.InvokeAsync(nameof(ISyncHub.CreateProject), projectId, TestContext.Current.CancellationToken);

    public Task JoinAsync(Guid projectId) =>
        Connection.InvokeAsync(nameof(ISyncHub.JoinProject), projectId, TestContext.Current.CancellationToken);

    public Task LeaveAsync(Guid projectId) =>
        Connection.InvokeAsync(nameof(ISyncHub.LeaveProject), projectId, TestContext.Current.CancellationToken);

    public Task<long> PushAsync(Envelope envelope) =>
        Connection.InvokeAsync<long>(nameof(ISyncHub.Push), envelope, TestContext.Current.CancellationToken);

    public Task<List<SequencedEnvelope>> CatchUpAsync(Guid projectId, long afterServerSeq) =>
        Connection
            .StreamAsync<SequencedEnvelope>(nameof(ISyncHub.CatchUp), projectId, afterServerSeq, TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken)
            .AsTask();

    public async ValueTask DisposeAsync() => await Connection.DisposeAsync();
}
