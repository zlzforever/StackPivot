namespace StackPivot.Control.Domain.Entities;

public sealed class ServiceOperationHistory
{
    public Guid HistoryId { get; set; }
    public Guid TaskId { get; set; }
    public Guid RequestId { get; set; }
    public Guid StackId { get; set; }
    public Guid AgentId { get; set; }
    public Guid UserId { get; set; }
    public string OperationType { get; set; } = "full_deploy";
    public string TargetCommitHash { get; set; } = string.Empty;
    public string TaskStatus { get; set; } = "pending";
    public string CommandText { get; set; } = "docker compose up -d";
    public int? ExitCode { get; set; }
    public DateTimeOffset? StartTime { get; set; }
    public DateTimeOffset? FinishTime { get; set; }
    public string OutputLog { get; set; } = string.Empty;
    public bool LogTruncated { get; set; }
    public string? ErrorCode { get; set; }
    public long LastSequence { get; set; } = -1;
    public DateTimeOffset LastEventAt { get; set; }
    public DateTimeOffset? DispatchedAt { get; set; }
    public DateTimeOffset? DispatchAttemptAt { get; set; }
    public DateTimeOffset? AcceptedAt { get; set; }
    public string TokenKeyId { get; set; } = string.Empty;
    public string? GitRepoSnapshot { get; set; }
    public string? GitUserNameSnapshot { get; set; }
    public string? StackGitRelativePathSnapshot { get; set; }
    public string? AgentStackLocalPathSnapshot { get; set; }
    public string OutputLogEntriesJson { get; set; } = "[]";

    public Stack? Stack { get; set; }
}
