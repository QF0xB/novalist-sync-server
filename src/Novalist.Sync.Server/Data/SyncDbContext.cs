using Microsoft.EntityFrameworkCore;

namespace Novalist.Sync.Server.Data;

public sealed class SyncDbContext(DbContextOptions<SyncDbContext> options) : DbContext(options)
{
    public DbSet<User> Users => Set<User>();
    public DbSet<Device> Devices => Set<Device>();
    public DbSet<Project> Projects => Set<Project>();
    public DbSet<Membership> Memberships => Set<Membership>();
    public DbSet<StoredUpdate> Updates => Set<StoredUpdate>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<User>(user =>
        {
            user.Property(u => u.Id).ValueGeneratedNever();
            user.HasIndex(u => new { u.Issuer, u.Subject }).IsUnique();
        });

        modelBuilder.Entity<Device>(device =>
        {
            device.Property(d => d.Id).ValueGeneratedNever();
            device.HasOne<User>().WithMany().HasForeignKey(d => d.UserId);
        });

        modelBuilder.Entity<Project>(project =>
        {
            project.Property(p => p.Id).ValueGeneratedNever();
        });

        modelBuilder.Entity<Membership>(membership =>
        {
            membership.HasKey(m => new { m.ProjectId, m.UserId });
            membership.HasOne<Project>().WithMany().HasForeignKey(m => m.ProjectId);
            membership.HasOne<User>().WithMany().HasForeignKey(m => m.UserId);
        });

        modelBuilder.Entity<StoredUpdate>(update =>
        {
            update.ToTable("updates");
            update.HasKey(u => new { u.ProjectId, u.ServerSeq });
            update.HasIndex(u => new { u.ProjectId, u.SenderDeviceId, u.SenderSeq }).IsUnique();
            update.HasOne<Project>().WithMany().HasForeignKey(u => u.ProjectId);
            update.HasOne<Device>().WithMany().HasForeignKey(u => u.SenderDeviceId)
                .OnDelete(DeleteBehavior.Restrict);
        });
    }
}