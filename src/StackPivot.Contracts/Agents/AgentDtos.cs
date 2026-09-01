using System.Text.Json.Serialization;
using StackPivot.Contracts.SignalR;

namespace StackPivot.Contracts.Agents;

public sealed record AgentHello(
    [property: JsonPropertyName("schemaVersion")] int SchemaVersion,
    [property: JsonPropertyName("agentId")] Guid AgentId,
    [property: JsonPropertyName("agentVersion")] string AgentVersion,
    [property: JsonPropertyName("os")] string Os,
    [property: JsonPropertyName("composeMajorVersion")] int ComposeMajorVersion,
    [property: JsonPropertyName("capabilities")] IReadOnlyList<string> Capabilities,
    [property: JsonPropertyName("sentAt")] DateTimeOffset SentAt) : IProtocolMessage
{
    public AgentHello(
        Guid agentId,
        string agentVersion,
        string os,
        int composeMajorVersion,
        IReadOnlyList<string> capabilities,
        DateTimeOffset sentAt)
        : this(ProtocolVersion.Current, agentId, agentVersion, os, composeMajorVersion, capabilities, sentAt)
    {
    }
}

public sealed record AgentHelloAck(
    [property: JsonPropertyName("schemaVersion")] int SchemaVersion,
    [property: JsonPropertyName("accepted")] bool Accepted,
    [property: JsonPropertyName("serverTime")] DateTimeOffset ServerTime,
    [property: JsonPropertyName("heartbeatIntervalSeconds")] int HeartbeatIntervalSeconds,
    [property: JsonPropertyName("errorCode")] string? ErrorCode = null) : IProtocolMessage;

public sealed record TaskAccepted(
    [property: JsonPropertyName("schemaVersion")] int SchemaVersion,
    [property: JsonPropertyName("taskId")] Guid TaskId,
    [property: JsonPropertyName("agentId")] Guid AgentId,
    [property: JsonPropertyName("acceptedAt")] DateTimeOffset AcceptedAt) : IProtocolMessage;

public sealed record TaskLog(
    [property: JsonPropertyName("schemaVersion")] int SchemaVersion,
    [property: JsonPropertyName("taskId")] Guid TaskId,
    [property: JsonPropertyName("agentId")] Guid AgentId,
    [property: JsonPropertyName("sequence")] long Sequence,
    [property: JsonPropertyName("stream")] string Stream,
    [property: JsonPropertyName("line")] string Line,
    [property: JsonPropertyName("emittedAt")] DateTimeOffset EmittedAt) : IProtocolMessage;

public sealed record TaskCompleted(
    [property: JsonPropertyName("schemaVersion")] int SchemaVersion,
    [property: JsonPropertyName("taskId")] Guid TaskId,
    [property: JsonPropertyName("agentId")] Guid AgentId,
    [property: JsonPropertyName("success")] bool Success,
    [property: JsonPropertyName("exitCode")] int? ExitCode,
    [property: JsonPropertyName("errorCode")] string? ErrorCode,
    [property: JsonPropertyName("finishedAt")] DateTimeOffset FinishedAt) : IProtocolMessage;

public sealed record HeartbeatMessage(
    [property: JsonPropertyName("schemaVersion")] int SchemaVersion,
    [property: JsonPropertyName("agentId")] Guid AgentId,
    [property: JsonPropertyName("sentAt")] DateTimeOffset SentAt) : IProtocolMessage;

public sealed record AgentNodeView(
    [property: JsonPropertyName("agentId")] Guid AgentId,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("online")] bool Online,
    [property: JsonPropertyName("lastSeenAt")] DateTimeOffset? LastSeenAt,
    [property: JsonPropertyName("capabilities")] IReadOnlyList<string> Capabilities,
    [property: JsonPropertyName("apiKeyLast4")] string? ApiKeyLast4 = null,
    [property: JsonPropertyName("apiKeyVersion")] int? ApiKeyVersion = null);
