using Novalist.Sync.Contracts;

namespace Novalist.Sync.Server.Access;

public sealed class DeviceProofServer(IConfiguration configuration)
{
    public string ServerUrl => DeviceProof.CanonicalServerUrl(configuration["Collaboration:ServerUrl"]
        ?? throw new InvalidOperationException("Configure Collaboration:ServerUrl before enabling verified devices."));
}
