namespace Novalist.Sync.Server.Access;

public sealed class AccessDeliveryGate
{
    private readonly object sync = new();
    private readonly Dictionary<(bool Account, Guid Id), Entry> entries = [];

    public ValueTask<IAsyncDisposable> AccountAsync(Guid account, CancellationToken ct) => AcquireAsync((true, account), ct);
    public ValueTask<IAsyncDisposable> ProjectAsync(Guid project, CancellationToken ct) => AcquireAsync((false, project), ct);

    public async ValueTask<IAsyncDisposable> AccountsAsync(IEnumerable<Guid> accounts, CancellationToken ct)
    {
        var acquired = new List<IAsyncDisposable>();
        try
        {
            foreach (var account in accounts.Distinct().Order())
                acquired.Add(await AccountAsync(account, ct));
            return new AccountLeases(acquired);
        }
        catch
        {
            foreach (var lease in acquired.AsEnumerable().Reverse()) await lease.DisposeAsync();
            throw;
        }
    }

    private async ValueTask<IAsyncDisposable> AcquireAsync((bool Account, Guid Id) key, CancellationToken ct)
    {
        Entry entry;
        lock (sync)
        {
            if (entries.TryGetValue(key, out var current)) entry = current;
            else entries.Add(key, entry = new());
            entry.Users++;
        }
        try { await entry.Semaphore.WaitAsync(ct); }
        catch { ReleaseReference(key, entry); throw; }
        return new Lease(this, key, entry);
    }

    private void ReleaseReference((bool Account, Guid Id) key, Entry entry)
    {
        lock (sync)
        {
            if (--entry.Users != 0) return;
            entries.Remove(key);
            entry.Semaphore.Dispose();
        }
    }

    private sealed class Entry
    {
        public SemaphoreSlim Semaphore { get; } = new(1, 1);
        public int Users { get; set; }
    }

    private sealed class Lease(AccessDeliveryGate owner, (bool Account, Guid Id) key, Entry entry) : IAsyncDisposable
    {
        private int disposed;

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref disposed, 1) == 0)
            {
                entry.Semaphore.Release();
                owner.ReleaseReference(key, entry);
            }
            return ValueTask.CompletedTask;
        }
    }

    private sealed class AccountLeases(List<IAsyncDisposable> leases) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            foreach (var lease in leases.AsEnumerable().Reverse()) await lease.DisposeAsync();
        }
    }
}
