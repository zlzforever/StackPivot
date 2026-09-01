using System.Text;
using System.Security.Cryptography;
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
    private static readonly SemaphoreSlim DispatchLock = new(1, 1);
    public static readonly TimeSpan DispatchAcknowledgementTimeout = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan ExecutionTimeout = TimeSpan.FromMinutes(30);

    public async Task DispatchPendingAsync(CancellationToken cancellationToken)
    {
        await DispatchLock.WaitAsync(cancellationToken);
        try
        {
            var pending = await dbContext.ServiceOperationHistories
                .Where(value => value.TaskStatus == "pending")
                .OrderBy(value => value.LastEventAt)
                .ToListAsync(cancellationToken);
            var now = DateTimeOffset.UtcNow;
            foreach (var history in pending)
            {
                if (IsExpired(history, now))
                {
                    await MarkFailedAsync(history, "agent_timeout", cancellationToken);
                }
                else if (history.DispatchedAt is null)
                {
                    await DispatchOneAsync(history, cancellationToken);
                }
            }
        }
        finally
        {
            DispatchLock.Release();
        }
    }

    public async Task HandleAcceptedAsync(TaskAccepted accepted, CancellationToken cancellationToken)
    {
        ProtocolValidation.EnsureSchemaVersion(accepted.SchemaVersion);
        var history = await FindTaskAsync(accepted.TaskId, accepted.AgentId, cancellationToken);
        if (history is null || history.TaskStatus != "pending" || history.StartTime is not null)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        history.StartTime = now;
        history.LastEventAt = now;
        auditWriter.Add(
            AuditActions.TaskAccepted,
            history.RequestId,
            history.UserId,
            history.AgentId,
            "task",
            history.TaskId.ToString(),
            "accepted");
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task HandleLogAsync(TaskLog log, CancellationToken cancellationToken)
    {
        ProtocolValidation.EnsureSchemaVersion(log.SchemaVersion);
        var history = await FindTaskAsync(log.TaskId, log.AgentId, cancellationToken);
        if (history is null
            || history.TaskStatus != "pending"
            || history.StartTime is null
            || log.Stream is not ("stdout" or "stderr")
            || log.Sequence != history.LastSequence + 1)
        {
            return;
        }

        var sanitized = DeploymentLogSanitizer.SanitizeLine(log.Line);
        history.OutputLog = DeploymentLogSanitizer.Append(
            history.OutputLog,
            sanitized,
            out var truncated);
        history.LogTruncated |= truncated;
        history.LastSequence = log.Sequence;
        history.LastEventAt = DateTimeOffset.UtcNow;
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task HandleCompletedAsync(TaskCompleted completed, CancellationToken cancellationToken)
    {
        ProtocolValidation.EnsureSchemaVersion(completed.SchemaVersion);
        var history = await FindTaskAsync(completed.TaskId, completed.AgentId, cancellationToken);
        if (history is null || history.TaskStatus != "pending" || history.StartTime is null)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        history.TaskStatus = completed.Success ? "success" : "failed";
        history.ExitCode = completed.ExitCode;
        history.ErrorCode = SanitizeErrorCode(completed.ErrorCode);
        history.FinishTime = now;
        history.LastEventAt = now;
        auditWriter.Add(
            completed.Success ? AuditActions.TaskSucceeded : AuditActions.TaskFailed,
            history.RequestId,
            history.UserId,
            history.AgentId,
            "task",
            history.TaskId.ToString(),
            completed.Success ? "success" : "failed",
            history.ErrorCode);
        await dbContext.SaveChangesAsync(cancellationToken);
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

        byte[]? accessToken = null;
        var command = (DeployStackCommand?)null;
        try
        {
            var tokenKeyId = string.IsNullOrWhiteSpace(history.TokenKeyId)
                ? setting.TokenKeyId
                : history.TokenKeyId;
            history.TokenKeyId = tokenKeyId;
            accessToken = credentialProtector.Unprotect(setting.AccessTokenEncrypted, tokenKeyId);
            command = new DeployStackCommand(
                ProtocolVersion.Current,
                history.TaskId,
                history.RequestId,
                history.StackId,
                history.AgentId,
                setting.GitRepo,
                setting.GitUserName,
                accessToken,
                history.TargetCommitHash,
                $"{request.Stack.Workspace.Name}/{request.Stack.FolderName}",
                $"/opt/agent-main/{request.Stack.Workspace.Name}/{request.Stack.FolderName}",
                DateTimeOffset.UtcNow.AddMinutes(5));
            await transport.SendDeployAsync(command, cancellationToken);
            history.DispatchedAt = DateTimeOffset.UtcNow;
            history.LastEventAt = history.DispatchedAt.Value;
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
        CancellationToken cancellationToken)
    {
        if (history.TaskStatus != "pending")
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        history.TaskStatus = "failed";
        history.ErrorCode = errorCode;
        history.FinishTime = now;
        history.LastEventAt = now;
        auditWriter.Add(
            AuditActions.TaskFailed,
            history.RequestId,
            history.UserId,
            history.AgentId,
            "task",
            history.TaskId.ToString(),
            "failed",
            errorCode);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private static bool IsExpired(ServiceOperationHistory history, DateTimeOffset now)
    {
        var lastActivity = history.StartTime is null
            ? history.DispatchedAt ?? history.LastEventAt
            : history.LastEventAt;
        var timeout = history.StartTime is null
            ? DispatchAcknowledgementTimeout
            : ExecutionTimeout;
        return now - lastActivity >= timeout;
    }

    private Task<ServiceOperationHistory?> FindTaskAsync(
        Guid taskId,
        Guid agentId,
        CancellationToken cancellationToken)
    {
        return dbContext.ServiceOperationHistories
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
}

public static partial class DeploymentLogSanitizer
{
    public const int MaxLineBytes = 16 * 1024;
    public const int MaxTaskBytes = 1024 * 1024;

    [GeneratedRegex("(?i)(authorization|x-agent-api-key)\\s*[:=]\\s*(?:Bearer\\s+)?[^\\s,;]+(?:\\s+[^\\s,;]+)?", RegexOptions.CultureInvariant)]
    private static partial Regex HeaderSecretPattern();

    [GeneratedRegex("(?i)(password|passwd|pwd|secret|token|api[-_]?key|access[-_]?token|client[-_]?secret)\\s*[:=]\\s*[^\\s,;]+", RegexOptions.CultureInvariant)]
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
