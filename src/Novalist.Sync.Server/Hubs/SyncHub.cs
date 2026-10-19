using System.Runtime.CompilerServices;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Novalist.Sync.Contracts;
using Novalist.Sync.Server.Access;
using Novalist.Sync.Server.Updates;

namespace Novalist.Sync.Server.Hubs;

[Authorize]
public sealed class SyncHub(
    UserDirectory users,
    DeviceRegistry devices,
    ProjectAccess projects,
    UpdateLog updateLog,
    ConnectionRegistry connections,
    ILogger<SyncHub> logger) : Hub<ISyncClient>, ISyncHub
{
    private const string UserIdItem = "userId";
    private const string DeviceIdItem = "deviceId";

    private Guid UserId => (Guid)Context.Items[UserIdItem]!;
    private Guid DeviceId => (Guid)Context.Items[DeviceIdItem]!;

    public override async Task OnConnectedAsync()
    {
        var cancellationToken = Context.ConnectionAborted;
        var deviceId = ReadDeviceId();
        var user = await users.GetOrCreateAsync(Context.User!, cancellationToken);

        // Register before checking, so a revocation that lands in between still finds
        // this connection. If the check fails, the connection never counts as open.
        connections.Add(Context, user.Id, deviceId);

        try
        {
            await devices.EnsureActiveAsync(user.Id, deviceId, cancellationToken);
        }
        catch
        {
            connections.Remove(Context.ConnectionId);
            throw;
        }

        Context.Items[UserIdItem] = user.Id;
        Context.Items[DeviceIdItem] = deviceId;

        logger.LogInformation("Device {DeviceId} of user {UserId} connected.", deviceId, user.Id);
        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        connections.Remove(Context.ConnectionId);
        await base.OnDisconnectedAsync(exception);
    }

    public async Task CreateProject(Guid projectId)
    {
        var cancellationToken = Context.ConnectionAborted;
        await EnsureDeviceActiveAsync(cancellationToken);

        await projects.CreateAsync(UserId, projectId, cancellationToken);

        logger.LogInformation("User {UserId} created project {ProjectId}.", UserId, projectId);
    }

    public async Task JoinProject(Guid projectId)
    {
        var cancellationToken = Context.ConnectionAborted;
        await EnsureDeviceActiveAsync(cancellationToken);
        await projects.EnsureCanReadAsync(UserId, projectId, cancellationToken);

        await Groups.AddToGroupAsync(Context.ConnectionId, ProjectGroup(projectId), cancellationToken);

        // A removal that ran between the check and joining the group could not see
        // this connection in the group yet; check again so it cannot slip through
        try
        {
            await projects.EnsureCanReadAsync(UserId, projectId, cancellationToken);
        }
        catch
        {
            await Groups.RemoveFromGroupAsync(Context.ConnectionId, ProjectGroup(projectId), cancellationToken);
            throw;
        }
    }

    public Task LeaveProject(Guid projectId) => Groups.RemoveFromGroupAsync(Context.ConnectionId, ProjectGroup(projectId), Context.ConnectionAborted);

    public async Task<long> Push(Envelope envelope)
    {
        var cancellationToken = Context.ConnectionAborted;
        var header = EnvelopeHeader.Parse(envelope.Header);

        if (header.SenderDeviceId != DeviceId)
        {
            throw new PushRejectedException($"Sender device {header.SenderDeviceId} is not this device {DeviceId} of the connection.");
        }

        await EnsureDeviceActiveAsync(cancellationToken);
        await projects.EnsureCanWriteAsync(UserId, header.ProjectId, cancellationToken);

        var serverSeq = await updateLog.AppendAsync(envelope, cancellationToken);

        await Clients.OthersInGroup(ProjectGroup(header.ProjectId))
            .ReceiveUpdate(new SequencedEnvelope(serverSeq, envelope));

        logger.LogInformation(
            "Device {DeviceId} of user {UserId} pushed update {ServerSeq} to project {ProjectId} ({Size} bytes).",
            DeviceId, UserId, serverSeq, header.ProjectId, envelope.Ciphertext.Length);

        return serverSeq;
    }

    public async IAsyncEnumerable<SequencedEnvelope> CatchUp(Guid projectId, long afterServerSeq, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        try
        {
            await EnsureDeviceActiveAsync(cancellationToken);
            await projects.EnsureCanReadAsync(UserId, projectId, cancellationToken);
        }
        catch (Exception ex) when (ClientErrorFilter.IsClientError(ex))
        {
            // Hub filters don't see exceptions thrown while a stream is enumerated
            throw new HubException(ex.Message);
        }

        await foreach (var update in updateLog.ReadAfterAsync(projectId, afterServerSeq, cancellationToken))
        {
            yield return update;
        }
    }

    private Guid ReadDeviceId()
    {
        var value = Context.GetHttpContext()?.Request.Query[Protocol.DeviceIdParameter].ToString();

        return Guid.TryParse(value, out var deviceId)
            ? deviceId
            : throw new HubException($"Connect with ?{Protocol.DeviceIdParameter}=<guid>.");
    }

    private Task EnsureDeviceActiveAsync(CancellationToken cancellationToken) => devices.EnsureActiveAsync(UserId, DeviceId, cancellationToken);

    internal static string ProjectGroup(Guid projectId) => $"project:{projectId}";
}