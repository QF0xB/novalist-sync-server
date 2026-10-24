using System.Net.WebSockets;

namespace Novalist.Sync.Server.Relay;

internal sealed class RelayRoute(RelayReservation reservation)
{
    private readonly object gate = new();
    private readonly CancellationTokenSource stop = new();
    private bool claimed;
    private bool completed;
    public RelayReservation Reservation { get; } = reservation;
    public CancellationToken Token => stop.Token;
    public Guid Id { get; } = Guid.NewGuid();
    public TaskCompletionSource<WebSocket> Host { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public bool Attached { get { lock (gate) return Host.Task.IsCompletedSuccessfully; } }

    public bool Claim()
    {
        lock (gate)
        {
            if (claimed || completed) return false;
            claimed = true;
            return true;
        }
    }

    public bool Attach(WebSocket socket)
    {
        lock (gate) return !completed && claimed && Host.TrySetResult(socket);
    }

    public void ReleaseClaim()
    {
        lock (gate)
            if (!completed && !Host.Task.IsCompletedSuccessfully) claimed = false;
    }

    public void Complete()
    {
        lock (gate)
        {
            if (completed) return;
            completed = true;
            Host.TrySetCanceled();
        }
        stop.Cancel();
        Done.TrySetResult();
    }
}
