using Microsoft.AspNetCore.SignalR;
using Novalist.Sync.Server.Access;
using Novalist.Sync.Server.Updates;

namespace Novalist.Sync.Server.Hubs;

public sealed class ClientErrorFilter : IHubFilter
{
    public static bool IsClientError(Exception exception) =>
        exception is AccessDeniedException or ConflictException or PushRejectedException or FormatException;

    public async ValueTask<object?> InvokeMethodAsync(HubInvocationContext invocationContext, Func<HubInvocationContext, ValueTask<object?>> next)
    {
        try
        {
            return await next(invocationContext);
        }
        catch (Exception ex) when (IsClientError(ex))
        {
            throw new HubException(ex.Message);
        }
    }

    public async Task OnConnectedAsync(HubLifetimeContext context, Func<HubLifetimeContext, Task> next)
    {
        try
        {
            await next(context);
        }
        catch (Exception ex) when (IsClientError(ex))
        {
            throw new HubException(ex.Message);
        }
    }
}