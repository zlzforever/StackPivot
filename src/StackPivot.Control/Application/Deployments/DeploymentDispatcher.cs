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
    AuditWriter auditWriter,
    IDbContextFactory<StackPivotDbContext>? verificationContextFactory = null)
{
    public static readonly TimeSpan AcceptanceTimeout = TimeSpan.FromMinutes(2);
    public static readonly TimeSpan DispatchLifetime = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan ExecutionTimeout = TimeSpan.FromHours(1);
    private static readonly TimeSpan ReportClockSkew = TimeSpan.FromMinutes(2);

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
                var executionStartedAt = history.StartTime ?? history.AcceptedAt ?? history.LastEventAt;
                if (now - executionStartedAt >= ExecutionTimeout)
                {
                    await MarkFailedAsync(history, "agent_execution_timeout", cancellationToken);
                }

                continue;
            }

            if (history.DispatchedAt is not null)
            {
                if (now - history.DispatchedAt >= AcceptanceTimeout)
                {
                    await MarkFailedAsync(
                        history,
                        "agent_accept_timeout",
                        cancellationToken,
                        requireNotStarted: true);
                }

                continue;
            }

            if (history.DispatchAttemptAt is not null)
            {
                if (now - history.DispatchAttemptAt >= AcceptanceTimeout)
                {
                    await MarkFailedAsync(
                        history,
                        history.DispatchedAt is null
                            ? "agent_dispatch_unknown"
                            : "agent_accept_timeout",
                        cancellationToken,
                        requireNotStarted: true);
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
            || !IsAcceptedReportAllowed(history, accepted))
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        var updated = await dbContext.ServiceOperationHistories
            .Where(value => value.HistoryId == history.HistoryId
                && (value.TaskStatus == "pending"
                    && (value.DispatchAttemptAt != null || value.DispatchedAt != null)
                    && value.AcceptedAt == null
                    && value.StartTime == null
                    || value.TaskStatus == "failed"
                    && value.ErrorCode == "agent_dispatch_unknown"))
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(value => value.TaskStatus, "pending")
                    .SetProperty(value => value.ErrorCode, (string?)null)
                    .SetProperty(value => value.ExitCode, (int?)null)
                    .SetProperty(value => value.FinishTime, (DateTimeOffset?)null)
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
        try
        {
            await transaction.CommitAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            var persisted = await IsAcceptedPersistedAsync(history.HistoryId, cancellationToken);
            dbContext.ChangeTracker.Clear();
            if (persisted)
            {
                return;
            }

            throw;
        }
    }

    public async Task HandleLogAsync(TaskLog log, CancellationToken cancellationToken)
    {
        ProtocolValidation.EnsureSchemaVersion(log.SchemaVersion);
        var history = await FindTaskAsync(log.TaskId, log.AgentId, cancellationToken);
        if (history is null
            || history.TaskStatus != "pending"
            || (history.AcceptedAt is null && history.StartTime is null)
            || !IsLogReportAllowed(history, log)
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
            || !IsCompletedReportAllowed(history, completed))
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        var startTime = history.StartTime
            ?? history.AcceptedAt
            ?? history.DispatchAttemptAt
            ?? history.DispatchedAt
            ?? completed.FinishedAt;
        var completionIsConsistent = completed.Success == (completed.ExitCode == 0);
        var taskStatus = completionIsConsistent && completed.Success ? "success" : "failed";
        var errorCode = !completionIsConsistent
            ? "completion_exit_code_conflict"
            : taskStatus == "failed"
                ? SanitizeErrorCode(completed.ErrorCode) ?? "agent_error"
                : null;
        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        var updated = await dbContext.ServiceOperationHistories
            .Where(value => value.HistoryId == history.HistoryId
                && ((value.TaskStatus == "pending"
                        && (value.AcceptedAt != null || value.StartTime != null))
                    || value.TaskStatus == "failed"
                    && value.ErrorCode == "agent_dispatch_unknown"))
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(value => value.TaskStatus, taskStatus)
                    .SetProperty(value => value.ExitCode, completed.ExitCode)
                    .SetProperty(value => value.ErrorCode, errorCode)
                    .SetProperty(value => value.LogTruncated, history.LogTruncated || completed.LogTruncated)
                    .SetProperty(value => value.AcceptedAt, startTime)
                    .SetProperty(value => value.StartTime, startTime)
                    .SetProperty(value => value.FinishTime, now)
                    .SetProperty(value => value.LastEventAt, now),
                cancellationToken);
        if (updated != 1)
        {
            return;
        }

        auditWriter.Add(
            taskStatus == "success" ? AuditActions.TaskSucceeded : AuditActions.TaskFailed,
            history.RequestId,
            history.UserId,
            history.AgentId,
            "task",
            history.TaskId.ToString(),
            taskStatus,
            errorCode);
        await dbContext.SaveChangesAsync(cancellationToken);
        try
        {
            await transaction.CommitAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            var persisted = await IsCompletedPersistedAsync(
                history.HistoryId,
                taskStatus,
                completed.ExitCode,
                errorCode,
                cancellationToken);
            dbContext.ChangeTracker.Clear();
            if (persisted)
            {
                return;
            }

            throw;
        }
    }

    private static bool IsAcceptedReportAllowed(
        ServiceOperationHistory history,
        TaskAccepted accepted)
    {
        var stateIsAcceptable = history.TaskStatus == "pending"
            && (history.DispatchAttemptAt is not null || history.DispatchedAt is not null)
            && history.AcceptedAt is null
            && history.StartTime is null;
        if (!stateIsAcceptable && !IsUnknownDispatch(history))
        {
            return false;
        }

        return HasValidReportContext(
            history,
            accepted.DispatchFingerprint,
            accepted.TargetCommitHash,
            accepted.StackGitRelativePath,
            accepted.AgentStackLocalPath,
            accepted.DispatchExpiresAt,
            accepted.AcceptedAt)
            && accepted.AcceptedAt >= DispatchStartedAt(history)!.Value.Add(-ReportClockSkew);
    }

    private static bool IsLogReportAllowed(
        ServiceOperationHistory history,
        TaskLog log)
    {
        if (!HasValidReportContext(
                history,
                log.DispatchFingerprint,
                log.TargetCommitHash,
                log.StackGitRelativePath,
                log.AgentStackLocalPath,
                log.DispatchExpiresAt,
                log.EmittedAt)
            || history.AcceptedAt is null)
        {
            return false;
        }

        return log.EmittedAt >= history.AcceptedAt.Value - ReportClockSkew;
    }

    private static bool IsCompletedReportAllowed(
        ServiceOperationHistory history,
        TaskCompleted completed)
    {
        var stateIsAcceptable = history.TaskStatus == "pending"
            && (history.AcceptedAt is not null || history.StartTime is not null);
        if (!stateIsAcceptable && !IsUnknownDispatch(history))
        {
            return false;
        }

        if (!HasValidReportContext(
                history,
                completed.DispatchFingerprint,
                completed.TargetCommitHash,
                completed.StackGitRelativePath,
                completed.AgentStackLocalPath,
                completed.DispatchExpiresAt,
                completed.FinishedAt))
        {
            return false;
        }

        var startTime = history.StartTime
            ?? history.AcceptedAt
            ?? DispatchStartedAt(history)
            ?? completed.FinishedAt;
        return completed.FinishedAt >= startTime - ReportClockSkew;
    }

    private static bool HasValidReportContext(
        ServiceOperationHistory history,
        string? dispatchFingerprint,
        string? targetCommitHash,
        string? stackGitRelativePath,
        string? agentStackLocalPath,
        DateTimeOffset? dispatchExpiresAt,
        DateTimeOffset eventAt)
    {
        var dispatchStartedAt = DispatchStartedAt(history);
        if (dispatchStartedAt is null
            || dispatchExpiresAt is null
            || string.IsNullOrWhiteSpace(history.GitRepoSnapshot)
            || string.IsNullOrWhiteSpace(history.GitUserNameSnapshot)
            || string.IsNullOrWhiteSpace(history.StackGitRelativePathSnapshot)
            || string.IsNullOrWhiteSpace(history.AgentStackLocalPathSnapshot)
            || !string.Equals(targetCommitHash, history.TargetCommitHash, StringComparison.Ordinal)
            || !string.Equals(stackGitRelativePath, history.StackGitRelativePathSnapshot, StringComparison.Ordinal)
            || !string.Equals(agentStackLocalPath, history.AgentStackLocalPathSnapshot, StringComparison.Ordinal))
        {
            return false;
        }

        var expectedExpiresAt = dispatchStartedAt.Value.Add(DispatchLifetime);
        if (dispatchExpiresAt.Value.UtcDateTime.Ticks != expectedExpiresAt.UtcDateTime.Ticks)
        {
            return false;
        }

        var expectedFingerprint = DispatchFingerprint.Compute(
            history.TaskId,
            history.RequestId,
            history.StackId,
            history.AgentId,
            history.GitRepoSnapshot,
            history.GitUserNameSnapshot,
            history.TargetCommitHash,
            history.StackGitRelativePathSnapshot,
            history.AgentStackLocalPathSnapshot,
            expectedExpiresAt);
        if (!DispatchFingerprint.Matches(dispatchFingerprint, expectedFingerprint))
        {
            return false;
        }

        var now = DateTimeOffset.UtcNow;
        if (eventAt < dispatchStartedAt.Value - ReportClockSkew
            || eventAt > now + ReportClockSkew)
        {
            return false;
        }

        var executionStartedAt = history.StartTime
            ?? history.AcceptedAt
            ?? dispatchStartedAt.Value;
        var executionDeadline = executionStartedAt.Add(ExecutionTimeout);
        if (history.AcceptedAt is null
            && !IsUnknownDispatch(history)
            && now > expectedExpiresAt + ReportClockSkew)
        {
            return false;
        }

        if (now > executionDeadline + ReportClockSkew)
        {
            return false;
        }

        return true;
    }

    private static DateTimeOffset? DispatchStartedAt(ServiceOperationHistory history) =>
        history.DispatchAttemptAt ?? history.DispatchedAt;

    private static bool IsUnknownDispatch(ServiceOperationHistory history) =>
        history.TaskStatus == "failed"
        && string.Equals(history.ErrorCode, "agent_dispatch_unknown", StringComparison.Ordinal);

    private async Task<bool> IsAcceptedPersistedAsync(
        Guid historyId,
        CancellationToken cancellationToken)
    {
        var state = await ReadReportPersistenceStateAsync(historyId, cancellationToken);
        return state is not null
            && state.AcceptedAt is not null
            && state.StartTime is not null;
    }

    private async Task<bool> IsCompletedPersistedAsync(
        Guid historyId,
        string taskStatus,
        int? exitCode,
        string? errorCode,
        CancellationToken cancellationToken)
    {
        var state = await ReadReportPersistenceStateAsync(historyId, cancellationToken);
        return state is not null
            && state.TaskStatus == taskStatus
            && state.ExitCode == exitCode
            && string.Equals(state.ErrorCode, errorCode, StringComparison.Ordinal)
            && state.FinishTime is not null;
    }

    private async Task<ReportPersistenceState?> ReadReportPersistenceStateAsync(
        Guid historyId,
        CancellationToken cancellationToken)
    {
        if (verificationContextFactory is null)
        {
            return null;
        }

        try
        {
            await using var verificationContext = await verificationContextFactory
                .CreateDbContextAsync(cancellationToken);
            return await verificationContext.ServiceOperationHistories
                .AsNoTracking()
                .Where(value => value.HistoryId == historyId)
                .Select(value => new ReportPersistenceState(
                    value.TaskStatus,
                    value.AcceptedAt,
                    value.StartTime,
                    value.ExitCode,
                    value.ErrorCode,
                    value.FinishTime))
                .SingleOrDefaultAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private sealed record ReportPersistenceState(
        string TaskStatus,
        DateTimeOffset? AcceptedAt,
        DateTimeOffset? StartTime,
        int? ExitCode,
        string? ErrorCode,
        DateTimeOffset? FinishTime);

    public async Task HandleAgentDisconnectedAsync(Guid agentId, CancellationToken cancellationToken)
    {
        if (agentId == Guid.Empty)
        {
            return;
        }

        var histories = await dbContext.ServiceOperationHistories
            .AsNoTracking()
            .Where(value => value.AgentId == agentId
                && value.TaskStatus == "pending"
                && value.DispatchAttemptAt == null
                && value.DispatchedAt == null)
            .ToListAsync(cancellationToken);
        if (histories.Count == 0)
        {
            return;
        }

        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        foreach (var history in histories)
        {
            await MarkFailedAsync(
                history,
                "agent_disconnected",
                cancellationToken,
                saveChanges: false,
                requireNotStarted: true,
                requireNotDispatched: true);
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
            await MarkFailedAsync(history, "agent_offline", cancellationToken, requireNotStarted: true);
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
            await MarkFailedAsync(history, "configuration_missing", cancellationToken, requireNotStarted: true);
            return;
        }

        if (string.IsNullOrWhiteSpace(history.GitRepoSnapshot)
            || string.IsNullOrWhiteSpace(history.GitUserNameSnapshot)
            || string.IsNullOrWhiteSpace(history.StackGitRelativePathSnapshot)
            || string.IsNullOrWhiteSpace(history.AgentStackLocalPathSnapshot)
            || string.IsNullOrWhiteSpace(history.TokenKeyId))
        {
            await MarkFailedAsync(history, "configuration_snapshot_missing", cancellationToken, requireNotStarted: true);
            return;
        }

        if (!string.Equals(history.GitRepoSnapshot, setting.GitRepo, StringComparison.Ordinal)
            || !string.Equals(history.GitUserNameSnapshot, setting.GitUserName, StringComparison.Ordinal)
            || !string.Equals(history.TokenKeyId, setting.TokenKeyId, StringComparison.Ordinal))
        {
            await MarkFailedAsync(history, "git_config_changed", cancellationToken, requireNotStarted: true);
            return;
        }

        var gitRepo = history.GitRepoSnapshot;
        var gitUserName = history.GitUserNameSnapshot;
        var stackGitRelativePath = history.StackGitRelativePathSnapshot;
        var agentStackLocalPath = history.AgentStackLocalPathSnapshot;
        var tokenKeyId = history.TokenKeyId;

        byte[]? accessToken = null;
        var command = (DeployStackCommand?)null;
        var sendStarted = false;
        var dispatchMarkerCommitted = false;
        try
        {
            history.TokenKeyId = tokenKeyId;
            accessToken = credentialProtector.Unprotect(setting.AccessTokenEncrypted, tokenKeyId);
            var dispatchStartedAt = history.DispatchAttemptAt ?? DateTimeOffset.UtcNow;
            var dispatchExpiresAt = dispatchStartedAt.Add(DispatchLifetime);
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
                dispatchExpiresAt,
                DispatchFingerprint.Compute(
                    history.TaskId,
                    history.RequestId,
                    history.StackId,
                    history.AgentId,
                    gitRepo,
                    gitUserName,
                    history.TargetCommitHash,
                    stackGitRelativePath,
                    agentStackLocalPath,
                    dispatchExpiresAt));
            if (!DispatchFingerprint.Matches(command))
            {
                await MarkFailedAsync(history, "agent_dispatch_invalid", cancellationToken, requireNotStarted: true);
                return;
            }

            sendStarted = true;
            await transport.SendDeployAsync(command, cancellationToken);
            var dispatchedAt = DateTimeOffset.UtcNow;
            await using (var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken))
            {
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
                    await transaction.CommitAsync(cancellationToken);
                    dispatchMarkerCommitted = true;
                }
            }

            if (!dispatchMarkerCommitted)
            {
                return;
            }

            try
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
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                dbContext.ChangeTracker.Clear();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (AgentOfflineException)
        {
            await MarkFailedAsync(history, "agent_offline", cancellationToken, requireNotStarted: true);
        }
        catch (Exception)
        {
            if (dispatchMarkerCommitted)
            {
                dbContext.ChangeTracker.Clear();
                return;
            }

            if (sendStarted)
            {
                var markerState = await ReadDispatchMarkerStateAsync(
                    history.HistoryId,
                    cancellationToken);
                if (markerState is null
                    || markerState.DispatchMarkerCommitted
                    || markerState.ExecutionStarted
                    || markerState.TaskStatus != "pending")
                {
                    return;
                }

                dbContext.ChangeTracker.Clear();
                return;
            }

            await MarkFailedAsync(history, "agent_dispatch_failed", cancellationToken, requireNotStarted: true);
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

    private async Task<DispatchMarkerState?> ReadDispatchMarkerStateAsync(
        Guid historyId,
        CancellationToken cancellationToken)
    {
        try
        {
            if (verificationContextFactory is not null)
            {
                await using var verificationContext = await verificationContextFactory.CreateDbContextAsync(cancellationToken);
                var state = await verificationContext.ServiceOperationHistories
                    .AsNoTracking()
                    .Where(value => value.HistoryId == historyId)
                    .Select(value => new
                    {
                        value.TaskStatus,
                        DispatchMarkerCommitted = value.DispatchedAt != null,
                        ExecutionStarted = value.AcceptedAt != null || value.StartTime != null
                    })
                    .SingleOrDefaultAsync(cancellationToken);
                return state is null
                    ? null
                    : new DispatchMarkerState(
                        state.TaskStatus,
                        state.DispatchMarkerCommitted,
                        state.ExecutionStarted);
            }

            dbContext.ChangeTracker.Clear();
            var localState = await dbContext.ServiceOperationHistories
                .AsNoTracking()
                .Where(value => value.HistoryId == historyId)
                .Select(value => new
                {
                    value.TaskStatus,
                    DispatchMarkerCommitted = value.DispatchedAt != null,
                    ExecutionStarted = value.AcceptedAt != null || value.StartTime != null
                })
                .SingleOrDefaultAsync(cancellationToken);
            return localState is null
                ? null
                : new DispatchMarkerState(
                    localState.TaskStatus,
                    localState.DispatchMarkerCommitted,
                    localState.ExecutionStarted);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private sealed record DispatchMarkerState(
        string TaskStatus,
        bool DispatchMarkerCommitted,
        bool ExecutionStarted);

    private async Task MarkFailedAsync(
        ServiceOperationHistory history,
        string errorCode,
        CancellationToken cancellationToken,
        bool saveChanges = true,
        bool requireNotStarted = false,
        bool requireNotDispatched = false)
    {
        if (history.TaskStatus != "pending")
        {
            return;
        }

        await using var transaction = saveChanges
            ? await dbContext.Database.BeginTransactionAsync(cancellationToken)
            : null;
        var now = DateTimeOffset.UtcNow;
        var query = dbContext.ServiceOperationHistories
            .Where(value => value.HistoryId == history.HistoryId && value.TaskStatus == "pending");
        if (requireNotStarted)
        {
            query = query.Where(value => value.AcceptedAt == null && value.StartTime == null);
        }

        if (requireNotDispatched)
        {
            query = query.Where(value => value.DispatchAttemptAt == null && value.DispatchedAt == null);
        }

        var updated = await query
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
