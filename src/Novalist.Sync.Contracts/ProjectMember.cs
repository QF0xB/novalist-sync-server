namespace Novalist.Sync.Contracts;

public sealed record ProjectMember(Guid UserId, string DisplayName, string Role);

public static class ProjectRoles
{
    public const string Owner = "Owner";
    public const string Contributor = "Contributor";
    public const string Viewer = "Viewer";
}
