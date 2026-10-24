namespace Novalist.Sync.Contracts;

public interface ISyncClient
{
    Task ProjectAccessEnded(Guid projectId);
    Task ReceiveUpdate(SequencedEnvelope envelope);
}
