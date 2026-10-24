using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Novalist.Sync.Server.Tests.Infrastructure;

internal sealed class SqlCommandPause(string sql) : DbCommandInterceptor
{
    public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private async Task PauseAsync(DbCommand command, CancellationToken ct)
    {
        if (!command.CommandText.Contains(sql, StringComparison.Ordinal)) return;
        Entered.TrySetResult();
        await Release.Task.WaitAsync(ct);
    }

    public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
        CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
    {
        await PauseAsync(command, cancellationToken);
        return result;
    }

    public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command,
        CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        await PauseAsync(command, cancellationToken);
        return result;
    }
}
