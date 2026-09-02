using System.Text.Json.Serialization;

namespace StackPivot.Contracts.Resources;

public sealed record CurrentUserView(
    [property: JsonPropertyName("userId")] Guid UserId,
    [property: JsonPropertyName("userName")] string UserName,
    [property: JsonPropertyName("roles")] IReadOnlyList<string> Roles);

public sealed record WorkspaceView(
    [property: JsonPropertyName("workspaceId")] Guid WorkspaceId,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("displayName")] string DisplayName);

public sealed record StackView(
    [property: JsonPropertyName("stackId")] Guid StackId,
    [property: JsonPropertyName("workspaceId")] Guid WorkspaceId,
    [property: JsonPropertyName("folderName")] string FolderName,
    [property: JsonPropertyName("displayName")] string DisplayName);

public sealed record DeploymentTargetView(
    [property: JsonPropertyName("agentId")] Guid AgentId,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("online")] bool Online,
    [property: JsonPropertyName("lastSeenAt")] DateTimeOffset? LastSeenAt,
    [property: JsonPropertyName("capabilities")] IReadOnlyList<string> Capabilities);

public sealed record AgentCreateRequest(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("remark")] string? Remark = null);

public sealed record UpdateStackAgentBindingsRequest(
    [property: JsonPropertyName("agentIds")] IReadOnlyList<Guid> AgentIds);

public sealed record AgentKeyIssueView(
    [property: JsonPropertyName("agentId")] Guid AgentId,
    [property: JsonPropertyName("apiKey")] string ApiKey,
    [property: JsonPropertyName("apiKeyVersion")] int ApiKeyVersion,
    [property: JsonPropertyName("apiKeyLast4")] string ApiKeyLast4);
