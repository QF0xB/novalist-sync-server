namespace Novalist.Sync.Contracts;

public interface ISyncHub
{
    Task JoinProject(Guid projectId);

    Task<long> Push(Envelope envelope);

    IAsyncEnumerable<SequencedEnvelope> CatchUp(
        Guid projectId,
        long afterServerSeq,
        CancellationToken cancellationToken);
}