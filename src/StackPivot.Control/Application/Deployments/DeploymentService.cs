using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using StackPivot.Control.Application.Audit;
using StackPivot.Control.Authorization;
using StackPivot.Control.Domain.Entities;
using StackPivot.Control.Infrastructure.Git;
using StackPivot.Control.Infrastructure.Persistence;
using StackPivot.Contracts.Deployments;
using StackPivot.Contracts.SignalR;

namespace StackPivot.Control.Application.Deployments;

public interface IDeploymentService
{
    Task<DeploymentRequestResult> RequestAsync(
        Guid userId,
        Guid stackId,
        DeployStackRequest request,
        CancellationToken cancellationToken);

    Task<DeploymentRequestView?> GetRequestAsync(
        Guid userId,
        Guid requestId,
        CancellationToken cancellationToken);
}

public interface IAgentTransport
{
    Task<bool> IsConnectedAsync(Guid agentId, CancellationToken cancellationToken);
    Task SendDeployAsync(DeployStackCommand command, CancellationToken cancellationToken);
}

public sealed class DeploymentRequestException(string code, int statusCode, string message) : Exception(message)
{
    public string Code { get; } = code;
    public int StatusCode { get; } = statusCode;
}

public sealed class DeploymentService(
    StackPivotDbContext dbContext,
    WorkspaceAuthorizationService authorization,
    ICentralGitPreflight preflight,
    AuditWriter auditWriter) : IDeploymentService
{
    public Task<DeploymentRequestResult> RequestAsync(
        Guid userId,
        Guid stackId,
        DeployStackRequest request,
        CancellationToken cancellationToken)
    {
        return RequestAsync(
            userId,
            stackId,
            request,
            Guid.NewGuid(),
            Guid.NewGuid(),
            cancellationToken);
    }

    public async Task<DeploymentRequestResult> RequestAsync(
        Guid userId,
        Guid stackId,
        DeployStackRequest request,
        Guid requestId,
        Guid idempotencyKey,
        CancellationToken cancellationToken)
    {
        return await RequestCoreAsync(userId, stackId, request, requestId, idempotencyKey, cancellationToken);
    }

    private async Task<DeploymentRequestResult> RequestCoreAsync(
        Guid userId,
        Guid stackId,
        DeployStackRequest request,
        Guid requestId,
        Guid idempotencyKey,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (userId == Guid.Empty || stackId == Guid.Empty || requestId == Guid.Empty || idempotencyKey == Guid.Empty)
        {
            throw new DeploymentRequestException("invalid_request", 422, "Request identifiers are required.");
        }

        var stack = await dbContext.Stacks
            .AsNoTracking()
            .SingleOrDefaultAsync(value => value.StackId == stackId, cancellationToken);
        if (stack is null)
        {
            throw new DeploymentRequestException("resource_not_found", 404, "Stack was not found.");
        }

        var readAccess = await authorization.RequireAsync(
            userId,
            stack.WorkspaceId,
            WorkspacePermission.ReadOnly,
            cancellationToken);
        if (!readAccess.IsAllowed)
        {
            throw new DeploymentRequestException("resource_not_found", 404, "Stack was not found.");
        }

        var editorAccess = await authorization.RequireAsync(
            userId,
            stack.WorkspaceId,
            WorkspacePermission.Editor,
            cancellationToken);
        if (!editorAccess.IsAllowed)
        {
            throw new DeploymentRequestException("insufficient_permission", 403, "Editor permission is required.");
        }

        var validation = request.Validate();
        if (!validation.IsValid)
        {
            throw new DeploymentRequestException(validation.Errors[0], 422, "Deployment request is invalid.");
        }

        var fingerprint = ComputeFingerprint(stackId, request);
        var existing = await dbContext.DeploymentRequests
            .AsNoTracking()
            .SingleOrDefaultAsync(
                value => value.UserId == userId && value.IdempotencyKey == idempotencyKey.ToString(),
                cancellationToken);
        if (existing is not null)
        {
            if (!string.Equals(existing.RequestFingerprint, fingerprint, StringComparison.Ordinal))
            {
                throw new DeploymentRequestException(
                    "idempotency_key_reused",
                    409,
                    "Idempotency key was used with a different request.");
            }

            return await BuildResultAsync(existing.RequestId, cancellationToken);
        }

        var bindings = await dbContext.StackAgentBindings
            .Include(value => value.Agent)
            .Where(value => value.StackId == stackId)
            .ToListAsync(cancellationToken);
        var targets = ResolveTargets(request, bindings);
        if (targets.Count == 0)
        {
            throw new DeploymentRequestException("invalid_target", 422, "No bound deployment target exists.");
        }

        var targetIds = targets.Select(value => value.AgentId).ToArray();
        var active = await dbContext.ServiceOperationHistories
            .AnyAsync(
                value => value.StackId == stackId
                    && value.TaskStatus == "pending"
                    && targetIds.Contains(value.AgentId),
                cancellationToken);
        if (active)
        {
            throw new DeploymentRequestException("deployment_in_progress", 409, "Deployment is already running.");
        }

        var checkedOut = await preflight.ValidateAsync(stackId, request.TargetCommitHash, cancellationToken);
        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        existing = await dbContext.DeploymentRequests
            .SingleOrDefaultAsync(
                value => value.UserId == userId && value.IdempotencyKey == idempotencyKey.ToString(),
                cancellationToken);
        if (existing is not null)
        {
            if (!string.Equals(existing.RequestFingerprint, fingerprint, StringComparison.Ordinal))
            {
                throw new DeploymentRequestException(
                    "idempotency_key_reused",
                    409,
                    "Idempotency key was used with a different request.");
            }

            await transaction.CommitAsync(cancellationToken);
            return await BuildResultAsync(existing.RequestId, cancellationToken);
        }

        var now = DateTimeOffset.UtcNow;
        var deployment = new DeploymentRequestEntity
        {
            RequestId = requestId,
            StackId = stackId,
            UserId = userId,
            IdempotencyKey = idempotencyKey.ToString(),
            RequestFingerprint = fingerprint,
            TargetCommitHash = request.TargetCommitHash,
            Mode = request.Mode,
            CreatedAt = now
        };
        dbContext.DeploymentRequests.Add(deployment);
        foreach (var target in targets)
        {
            deployment.Operations.Add(new ServiceOperationHistory
            {
                HistoryId = Guid.NewGuid(),
                TaskId = Guid.NewGuid(),
                RequestId = requestId,
                StackId = stackId,
                AgentId = target.AgentId,
                UserId = userId,
                TargetCommitHash = request.TargetCommitHash,
                TokenKeyId = checkedOut.TokenKeyId,
                GitRepoSnapshot = checkedOut.GitRepo,
                GitUserNameSnapshot = checkedOut.GitUserName,
                StackGitRelativePathSnapshot = checkedOut.StackGitRelativePath,
                AgentStackLocalPathSnapshot = checkedOut.AgentStackLocalPath,
                TaskStatus = "pending",
                CommandText = "docker compose up -d",
                OutputLog = string.Empty,
                LastSequence = -1,
                LastEventAt = now
            });
        }

        auditWriter.Add(
            AuditActions.DeployRequested,
            requestId,
            userId,
            null,
            "stack",
            stackId.ToString(),
            "accepted");
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (IsActiveTaskConflict(exception))
        {
            throw new DeploymentRequestException("deployment_in_progress", 409, "Deployment is already running.");
        }

        _ = checkedOut;
        return await BuildResultAsync(requestId, cancellationToken);
    }

    public async Task<DeploymentRequestView?> GetRequestAsync(
        Guid userId,
        Guid requestId,
        CancellationToken cancellationToken)
    {
        var request = await dbContext.DeploymentRequests
            .AsNoTracking()
            .Include(value => value.Stack)
            .SingleOrDefaultAsync(value => value.RequestId == requestId, cancellationToken);
        if (request?.Stack is null)
        {
            return null;
        }

        var access = await authorization.RequireAsync(
            userId,
            request.Stack.WorkspaceId,
            WorkspacePermission.ReadOnly,
            cancellationToken);
        if (!access.IsAllowed)
        {
            return null;
        }

        return await BuildViewAsync(request, cancellationToken);
    }

    public async Task<IReadOnlyList<DeploymentTaskView>?> GetOperationsAsync(
        Guid userId,
        Guid stackId,
        int limit,
        CancellationToken cancellationToken)
    {
        var stack = await dbContext.Stacks
            .AsNoTracking()
            .SingleOrDefaultAsync(value => value.StackId == stackId, cancellationToken);
        if (stack is null)
        {
            return null;
        }

        var access = await authorization.RequireAsync(
            userId,
            stack.WorkspaceId,
            WorkspacePermission.ReadOnly,
            cancellationToken);
        if (!access.IsAllowed)
        {
            return null;
        }

        var page = await GetOperationsPageAsync(userId, stackId, limit, null, cancellationToken);
        return page?.Items;
    }

    public async Task<DeploymentOperationsPage?> GetOperationsPageAsync(
        Guid userId,
        Guid stackId,
        int limit,
        string? cursor,
        CancellationToken cancellationToken)
    {
        var stack = await dbContext.Stacks
            .AsNoTracking()
            .SingleOrDefaultAsync(value => value.StackId == stackId, cancellationToken);
        if (stack is null)
        {
            return null;
        }

        var access = await authorization.RequireAsync(
            userId,
            stack.WorkspaceId,
            WorkspacePermission.ReadOnly,
            cancellationToken);
        if (!access.IsAllowed)
        {
            return null;
        }

        OperationsCursor? decodedCursor = null;
        if (!string.IsNullOrWhiteSpace(cursor))
        {
            decodedCursor = DecodeCursor(cursor);
        }

        limit = Math.Clamp(limit, 1, 100);
        var query = dbContext.ServiceOperationHistories
            .AsNoTracking()
            .Where(value => value.StackId == stackId)
            .AsQueryable();
        if (decodedCursor is not null)
        {
            query = query.Where(value => value.LastEventAt < decodedCursor.LastEventAt
                || value.LastEventAt == decodedCursor.LastEventAt
                    && value.HistoryId.CompareTo(decodedCursor.HistoryId) < 0);
        }

        var histories = await query
            .OrderByDescending(value => value.LastEventAt)
            .ThenByDescending(value => value.HistoryId)
            .Take(limit + 1)
            .ToListAsync(cancellationToken);
        var hasMore = histories.Count > limit;
        if (hasMore)
        {
            histories.RemoveAt(histories.Count - 1);
        }

        var items = histories.Select(ToView).ToList();
        var nextCursor = hasMore && histories.Count > 0
            ? EncodeCursor(new OperationsCursor(histories[^1].LastEventAt, histories[^1].HistoryId))
            : null;
        return new DeploymentOperationsPage(items, nextCursor);
    }

    private async Task<DeploymentRequestResult> BuildResultAsync(
        Guid requestId,
        CancellationToken cancellationToken)
    {
        var request = await dbContext.DeploymentRequests
            .AsNoTracking()
            .SingleAsync(value => value.RequestId == requestId, cancellationToken);
        var tasks = await dbContext.ServiceOperationHistories
            .AsNoTracking()
            .Where(value => value.RequestId == requestId)
            .OrderBy(value => value.AgentId)
            .Select(value => new DeploymentTaskResult(
                value.TaskId,
                value.AgentId,
                value.TaskStatus,
                value.ErrorCode))
            .ToListAsync(cancellationToken);
        return new DeploymentRequestResult(request.RequestId, request.StackId, request.TargetCommitHash, tasks);
    }

    private async Task<DeploymentRequestView> BuildViewAsync(
        DeploymentRequestEntity request,
        CancellationToken cancellationToken)
    {
        var histories = await dbContext.ServiceOperationHistories
            .AsNoTracking()
            .Where(value => value.RequestId == request.RequestId)
            .OrderBy(value => value.AgentId)
            .ToListAsync(cancellationToken);
        var tasks = histories.Select(ToView).ToList();
        return new DeploymentRequestView(request.RequestId, tasks);
    }

    private static List<AgentNode> ResolveTargets(
        DeployStackRequest request,
        IReadOnlyCollection<StackAgentBinding> bindings)
    {
        return request.Mode switch
        {
            DeploymentMode.BoundAgents => bindings
                .Where(value => value.Agent is not null && value.Agent.RevokedAt is null)
                .Select(value => value.Agent!)
                .ToList(),
            DeploymentMode.SingleAgent => bindings
                .Where(value => value.AgentId == request.AgentId && value.Agent is not null && value.Agent.RevokedAt is null)
                .Select(value => value.Agent!)
                .ToList(),
            _ => new List<AgentNode>()
        };
    }

    private static string ComputeFingerprint(Guid stackId, DeployStackRequest request)
    {
        var bytes = Encoding.UTF8.GetBytes($"{stackId:D}:{ProtocolJson.Serialize(request)}");
        var digest = SHA256.HashData(bytes);
        CryptographicOperations.ZeroMemory(bytes);
        return Convert.ToHexString(digest).ToLowerInvariant();
    }

    private static bool IsActiveTaskConflict(DbUpdateException exception)
    {
        return exception.InnerException is Microsoft.Data.Sqlite.SqliteException sqlite
            && sqlite.SqliteErrorCode == 19
            && sqlite.Message.Contains("service_operation_history", StringComparison.OrdinalIgnoreCase);
    }

    private static IReadOnlyList<DeploymentLogEntryView> ParseLogEntries(string json)
    {
        try
        {
            return System.Text.Json.JsonSerializer.Deserialize<List<DeploymentLogEntryView>>(json)
                ?? new List<DeploymentLogEntryView>();
        }
        catch (System.Text.Json.JsonException)
        {
            return Array.Empty<DeploymentLogEntryView>();
        }
    }

    private static string EncodeCursor(OperationsCursor cursor)
    {
        var json = JsonSerializer.Serialize(cursor);
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(json))
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');
    }

    private static OperationsCursor DecodeCursor(string cursor)
    {
        try
        {
            var padded = cursor.Replace('-', '+').Replace('_', '/');
            padded = padded.PadRight(padded.Length + (4 - padded.Length % 4) % 4, '=');
            var json = Encoding.UTF8.GetString(Convert.FromBase64String(padded));
            return JsonSerializer.Deserialize<OperationsCursor>(json)
                ?? throw new FormatException();
        }
        catch (Exception exception) when (exception is FormatException or JsonException or ArgumentException)
        {
            throw new DeploymentRequestException("invalid_cursor", 422, "Deployment history cursor is invalid.");
        }
    }

    private sealed record OperationsCursor(DateTimeOffset LastEventAt, Guid HistoryId);

    private static DeploymentTaskView ToView(ServiceOperationHistory value)
    {
        return new DeploymentTaskView(
            value.TaskId,
            value.AgentId,
            value.OperationType,
            value.TargetCommitHash,
            value.TaskStatus,
            value.CommandText,
            value.ExitCode,
            value.StartTime,
            value.FinishTime,
            value.OutputLog,
            value.LogTruncated,
            value.ErrorCode,
            ParseLogEntries(value.OutputLogEntriesJson));
    }
}
