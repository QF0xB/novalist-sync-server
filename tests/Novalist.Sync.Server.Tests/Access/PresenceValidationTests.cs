using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Novalist.Sync.Contracts;
using Novalist.Sync.Server.Access;
using Novalist.Sync.Server.Data;
using Novalist.Sync.Server.Tests.Infrastructure;
using Novalist.Sync.Server.Updates;

namespace Novalist.Sync.Server.Tests.Access;

public sealed class PresenceValidationTests(DatabaseFixture database)
{
    [Theory]
    [InlineData("nonce")]
    [InlineData("signature")]
    [InlineData("ciphertext")]
    [InlineData("message")]
    [InlineData("sender")]
    [InlineData("sequence")]
    [InlineData("membership")]
    [InlineData("device")]
    public async Task InvalidPresenceIsRejectedBeforeOpeningATransaction(string invalid)
    {
        var ct = TestContext.Current.CancellationToken;
        var owner = await database.CreateUserAsync();
        var sender = invalid == "membership" ? await database.CreateUserAsync() : owner;
        var device = await database.CreateDeviceAsync(sender.Id, revoked: invalid == "device");
        var project = await database.CreateProjectAsync((owner.Id, MembershipRole.Owner));
        var envelope = TestData.Envelope(project, device, 1, MsgType.Presence);
        var header = EnvelopeHeader.Parse(envelope.Header);
        envelope = invalid switch
        {
            "nonce" => envelope with { Nonce = new byte[23] },
            "signature" => envelope with { Signature = new byte[63] },
            "ciphertext" => envelope with { Ciphertext = new byte[4097] },
            "message" => envelope with { Header = (header with { MsgType = MsgType.Update }).ToBytes() },
            "sender" => envelope with { Header = (header with { SenderDeviceId = Guid.NewGuid() }).ToBytes() },
            "sequence" => envelope with { Header = (header with { SenderSeq = 0 }).ToBytes() },
            _ => envelope
        };
        var transactions = new TransactionCounter();
        await using var db = database.CreateContext(transactions);
        var presence = new PresenceAccess(db, new(db, database.DeliveryGate), new(db, database.DeliveryGate), database.DeliveryGate);
        Task Send() => presence.DeliverAsync(sender.Id, device, envelope, new HashSet<Guid> { device },
            (_, _, _) => throw new InvalidOperationException("Invalid presence reached delivery."), ct);
        if (invalid is "membership" or "device") await Assert.ThrowsAsync<AccessDeniedException>(Send);
        else await Assert.ThrowsAsync<PushRejectedException>(Send);
        Assert.Equal(0, transactions.Started);
    }

    private sealed class TransactionCounter : DbTransactionInterceptor
    {
        public int Started { get; private set; }

        public override ValueTask<InterceptionResult<DbTransaction>> TransactionStartingAsync(DbConnection connection,
            TransactionStartingEventData eventData, InterceptionResult<DbTransaction> result, CancellationToken cancellationToken = default)
        {
            Started++;
            return ValueTask.FromResult(result);
        }
    }
}
