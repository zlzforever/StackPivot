namespace StackPivot.Control.Domain.Entities;

public sealed class Stack
{
    public Guid StackId { get; set; }
    public Guid WorkspaceId { get; set; }
    public string FolderName { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }

    public Workspace? Workspace { get; set; }
    public ICollection<StackAgentBinding> AgentBindings { get; } = new List<StackAgentBinding>();
    public ICollection<ServiceOperationHistory> Operations { get; } = new List<ServiceOperationHistory>();
}
