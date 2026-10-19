using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Novalist.Sync.Contracts;
using Novalist.Sync.Contracts.Exceptions;
using Novalist.Sync.Server.Data;
using Novalist.Sync.Server.Updates;

namespace Novalist.Sync.Server.Tests;

public class UpdateLogTests(DatabaseFixture database)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Append_AssignsServerSeqStartingAtOne()
    {
        var (projectId, deviceId) = await SeedProjectAsync();
        await using var db = database.CreateContext();
        var log = new UpdateLog(db);

        Assert.Equal(1, await log.AppendAsync(CreateEnvelope(projectId, deviceId, senderSeq: 1), Ct));
        Assert.Equal(2, await log.AppendAsync(CreateEnvelope(projectId, deviceId, senderSeq: 2), Ct));
        Assert.Equal(3, await log.AppendAsync(CreateEnvelope(projectId, deviceId, senderSeq: 3), Ct));
    }

    [Fact]
    public async Task Append_ConcurrentPushes_AreGapFreeAndStrictlyIncreasing()
    {
        const int pushes = 50;
        var (projectId, deviceId) = await SeedProjectAsync();

        // Each push gets its own DbContext (and connection), like separate hub calls.
        var assigned = await Task.WhenAll(Enumerable.Range(1, pushes).Select(async i =>
        {
            await using var db = database.CreateContext();
            return await new UpdateLog(db).AppendAsync(CreateEnvelope(projectId, deviceId, (ulong)i), Ct);
        }));

        var expected = Enumerable.Range(1, pushes).Select(i => (long)i).ToList();
        Assert.Equal(expected, assigned.Order());

        await using var check = database.CreateContext();
        var stored = await check.Updates
            .Where(u => u.ProjectId == projectId)
            .OrderBy(u => u.ServerSeq)
            .Select(u => u.ServerSeq)
            .ToListAsync(Ct);
        Assert.Equal(expected, stored);
        Assert.Equal(pushes, await check.Projects.Where(p => p.Id == projectId).Select(p => p.LastServerSeq).SingleAsync(Ct));
    }

    [Fact]
    public async Task Append_ProjectsCountIndependently()
    {
        var (projectA, deviceA) = await SeedProjectAsync();
        var (projectB, deviceB) = await SeedProjectAsync();
        await using var db = database.CreateContext();
        var log = new UpdateLog(db);

        Assert.Equal(1, await log.AppendAsync(CreateEnvelope(projectA, deviceA, senderSeq: 1), Ct));
        Assert.Equal(1, await log.AppendAsync(CreateEnvelope(projectB, deviceB, senderSeq: 1), Ct));
        Assert.Equal(2, await log.AppendAsync(CreateEnvelope(projectA, deviceA, senderSeq: 2), Ct));
    }

    [Fact]
    public async Task Append_RetriedPush_ReturnsSameServerSeqWithoutGap()
    {
        var (projectId, deviceId) = await SeedProjectAsync();
        await using var db = database.CreateContext();
        var log = new UpdateLog(db);
        var envelope = CreateEnvelope(projectId, deviceId, senderSeq: 1);

        Assert.Equal(1, await log.AppendAsync(envelope, Ct));
        Assert.Equal(1, await log.AppendAsync(envelope, Ct));
        Assert.Equal(2, await log.AppendAsync(CreateEnvelope(projectId, deviceId, senderSeq: 2), Ct));
    }

    [Fact]
    public async Task Append_ReusedSenderSeqWithDifferentBytes_IsRejectedWithoutGap()
    {
        var (projectId, deviceId) = await SeedProjectAsync();
        await using var db = database.CreateContext();
        var log = new UpdateLog(db);

        await log.AppendAsync(CreateEnvelope(projectId, deviceId, senderSeq: 1), Ct);

        await Assert.ThrowsAsync<PushRejectedException>(
            () => log.AppendAsync(CreateEnvelope(projectId, deviceId, senderSeq: 1), Ct));
        Assert.Equal(2, await log.AppendAsync(CreateEnvelope(projectId, deviceId, senderSeq: 2), Ct));
    }

    [Fact]
    public async Task Append_UnknownProject_IsRejected()
    {
        var (_, deviceId) = await SeedProjectAsync();
        await using var db = database.CreateContext();

        await Assert.ThrowsAsync<PushRejectedException>(
            () => new UpdateLog(db).AppendAsync(CreateEnvelope(Guid.NewGuid(), deviceId, senderSeq: 1), Ct));
    }

    [Theory]
    [InlineData(MsgType.Snapshot)]
    [InlineData(MsgType.Presence)]
    public async Task Append_NonUpdateMsgType_IsRejected(MsgType msgType)
    {
        var (projectId, deviceId) = await SeedProjectAsync();
        await using var db = database.CreateContext();

        await Assert.ThrowsAsync<PushRejectedException>(
            () => new UpdateLog(db).AppendAsync(CreateEnvelope(projectId, deviceId, senderSeq: 1, msgType), Ct));
    }

    [Fact]
    public async Task Append_SenderSeqAboveLongMaxValue_IsRejected()
    {
        var (projectId, deviceId) = await SeedProjectAsync();
        await using var db = database.CreateContext();

        await Assert.ThrowsAsync<PushRejectedException>(
            () => new UpdateLog(db).AppendAsync(CreateEnvelope(projectId, deviceId, (ulong)long.MaxValue + 1), Ct));
    }

    [Fact]
    public async Task Append_UnsupportedProtoVersion_IsRejected()
    {
        var (projectId, deviceId) = await SeedProjectAsync();
        await using var db = database.CreateContext();
        var envelope = CreateEnvelope(projectId, deviceId, senderSeq: 1);
        envelope.Header[0] = Protocol.Version + 1;

        await Assert.ThrowsAsync<UnsupportedProtocolVersionException>(
            () => new UpdateLog(db).AppendAsync(envelope, Ct));
    }

    [Fact]
    public async Task ReadAfter_ReturnsLaterUpdatesInOrderWithUnchangedBytes()
    {
        var (projectId, deviceId) = await SeedProjectAsync();
        await using var db = database.CreateContext();
        var log = new UpdateLog(db);
        var envelopes = Enumerable.Range(1, 5).Select(i => CreateEnvelope(projectId, deviceId, (ulong)i)).ToList();
        foreach (var envelope in envelopes)
        {
            await log.AppendAsync(envelope, Ct);
        }

        var read = await log.ReadAfterAsync(projectId, afterServerSeq: 2, Ct).ToListAsync(Ct);

        Assert.Equal([3L, 4L, 5L], read.Select(u => u.ServerSeq));
        for (var i = 0; i < read.Count; i++)
        {
            var sent = envelopes[i + 2];
            Assert.Equal(sent.Header, read[i].Envelope.Header);
            Assert.Equal(sent.Nonce, read[i].Envelope.Nonce);
            Assert.Equal(sent.Ciphertext, read[i].Envelope.Ciphertext);
            Assert.Equal(sent.Signature, read[i].Envelope.Signature);
        }
    }

    [Fact]
    public async Task ReadAfter_OnlyReturnsUpdatesOfTheRequestedProject()
    {
        var (projectA, deviceA) = await SeedProjectAsync();
        var (projectB, deviceB) = await SeedProjectAsync();
        await using var db = database.CreateContext();
        var log = new UpdateLog(db);
        await log.AppendAsync(CreateEnvelope(projectA, deviceA, senderSeq: 1), Ct);
        await log.AppendAsync(CreateEnvelope(projectB, deviceB, senderSeq: 1), Ct);

        var read = await log.ReadAfterAsync(projectA, afterServerSeq: 0, Ct).ToListAsync(Ct);

        var update = Assert.Single(read);
        Assert.Equal(projectA, EnvelopeHeader.Parse(update.Envelope.Header).ProjectId);
    }

    private async Task<(Guid ProjectId, Guid DeviceId)> SeedProjectAsync()
    {
        var now = DateTimeOffset.UtcNow;
        var user = new User
        {
            Id = Guid.CreateVersion7(),
            Issuer = "https://idp.test",
            Subject = Guid.NewGuid().ToString(),
            CreatedAt = now,
        };
        var device = new Device { Id = Guid.NewGuid(), UserId = user.Id, CreatedAt = now };
        var project = new Project { Id = Guid.NewGuid(), EpochStartedAt = now, CreatedAt = now };
        var membership = new Membership { ProjectId = project.Id, UserId = user.Id, Role = MembershipRole.Owner };

        await using var db = database.CreateContext();
        db.AddRange(user, device, project, membership);
        await db.SaveChangesAsync(Ct);

        return (project.Id, device.Id);
    }

    // Random bytes as "ciphertext": the server must behave the same for any payload.
    private static Envelope CreateEnvelope(Guid projectId, Guid deviceId, ulong senderSeq, MsgType msgType = MsgType.Update) =>
        new(
            new EnvelopeHeader(Protocol.Version, msgType, projectId, Guid.NewGuid(), Epoch: 0, deviceId, senderSeq).ToBytes(),
            RandomNumberGenerator.GetBytes(24),
            RandomNumberGenerator.GetBytes(RandomNumberGenerator.GetInt32(1, 512)),
            RandomNumberGenerator.GetBytes(64));
}
