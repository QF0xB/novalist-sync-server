using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Novalist.Sync.Server.Data;
using Npgsql;

namespace Novalist.Sync.Server.Access;

public sealed class UserDirectory(SyncDbContext db)
{
    private const string IssuerSubjectIndex = "ix_users_issuer_subject";

    public async Task<User> GetOrCreateAsync(ClaimsPrincipal principal, CancellationToken cancellationToken)
    {
        var issuer = principal.FindFirstValue("iss")
                     ?? throw new AccessDeniedException("The token has no iss claim.");
        var subject = principal.FindFirstValue("sub")
                      ?? throw new AccessDeniedException("The token has no sub claim.");

        var user = await db.Users
            .SingleOrDefaultAsync(u => u.Issuer == issuer && u.Subject == subject, cancellationToken);

        if (user is null)
        {
            user = new User
            {
                Id = Guid.CreateVersion7(),
                Issuer = issuer,
                Subject = subject,
                CreatedAt = DateTimeOffset.UtcNow,
            };

            db.Users.Add(user);
        }

        RefreshProfile(user, principal);

        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return user;
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException
        {
            SqlState: PostgresErrorCodes.UniqueViolation,
            ConstraintName: IssuerSubjectIndex
        })
        {
            // Two connections of a new user raced to create it; the other one won.
            db.ChangeTracker.Clear();
            var existing = await db.Users.SingleAsync(u => u.Issuer == issuer && u.Subject == subject, cancellationToken);
            RefreshProfile(existing, principal);
            await db.SaveChangesAsync(cancellationToken);
            return existing;
        }
    }


    // Unverified emails are not stored: they will be used to find people for
    // invites, and anyone can put someone else's email into their profile.
    private static void RefreshProfile(User user, ClaimsPrincipal principal)
    {
        user.Email = principal.FindFirstValue("email_verified") == "true"
            ? principal.FindFirstValue("email")
            : null;

        user.UserName = principal.FindFirstValue("preferred_username");
    }

}