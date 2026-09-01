namespace StackPivot.Control.Domain.Entities;

public sealed class UserAccount
{
    public Guid UserId { get; set; }
    public string UserName { get; set; } = string.Empty;
    public string SsoSubject { get; set; } = string.Empty;
    public bool IsPlatformAdmin { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    public ICollection<WorkspaceMember> WorkspaceMembers { get; } = new List<WorkspaceMember>();
}
