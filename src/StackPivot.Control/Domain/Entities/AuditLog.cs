namespace StackPivot.Control.Domain.Entities;

public sealed class AuditLog
{
    public Guid AuditId { get; set; }
    public Guid? RequestId { get; set; }
    public Guid? ActorUserId { get; set; }
    public Guid? AgentId { get; set; }
    public string Action { get; set; } = string.Empty;
    public string ResourceType { get; set; } = string.Empty;
    public string ResourceId { get; set; } = string.Empty;
    public string Result { get; set; } = string.Empty;
    public string? ErrorCode { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public string? RemoteIp { get; set; }
}
