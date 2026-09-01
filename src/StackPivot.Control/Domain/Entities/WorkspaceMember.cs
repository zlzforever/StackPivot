using StackPivot.Control.Authorization;

namespace StackPivot.Control.Domain.Entities;

public sealed class WorkspaceMember
{
    public Guid Id { get; set; }
    public Guid WorkspaceId { get; set; }
    public Guid UserId { get; set; }
    public WorkspacePermission Permission { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    public Workspace? Workspace { get; set; }
    public UserAccount? User { get; set; }
}
