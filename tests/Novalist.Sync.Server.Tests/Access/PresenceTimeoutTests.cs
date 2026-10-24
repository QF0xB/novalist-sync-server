using Microsoft.EntityFrameworkCore;
using Novalist.Sync.Contracts;
using Novalist.Sync.Server.Data;
using Novalist.Sync.Server.Tests.Infrastructure;
using Novalist.Sync.Server.Updates;

namespace Novalist.Sync.Server.Tests.Access;

public sealed class PresenceTimeoutTests(DatabaseFixture database)
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SlowPresenceHoldsNoTransactionAndDoesNotBlockProjectWrites(bool cancelCaller)
    {
        var ct = TestContext.Current.CancellationToken;
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var user = await database.CreateUserAsync();
        var device = await database.CreateDeviceAsync(user.Id);
        var project = await database.CreateProjectAsync((user.Id, MembershipRole.Owner));
        await using var writing = database.CreateContext();
        await using var sending = database.CreateContext();
        var presence = new PresenceAccess(sending, new(sending, database.DeliveryGate), new(sending, database.DeliveryGate), database.DeliveryGate);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopped = false;
        var delivery = presence.DeliverAsync(user.Id, device, TestData.Envelope(project, device, 1, MsgType.Presence), new HashSet<Guid> { device },
            async (_, _, token) =>
            {
                entered.TrySetResult();
                try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    cancelled.TrySetResult();
                    await finish.Task.WaitAsync(ct);
                    throw;
                }
                finally { stopped = true; }
            }, caller.Token);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
            Assert.Null(sending.Database.CurrentTransaction);
            var update = new UpdateLog(writing).AppendAsync(user.Id, TestData.Envelope(project, device, 2), ct);
            Assert.Equal(1, await update.WaitAsync(TimeSpan.FromSeconds(5), ct));
            Assert.False(stopped);
            if (cancelCaller) await caller.CancelAsync();
            await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
            Assert.False(stopped);
            finish.TrySetResult();
            if (cancelCaller) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => delivery);
            else
            {
                var error = await Assert.ThrowsAsync<PushRejectedException>(() => delivery);
                Assert.Equal("Presence delivery timed out.", error.Message);
            }
            Assert.True(stopped);
            Assert.Null(sending.Database.CurrentTransaction);
            Assert.Single(await writing.Updates.Where(value => value.ProjectId == project).ToListAsync(ct));
        }
        finally
        {
            finish.TrySetResult();
            await caller.CancelAsync();
        }
    }
}
