using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using StackPivot.Control.Application.Audit;
using StackPivot.Control.Domain.Entities;
using StackPivot.Control.Infrastructure.Persistence;
using StackPivot.Control.Infrastructure.Security;
using StackPivot.Contracts.Agents;
using StackPivot.Contracts.Deployments;
using StackPivot.Contracts.SignalR;

namespace StackPivot.Control.Application.Deployments;

public sealed class DeploymentDispatcher(
    StackPivotDbContext dbContext,
    IAgentTransport transport,
    IGitCredentialProtector credentialProtector,
    AuditWriter auditWriter)
{
    public static readonly TimeSpan AcceptanceTimeout = TimeSpan.FromMinutes(2);
    public static readonly TimeSpan ExecutionTimeout = TimeSpan.FromHours(1);

    public async Task DispatchPendingAsync(CancellationToken cancellationToken)
    {
        var pending = await dbContext.ServiceOperationHistories
            .AsNoTracking()
            .Where(value => value.TaskStatus == "pending")
            .OrderBy(value => value.LastEventAt)
            .ToListAsync(cancellationToken);
        foreach (var history in pending)
        {
            var now = DateTimeOffset.UtcNow;
            if (history.AcceptedAt is not null || history.StartTime is not null)
            {
                if (now - history.LastEventAt >= ExecutionTimeout)
                {
                    await MarkFailedAsync(history, "agent_execution_timeout", cancellationToken);
                }

                continue;
            }

            if (history.DispatchedAt is not null)
            {
                if (now - history.DispatchedAt >= AcceptanceTimeout)
                {
                    await MarkFailedAsync(history, "agent_accept_timeout", cancellationToken);
                }

                continue;
            }

            if (history.DispatchAttemptAt is not null)
            {
                if (now - history.DispatchAttemptAt >= AcceptanceTimeout)
                {
                    await MarkFailedAsync(history, "agent_accept_timeout", cancellationToken);
                }

                continue;
            }

            if (!await TryClaimDispatchAsync(history.HistoryId, now, cancellationToken))
            {
                continue;
            }

            history.DispatchAttemptAt = now;
            await DispatchOneAsync(history, cancellationToken);
        }
    }

    public async Task HandleAcceptedAsync(TaskAccepted accepted, CancellationToken cancellationToken)
    {
        ProtocolValidation.EnsureSchemaVersion(accepted.SchemaVersion);
        var history = await FindTaskAsync(accepted.TaskId, accepted.AgentId, cancellationToken);
        if (history is null
            || history.TaskStatus != "pending"
            || (history.DispatchAttemptAt is null && history.DispatchedAt is null)
            || history.AcceptedAt is not null
            || history.StartTime is not null)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        var updated = await dbContext.ServiceOperationHistories
            .Where(value => value.HistoryId == history.HistoryId
                && value.TaskStatus == "pending"
                && (value.DispatchAttemptAt != null || value.DispatchedAt != null)
                && value.AcceptedAt == null
                && value.StartTime == null)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(value => value.AcceptedAt, now)
                    .SetProperty(value => value.StartTime, now)
                    .SetProperty(value => value.LastEventAt, now),
                cancellationToken);
        if (updated != 1)
        {
            return;
        }

        auditWriter.Add(
            AuditActions.TaskAccepted,
            history.RequestId,
            history.UserId,
            history.AgentId,
            "task",
            history.TaskId.ToString(),
            "accepted");
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task HandleLogAsync(TaskLog log, CancellationToken cancellationToken)
    {
        ProtocolValidation.EnsureSchemaVersion(log.SchemaVersion);
        var history = await FindTaskAsync(log.TaskId, log.AgentId, cancellationToken);
        if (history is null
            || history.TaskStatus != "pending"
            || (history.AcceptedAt is null && history.StartTime is null)
            || log.Stream is not ("stdout" or "stderr")
            || log.Sequence != history.LastSequence + 1)
        {
            return;
        }

        var sanitized = DeploymentLogSanitizer.SanitizeLine(log.Line);
        var outputLog = DeploymentLogSanitizer.Append(
            history.OutputLog,
            sanitized,
            out var truncated);
        var logEntries = history.OutputLogEntriesJson;
        var entriesTruncated = AppendLogEntry(ref logEntries, log.Stream, sanitized);
        var now = DateTimeOffset.UtcNow;
        await dbContext.ServiceOperationHistories
            .Where(value => value.HistoryId == history.HistoryId
                && value.TaskStatus == "pending"
                && (value.AcceptedAt != null || value.StartTime != null)
                && value.LastSequence == log.Sequence - 1)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(value => value.OutputLog, outputLog)
                    .SetProperty(value => value.OutputLogEntriesJson, logEntries)
                    .SetProperty(value => value.LogTruncated, history.LogTruncated || truncated || entriesTruncated)
                    .SetProperty(value => value.LastSequence, log.Sequence)
                    .SetProperty(value => value.LastEventAt, now),
                cancellationToken);
    }

    public async Task HandleCompletedAsync(TaskCompleted completed, CancellationToken cancellationToken)
    {
        ProtocolValidation.EnsureSchemaVersion(completed.SchemaVersion);
        var history = await FindTaskAsync(completed.TaskId, completed.AgentId, cancellationToken);
        if (history is null
            || history.TaskStatus != "pending"
            || (history.AcceptedAt is null && history.StartTime is null))
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        var taskStatus = completed.Success ? "success" : "failed";
        var errorCode = SanitizeErrorCode(completed.ErrorCode);
        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        var updated = await dbContext.ServiceOperationHistories
            .Where(value => value.HistoryId == history.HistoryId
                && value.TaskStatus == "pending"
                && (value.AcceptedAt != null || value.StartTime != null))
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(value => value.TaskStatus, taskStatus)
                    .SetProperty(value => value.ExitCode, completed.ExitCode)
                    .SetProperty(value => value.ErrorCode, errorCode)
                    .SetProperty(value => value.LogTruncated, history.LogTruncated || completed.LogTruncated)
                    .SetProperty(value => value.FinishTime, now)
                    .SetProperty(value => value.LastEventAt, now),
                cancellationToken);
        if (updated != 1)
        {
            return;
        }

        auditWriter.Add(
            completed.Success ? AuditActions.TaskSucceeded : AuditActions.TaskFailed,
            history.RequestId,
            history.UserId,
            history.AgentId,
            "task",
            history.TaskId.ToString(),
            completed.Success ? "success" : "failed",
            errorCode);
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task HandleAgentDisconnectedAsync(Guid agentId, CancellationToken cancellationToken)
    {
        if (agentId == Guid.Empty)
        {
            return;
        }

        var histories = await dbContext.ServiceOperationHistories
            .AsNoTracking()
            .Where(value => value.AgentId == agentId && value.TaskStatus == "pending")
            .ToListAsync(cancellationToken);
        if (histories.Count == 0)
        {
            return;
        }

        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        foreach (var history in histories)
        {
            await MarkFailedAsync(history, "agent_disconnected", cancellationToken, saveChanges: false);
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task HandleHeartbeatAsync(HeartbeatMessage heartbeat, CancellationToken cancellationToken)
    {
        ProtocolValidation.EnsureSchemaVersion(heartbeat.SchemaVersion);
        var agent = await dbContext.AgentNodes
            .SingleOrDefaultAsync(value => value.AgentId == heartbeat.AgentId && value.RevokedAt == null, cancellationToken);
        if (agent is null)
        {
            return;
        }

        agent.LastSeenAt = DateTimeOffset.UtcNow;
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private async Task DispatchOneAsync(
        ServiceOperationHistory history,
        CancellationToken cancellationToken)
    {
        if (!await transport.IsConnectedAsync(history.AgentId, cancellationToken))
        {
            await MarkFailedAsync(history, "agent_offline", cancellationToken);
            return;
        }

        var request = await dbContext.DeploymentRequests
            .Include(value => value.Stack)
            .ThenInclude(value => value!.Workspace)
            .SingleOrDefaultAsync(value => value.RequestId == history.RequestId, cancellationToken);
        var setting = await dbContext.GlobalGitSettings
            .SingleOrDefaultAsync(value => value.Id == 1, cancellationToken);
        if (request?.Stack?.Workspace is null || setting is null)
        {
            await MarkFailedAsync(history, "configuration_missing", cancellationToken);
            return;
        }

        if (string.IsNullOrWhiteSpace(history.GitRepoSnapshot)
            || string.IsNullOrWhiteSpace(history.GitUserNameSnapshot)
            || string.IsNullOrWhiteSpace(history.StackGitRelativePathSnapshot)
            || string.IsNullOrWhiteSpace(history.AgentStackLocalPathSnapshot)
            || string.IsNullOrWhiteSpace(history.TokenKeyId))
        {
            await MarkFailedAsync(history, "configuration_snapshot_missing", cancellationToken);
            return;
        }

        if (!string.Equals(history.GitRepoSnapshot, setting.GitRepo, StringComparison.Ordinal)
            || !string.Equals(history.GitUserNameSnapshot, setting.GitUserName, StringComparison.Ordinal)
            || !string.Equals(history.TokenKeyId, setting.TokenKeyId, StringComparison.Ordinal))
        {
            await MarkFailedAsync(history, "git_config_changed", cancellationToken);
            return;
        }

        var gitRepo = history.GitRepoSnapshot;
        var gitUserName = history.GitUserNameSnapshot;
        var stackGitRelativePath = history.StackGitRelativePathSnapshot;
        var agentStackLocalPath = history.AgentStackLocalPathSnapshot;
        var tokenKeyId = history.TokenKeyId;

        byte[]? accessToken = null;
        var command = (DeployStackCommand?)null;
        try
        {
            history.TokenKeyId = tokenKeyId;
            accessToken = credentialProtector.Unprotect(setting.AccessTokenEncrypted, tokenKeyId);
            command = new DeployStackCommand(
                ProtocolVersion.Current,
                history.TaskId,
                history.RequestId,
                history.StackId,
                history.AgentId,
                gitRepo,
                gitUserName,
                accessToken,
                history.TargetCommitHash,
                stackGitRelativePath,
                agentStackLocalPath,
                DateTimeOffset.UtcNow.AddMinutes(5));
            await transport.SendDeployAsync(command, cancellationToken);
            var dispatchedAt = DateTimeOffset.UtcNow;
            await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
            var updated = await dbContext.ServiceOperationHistories
                .Where(value => value.HistoryId == history.HistoryId
                    && value.TaskStatus == "pending"
                    && value.DispatchAttemptAt == history.DispatchAttemptAt
                    && value.DispatchedAt == null)
                .ExecuteUpdateAsync(
                    setters => setters
                        .SetProperty(value => value.DispatchedAt, dispatchedAt)
                        .SetProperty(value => value.TokenKeyId, tokenKeyId)
                        .SetProperty(value => value.LastEventAt, dispatchedAt),
                    cancellationToken);
            if (updated == 1)
            {
                auditWriter.Add(
                    AuditActions.TaskDispatched,
                    history.RequestId,
                    history.UserId,
                    history.AgentId,
                    "task",
                    history.TaskId.ToString(),
                    "sent");
                await dbContext.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            await MarkFailedAsync(history, "agent_offline", cancellationToken);
        }
        finally
        {
            command?.ClearAccessToken();
            if (accessToken is not null)
            {
                CryptographicOperations.ZeroMemory(accessToken);
            }
        }
    }

    private async Task MarkFailedAsync(
        ServiceOperationHistory history,
        string errorCode,
        CancellationToken cancellationToken,
        bool saveChanges = true)
    {
        if (history.TaskStatus != "pending")
        {
            return;
        }

        await using var transaction = saveChanges
            ? await dbContext.Database.BeginTransactionAsync(cancellationToken)
            : null;
        var now = DateTimeOffset.UtcNow;
        var updated = await dbContext.ServiceOperationHistories
            .Where(value => value.HistoryId == history.HistoryId && value.TaskStatus == "pending")
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(value => value.TaskStatus, "failed")
                    .SetProperty(value => value.ErrorCode, errorCode)
                    .SetProperty(value => value.FinishTime, now)
                    .SetProperty(value => value.LastEventAt, now),
                cancellationToken);
        if (updated != 1)
        {
            return;
        }

        auditWriter.Add(
            AuditActions.TaskFailed,
            history.RequestId,
            history.UserId,
            history.AgentId,
            "task",
            history.TaskId.ToString(),
            "failed",
            errorCode);
        if (saveChanges)
        {
            await dbContext.SaveChangesAsync(cancellationToken);
            await transaction!.CommitAsync(cancellationToken);
        }
    }

    private async Task<bool> TryClaimDispatchAsync(
        Guid historyId,
        DateTimeOffset attemptAt,
        CancellationToken cancellationToken)
    {
        var updated = await dbContext.ServiceOperationHistories
            .Where(value => value.HistoryId == historyId
                && value.TaskStatus == "pending"
                && value.DispatchAttemptAt == null
                && value.DispatchedAt == null)
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(value => value.DispatchAttemptAt, attemptAt),
                cancellationToken);
        return updated == 1;
    }

    private Task<ServiceOperationHistory?> FindTaskAsync(
        Guid taskId,
        Guid agentId,
        CancellationToken cancellationToken)
    {
        return dbContext.ServiceOperationHistories
            .AsNoTracking()
            .SingleOrDefaultAsync(
                value => value.TaskId == taskId && value.AgentId == agentId,
                cancellationToken);
    }

    private static string? SanitizeErrorCode(string? errorCode)
    {
        if (string.IsNullOrWhiteSpace(errorCode))
        {
            return null;
        }

        return errorCode.Length <= 100 && errorCode.All(character => char.IsLetterOrDigit(character) || character is '_' or '-')
            ? errorCode
            : "agent_error";
    }

    private static bool AppendLogEntry(ref string json, string stream, string line)
    {
        try
        {
            var entries = JsonSerializer.Deserialize<List<DeploymentLogEntryView>>(json)
                ?? new List<DeploymentLogEntryView>();
            entries.Add(new DeploymentLogEntryView(stream, line));
            var serialized = JsonSerializer.Serialize(entries);
            if (Encoding.UTF8.GetByteCount(serialized) <= DeploymentLogSanitizer.MaxTaskBytes)
            {
                json = serialized;
                return false;
            }

            return true;
        }
        catch (JsonException)
        {
            var serialized = JsonSerializer.Serialize(new[] { new DeploymentLogEntryView(stream, line) });
            if (Encoding.UTF8.GetByteCount(serialized) <= DeploymentLogSanitizer.MaxTaskBytes)
            {
                json = serialized;
                return false;
            }

            return true;
        }
    }
}

public static partial class DeploymentLogSanitizer
{
    public const int MaxLineBytes = 16 * 1024;
    public const int MaxTaskBytes = 1024 * 1024;

    [GeneratedRegex("(?i)(authorization|x-agent-api-key)\\s*[:=]\\s*(?:Bearer\\s+)?(?:\"[^\"\\r\\n]*\"|'[^'\\r\\n]*'|[^\\s,;]+)", RegexOptions.CultureInvariant)]
    private static partial Regex HeaderSecretPattern();

    [GeneratedRegex("(?i)(password|passwd|pwd|secret|token|api[-_]?key|access[-_]?token|client[-_]?secret)\\s*[:=]\\s*(?:\"[^\"\\r\\n]*\"|'[^'\\r\\n]*'|[^\\s,;]+)", RegexOptions.CultureInvariant)]
    private static partial Regex KeyValueSecretPattern();

    public static string SanitizeLine(string? value)
    {
        var sanitized = HeaderSecretPattern().Replace(value ?? string.Empty, "$1=[REDACTED]");
        sanitized = KeyValueSecretPattern().Replace(sanitized, "$1=[REDACTED]");
        var bytes = Encoding.UTF8.GetBytes(sanitized);
        if (bytes.Length <= MaxLineBytes)
        {
            return sanitized;
        }

        return TruncateUtf8(sanitized, MaxLineBytes);
    }

    public static string Append(string current, string line, out bool truncated)
    {
        var separator = string.IsNullOrEmpty(current) ? string.Empty : "\n";
        var candidate = current + separator + line;
        var bytes = Encoding.UTF8.GetBytes(candidate);
        if (bytes.Length <= MaxTaskBytes)
        {
            truncated = false;
            return candidate;
        }

        truncated = true;
        return TruncateUtf8(candidate, MaxTaskBytes);
    }

    private static string TruncateUtf8(string value, int maxBytes)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        if (bytes.Length <= maxBytes)
        {
            return value;
        }

        var length = Math.Max(0, maxBytes);
        var strict = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
        while (length > 0)
        {
            try
            {
                return strict.GetString(bytes, 0, length);
            }
            catch (DecoderFallbackException)
            {
                length--;
            }
        }

        return string.Empty;
    }
}
