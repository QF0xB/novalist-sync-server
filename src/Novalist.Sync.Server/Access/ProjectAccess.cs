using Microsoft.EntityFrameworkCore;
using Novalist.Sync.Server.Data;
using Npgsql;

namespace Novalist.Sync.Server.Access;

public sealed class ProjectAccess(SyncDbContext db)
{
    private const string ProjectPrimaryKey = "pk_projects";

    public async Task CreateAsync(Guid userId, Guid projectId, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        db.Projects.Add(new Project { Id = projectId, EpochStartedAt = now, CreatedAt = now });
        db.Memberships.Add(new Membership { ProjectId = projectId, UserId = userId, Role = MembershipRole.Owner });

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException
        {
            SqlState: PostgresErrorCodes.UniqueViolation,
            ConstraintName: ProjectPrimaryKey
        })
        {
            db.ChangeTracker.Clear();

            // A retried CreateProject from the owner is fine; anyone else gets a conflict.
            if (await GetRoleAsync(userId, projectId, cancellationToken) != MembershipRole.Owner)
            {
                throw new ConflictException($"Project ID {projectId} is already taken.");
            }
        }
    }

    public async Task EnsureCanReadAsync(Guid userId, Guid projectId, CancellationToken cancellationToken)
    {
        if (await GetRoleAsync(userId, projectId, cancellationToken) is null)
        {
            throw new AccessDeniedException($"User {userId} is not a member of project {projectId}.");
        }
    }

    public async Task EnsureCanWriteAsync(Guid userId, Guid projectId, CancellationToken cancellationToken)
    {
        var role = await GetRoleAsync(userId, projectId, cancellationToken);

        // Only allow OWNER and CONTRIBUTOR write access
        if (role is not (MembershipRole.Owner or MembershipRole.Contributor))
        {
            throw new AccessDeniedException($"User {userId} does not have write access to project {projectId}.");
        }
    }

    public async Task EnsureIsOwnerAsync(Guid userId, Guid projectId, CancellationToken cancellationToken)
    {
        var role = await GetRoleAsync(userId, projectId, cancellationToken);

        // Only allow OWNER permission to add/modify other users membership
        if (role is not MembershipRole.Owner)
        {
            throw new AccessDeniedException($"User {userId} does not have modify access to project {projectId}.");
        }
    }

    public async Task RemoveMemberAsync(Guid ownerId, Guid projectId, Guid memberId,
        CancellationToken cancellationToken)
    {
        await EnsureIsOwnerAsync(ownerId, projectId, cancellationToken);

        if (memberId == ownerId)
        {
            throw new ConflictException($"Cannot remove owner from project {projectId}.");
        }

        await db.Memberships
            .Where(m => m.ProjectId == projectId && m.UserId == memberId)
            .ExecuteDeleteAsync(cancellationToken);
    }

    private Task<MembershipRole?> GetRoleAsync(Guid userId, Guid projectId, CancellationToken cancellationToken) =>
        db.Memberships
            .Where(m => m.ProjectId == projectId && m.UserId == userId)
            .Select(m => (MembershipRole?)m.Role)
            .SingleOrDefaultAsync(cancellationToken);
}