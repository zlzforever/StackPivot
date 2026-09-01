namespace StackPivot.Control.Domain.Entities;

public sealed class StackAgentBinding
{
    public Guid Id { get; set; }
    public Guid StackId { get; set; }
    public Guid AgentId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    public Stack? Stack { get; set; }
    public AgentNode? Agent { get; set; }
}
