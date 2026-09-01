using System.Text.Json.Serialization;
using StackPivot.Contracts.SignalR;

namespace StackPivot.Contracts.Deployments;

[JsonConverter(typeof(DeploymentModeJsonConverter))]
public enum DeploymentMode
{
    BoundAgents,
    SingleAgent
}

public sealed record DeployStackRequest(
    [property: JsonPropertyName("targetCommitHash")] string TargetCommitHash,
    [property: JsonPropertyName("mode")] DeploymentMode Mode,
    [property: JsonPropertyName("agentId")] Guid? AgentId)
{
    public ValidationResult Validate() => ProtocolValidation.Validate(this);
}

public sealed record DeployStackCommand(
    [property: JsonPropertyName("schemaVersion")] int SchemaVersion,
    [property: JsonPropertyName("taskId")] Guid TaskId,
    [property: JsonPropertyName("requestId")] Guid RequestId,
    [property: JsonPropertyName("stackId")] Guid StackId,
    [property: JsonPropertyName("agentId")] Guid AgentId,
    [property: JsonPropertyName("gitRepo")] string GitRepo,
    [property: JsonPropertyName("gitUserName")] string GitUserName,
    [property: JsonPropertyName("accessToken")] byte[] AccessToken,
    [property: JsonPropertyName("targetCommitHash")] string TargetCommitHash,
    [property: JsonPropertyName("stackGitRelativePath")] string StackGitRelativePath,
    [property: JsonPropertyName("agentStackLocalPath")] string AgentStackLocalPath,
    [property: JsonPropertyName("expiresAt")] DateTimeOffset ExpiresAt) : IProtocolMessage
{
    public void ClearAccessToken()
    {
        if (AccessToken is { Length: > 0 })
        {
            Array.Clear(AccessToken, 0, AccessToken.Length);
        }
    }
}

public sealed record DeploymentTaskResult(
    [property: JsonPropertyName("taskId")] Guid TaskId,
    [property: JsonPropertyName("agentId")] Guid AgentId,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("errorCode")] string? ErrorCode = null);

public sealed record DeploymentRequestResult(
    [property: JsonPropertyName("requestId")] Guid RequestId,
    [property: JsonPropertyName("stackId")] Guid StackId,
    [property: JsonPropertyName("targetCommitHash")] string TargetCommitHash,
    [property: JsonPropertyName("tasks")] IReadOnlyList<DeploymentTaskResult> Tasks);

public sealed record DeploymentTaskView(
    [property: JsonPropertyName("taskId")] Guid TaskId,
    [property: JsonPropertyName("agentId")] Guid AgentId,
    [property: JsonPropertyName("operationType")] string OperationType,
    [property: JsonPropertyName("targetCommitHash")] string TargetCommitHash,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("command")] string Command,
    [property: JsonPropertyName("exitCode")] int? ExitCode,
    [property: JsonPropertyName("startedAt")] DateTimeOffset? StartedAt,
    [property: JsonPropertyName("finishedAt")] DateTimeOffset? FinishedAt,
    [property: JsonPropertyName("outputLog")] string OutputLog,
    [property: JsonPropertyName("logTruncated")] bool LogTruncated,
    [property: JsonPropertyName("errorCode")] string? ErrorCode);

public sealed record DeploymentRequestView(
    [property: JsonPropertyName("requestId")] Guid RequestId,
    [property: JsonPropertyName("tasks")] IReadOnlyList<DeploymentTaskView> Tasks);

public enum TaskStatus
{
    Pending,
    Success,
    Failed
}
