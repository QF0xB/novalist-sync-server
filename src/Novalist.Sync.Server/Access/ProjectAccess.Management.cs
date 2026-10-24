using Microsoft.EntityFrameworkCore;
using Novalist.Sync.Contracts;
using Novalist.Sync.Server.Data;

namespace Novalist.Sync.Server.Access;

public sealed partial class ProjectAccess
{
    public async Task<IReadOnlyList<ProjectMember>> ListMembersAsync(Guid ownerId, Guid projectId, CancellationToken ct)
    {
        await EnsureIsOwnerAsync(ownerId, projectId, ct);
        var members = await db.Memberships.Where(member => member.ProjectId == projectId)
            .Join(db.Users, member => member.UserId, user => user.Id,
                (member, user) => new { user.Id, Name = user.UserName ?? user.Email ?? user.Id.ToString(), member.Role })
            .ToListAsync(ct);
        return members.Select(member => new ProjectMember(member.Id, member.Name, member.Role switch
        {
            MembershipRole.Owner => ProjectRoles.Owner,
            MembershipRole.Contributor => ProjectRoles.Contributor,
            MembershipRole.Viewer => ProjectRoles.Viewer,
            _ => throw new InvalidOperationException("Unsupported stored project role.")
        })).ToArray();
    }

    public async Task<IReadOnlyList<Guid>> DeleteAsync(Guid ownerId, Guid projectId, CancellationToken ct)
    {
        await EnsureIsOwnerAsync(ownerId, projectId, ct);
        await using var delivery = await PauseDeliveryAsync(projectId, ct);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await db.Projects.FromSqlInterpolated($"SELECT * FROM projects WHERE id = {projectId} FOR UPDATE").ToListAsync(ct);
        await EnsureIsOwnerAsync(ownerId, projectId, ct);
        var users = await db.Memberships.Where(member => member.ProjectId == projectId).Select(member => member.UserId).ToListAsync(ct);
        await db.Invitations.Where(invitation => invitation.ProjectId == projectId).ExecuteDeleteAsync(ct);
        await db.Updates.Where(update => update.ProjectId == projectId).ExecuteDeleteAsync(ct);
        await db.Memberships.Where(member => member.ProjectId == projectId).ExecuteDeleteAsync(ct);
        await transaction.CommitAsync(ct);
        return users;
    }
}
