using System.Data;
using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Novalist.Sync.Server.Tests.Infrastructure;

internal sealed class DatabaseTimeOverride(DateTimeOffset now) : DbCommandInterceptor
{
    public DateTimeOffset Now { get; set; } = now;

    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData,
        InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
    {
        if (command.CommandText.Contains("clock_timestamp()", StringComparison.Ordinal))
        {
            command.CommandText = command.CommandText.Replace("clock_timestamp()", "@test_database_time", StringComparison.Ordinal);
            var time = command.CreateParameter();
            time.ParameterName = "test_database_time";
            time.DbType = DbType.DateTimeOffset;
            time.Value = Now;
            command.Parameters.Add(time);
        }
        return ValueTask.FromResult(result);
    }
}
