namespace StackPivot.Control.Domain.Entities;

public sealed class Workspace
{
    public Guid WorkspaceId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }

    public ICollection<Stack> Stacks { get; } = new List<Stack>();
    public ICollection<WorkspaceMember> Members { get; } = new List<WorkspaceMember>();
}
