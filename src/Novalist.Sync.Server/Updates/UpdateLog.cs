using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;
using Novalist.Sync.Contracts;
using Novalist.Sync.Server.Data;
using Npgsql;

namespace Novalist.Sync.Server.Updates;

public sealed class UpdateLog(SyncDbContext db)
{
    private const string SenderSeqIndex = "ix_updates_project_id_sender_device_id_sender_seq";

    public async Task<long> AppendAsync(Guid userId, Envelope envelope, CancellationToken cancellationToken)
    {
        var header = EnvelopeHeader.Parse(envelope.Header);

        if (header.MsgType != MsgType.Update)
        {
            throw new PushRejectedException($"Expected msgType {MsgType.Update}, got {header.MsgType}.");
        }

        if (header.SenderSeq > long.MaxValue)
        {
            throw new PushRejectedException($"SenderSeq {header.SenderSeq} is out of range.");
        }

        var senderSeq = (long)header.SenderSeq;
        long epoch = header.Epoch;

        var owner = (int)MembershipRole.Owner;
        var contributor = (int)MembershipRole.Contributor;

        // One statement: bumping the project's counter and inserting the update
        // either both happen or neither does, so serverSeq has no gaps. The UPDATE
        // locks the project row, which queues concurrent pushes to the same project.
        // Don't compose further LINQ onto this query (e.g. SingleAsync): EF would then
        // wrap the INSERT in a subquery, which PostgreSQL rejects.
        try
        {
            var assigned = await db.Database
                .SqlQuery<long>($"""
                                 WITH writer AS (
                                    SELECT 1
                                    FROM memberships
                                    WHERE project_id = {header.ProjectId}
                                        AND user_id = {userId}
                                        AND role IN ({owner}, {contributor})
                                    FOR SHARE
                                 ),
                                 seq AS (
                                    UPDATE projects
                                    SET last_server_seq = last_server_seq + 1
                                    WHERE id = {header.ProjectId} AND EXISTS (SELECT 1 FROM writer)
                                    RETURNING last_server_seq
                                 )
                                 INSERT INTO updates (
                                    project_id, server_seq, item_id, epoch, sender_device_id, sender_seq,
                                    header, nonce, ciphertext, signature, received_at)
                                 SELECT
                                    {header.ProjectId}, seq.last_server_seq, {header.ItemId}, {epoch},
                                    {header.SenderDeviceId}, {senderSeq},
                                    {envelope.Header}, {envelope.Nonce}, {envelope.Ciphertext}, {envelope.Signature}, now()
                                 FROM seq
                                 RETURNING server_seq AS "Value"
                                 """).ToListAsync(cancellationToken);
            return assigned.Count == 1
                ? assigned[0]
                : throw new PushRejectedException($"No write access to project {header.ProjectId}.");
        }
        catch (PostgresException ex) when (ex is { SqlState: PostgresErrorCodes.UniqueViolation, ConstraintName: SenderSeqIndex })
        {
            return await ResolveDuplicateAsync(header, senderSeq, envelope, cancellationToken);
        }


    }

    private async Task<long> ResolveDuplicateAsync(EnvelopeHeader header, long senderSeq, Envelope envelope,
        CancellationToken cancellationToken)
    {
        var existing = await db.Updates
            .AsNoTracking()
            .Where(u => u.ProjectId == header.ProjectId
                        && u.SenderDeviceId == header.SenderDeviceId
                        && u.SenderSeq == senderSeq)
            .Select(u => new { u.ServerSeq, u.Header, u.Nonce, u.Ciphertext, u.Signature })
            .SingleAsync(cancellationToken);

        var identical = existing.Header.AsSpan().SequenceEqual(envelope.Header)
                        && existing.Nonce.AsSpan().SequenceEqual(envelope.Nonce)
                        && existing.Ciphertext.AsSpan().SequenceEqual(envelope.Ciphertext)
                        && existing.Signature.AsSpan().SequenceEqual(envelope.Signature);

        return identical
            ? existing.ServerSeq
            : throw new PushRejectedException(
                $"senderSeq {senderSeq} of device {header.SenderDeviceId} was already used for a different update.");
    }

    public async IAsyncEnumerable<SequencedEnvelope> ReadAfterAsync(
        Guid projectId,
        long afterServerSeq,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var updates = db.Updates
            .AsNoTracking()
            .Where(u => u.ProjectId == projectId && u.ServerSeq > afterServerSeq)
            .OrderBy(u => u.ServerSeq)
            .Select(u => new SequencedEnvelope(u.ServerSeq, new Envelope(u.Header, u.Nonce, u.Ciphertext, u.Signature)))
            .AsAsyncEnumerable();

        await foreach (var update in updates.WithCancellation(cancellationToken))
        {
            yield return update;
        }
    }
}