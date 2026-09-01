namespace StackPivot.Control.Domain.Entities;

public sealed class AgentNode
{
    public Guid AgentId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Remark { get; set; } = string.Empty;
    public string ApiKeyHash { get; set; } = string.Empty;
    public int ApiKeyVersion { get; set; }
    public string? ApiKeyLast4 { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
    public DateTimeOffset? LastSeenAt { get; set; }
    public string CapabilitiesJson { get; set; } = "[]";

    public ICollection<StackAgentBinding> StackBindings { get; } = new List<StackAgentBinding>();
}
