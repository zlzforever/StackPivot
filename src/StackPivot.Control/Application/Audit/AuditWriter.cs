using StackPivot.Control.Domain.Entities;
using StackPivot.Control.Infrastructure.Persistence;

namespace StackPivot.Control.Application.Audit;

public static class AuditActions
{
    public const string DeployRequested = "deploy_requested";
    public const string TaskDispatched = "task_dispatched";
    public const string TaskAccepted = "task_accepted";
    public const string TaskSucceeded = "task_succeeded";
    public const string TaskFailed = "task_failed";
    public const string AgentConnected = "agent_connected";
    public const string AgentDisconnected = "agent_disconnected";
    public const string AgentKeyCreated = "agent_key_created";
    public const string AgentKeyRotated = "agent_key_rotated";
    public const string AgentKeyRevoked = "agent_key_revoked";
}

public sealed class AuditWriter(StackPivotDbContext dbContext)
{
    public AuditLog Add(
        string action,
        Guid? requestId,
        Guid? actorUserId,
        Guid? agentId,
        string resourceType,
        string resourceId,
        string result,
        string? errorCode = null,
        string? remoteIp = null)
    {
        var audit = new AuditLog
        {
            AuditId = Guid.NewGuid(),
            Action = action,
            RequestId = requestId,
            ActorUserId = actorUserId,
            AgentId = agentId,
            ResourceType = resourceType,
            ResourceId = resourceId,
            Result = result,
            ErrorCode = errorCode,
            CreatedAt = DateTimeOffset.UtcNow,
            RemoteIp = remoteIp
        };
        dbContext.AuditLogs.Add(audit);
        return audit;
    }
}
