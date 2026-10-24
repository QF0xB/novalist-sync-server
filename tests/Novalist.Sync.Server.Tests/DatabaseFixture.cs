using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Novalist.Sync.Server.Data;
using Novalist.Sync.Server.Access;
using Npgsql;

[assembly: AssemblyFixture(typeof(Novalist.Sync.Server.Tests.DatabaseFixture))]

namespace Novalist.Sync.Server.Tests;

// One fresh database per test run, migrated like production and dropped at the end.
// Tests stay independent by creating their own projects with random IDs.
public sealed class DatabaseFixture : IAsyncLifetime
{
    public AccessDeliveryGate DeliveryGate { get; } = new();

    public string ConnectionString { get; } = new NpgsqlConnectionStringBuilder(
        Environment.GetEnvironmentVariable("ConnectionStrings__Default")
        ?? throw new InvalidOperationException("ConnectionStrings__Default is not set. Run the tests inside `devenv shell`."))
    {
        Database = $"novalist_sync_test_{Guid.NewGuid():N}",
    }.ConnectionString;

    // Must match the configuration in Program.cs.
    public SyncDbContext CreateContext(params IInterceptor[] interceptors) =>
        new(new DbContextOptionsBuilder<SyncDbContext>()
            .UseNpgsql(ConnectionString)
            .UseSnakeCaseNamingConvention()
            .AddInterceptors(interceptors)
            .Options);

    public async ValueTask InitializeAsync()
    {
        await using var db = CreateContext();
        await db.Database.MigrateAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await using var db = CreateContext();
        await db.Database.EnsureDeletedAsync();
    }
}
