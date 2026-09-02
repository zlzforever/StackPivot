using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using StackPivot.Agent.Execution;
using StackPivot.Agent.Security;
using StackPivot.Contracts.Agents;
using StackPivot.Contracts.Deployments;
using StackPivot.Contracts.SignalR;

namespace StackPivot.Agent.Connection;

public interface IAgentTaskReporter
{
    Task ReportAcceptedAsync(TaskAccepted accepted, CancellationToken cancellationToken);
    Task ReportLogAsync(TaskLog log, CancellationToken cancellationToken);
    Task ReportCompletedAsync(TaskCompleted completed, CancellationToken cancellationToken);
}

public sealed class AgentTaskCoordinator
{
    private readonly Guid agentId;
    private readonly IStackExecutor executor;
    private readonly string stackLockDirectory;
    private readonly int maxCompletedTasks;
    private readonly TimeSpan completedTaskTtl;
    private readonly object completedTasksGate = new();
    private readonly ConcurrentDictionary<Guid, Lazy<Task<AgentExecutionResult>>> activeTasks = new();
    private readonly ConcurrentDictionary<Guid, CachedExecutionResult> completedTasks = new();

    public AgentTaskCoordinator(
        Guid agentId,
        IStackExecutor executor,
        string? stackLockDirectory = null,
        int maxCompletedTasks = 1024,
        TimeSpan? completedTaskTtl = null)
    {
        if (agentId == Guid.Empty)
        {
            throw new ArgumentException("Agent id is required.", nameof(agentId));
        }

        this.agentId = agentId;
        this.executor = executor ?? throw new ArgumentNullException(nameof(executor));
        this.stackLockDirectory = stackLockDirectory
            ?? Path.Combine(Path.GetTempPath(), "stackpivot-agent-locks");
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxCompletedTasks);

        this.maxCompletedTasks = maxCompletedTasks;
        this.completedTaskTtl = completedTaskTtl ?? TimeSpan.FromMinutes(15);
        if (this.completedTaskTtl <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(completedTaskTtl));
        }
    }

    public async Task HandleAsync(
        DeployStackCommand command,
        IAgentTaskReporter reporter,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(reporter);

        try
        {
            if (TryGetCompleted(command.TaskId, out var completed))
            {
                await reporter.ReportAcceptedAsync(
                    new TaskAccepted(ProtocolVersion.Current, command.TaskId, agentId, DateTimeOffset.UtcNow),
                    cancellationToken);
                await reporter.ReportCompletedAsync(CreateCompleted(command, completed), cancellationToken);
                return;
            }

            var candidate = new Lazy<Task<AgentExecutionResult>>(
                () => ExecuteAndReportAsync(command, reporter, cancellationToken),
                LazyThreadSafetyMode.ExecutionAndPublication);
            var execution = activeTasks.GetOrAdd(command.TaskId, candidate);
            await execution.Value;
        }
        finally
        {
            command.ClearAccessToken();
        }
    }

    private async Task<AgentExecutionResult> ExecuteAndReportAsync(
        DeployStackCommand command,
        IAgentTaskReporter reporter,
        CancellationToken cancellationToken)
    {
        try
        {
            if (!OperatingSystem.IsLinux())
            {
                var failure = new AgentExecutionResult(false, -1, string.Empty, false, "platform_unsupported");
                await reporter.ReportAcceptedAsync(
                    new TaskAccepted(ProtocolVersion.Current, command.TaskId, agentId, DateTimeOffset.UtcNow),
                    cancellationToken);
                CacheCompleted(command.TaskId, failure);
                await reporter.ReportCompletedAsync(CreateCompleted(command, failure), cancellationToken);
                return failure;
            }

            var lockResult = StackDeploymentLease.TryAcquire(command.AgentStackLocalPath, stackLockDirectory);
            if (!lockResult.Acquired)
            {
                var failure = new AgentExecutionResult(false, -1, string.Empty, false, lockResult.ErrorCode);
                await reporter.ReportAcceptedAsync(
                    new TaskAccepted(ProtocolVersion.Current, command.TaskId, agentId, DateTimeOffset.UtcNow),
                    cancellationToken);
                CacheCompleted(command.TaskId, failure);
                await reporter.ReportCompletedAsync(CreateCompleted(command, failure), cancellationToken);
                return failure;
            }

            await using var stackLease = lockResult.Lease!;
            await reporter.ReportAcceptedAsync(
                new TaskAccepted(ProtocolVersion.Current, command.TaskId, agentId, DateTimeOffset.UtcNow),
                cancellationToken);

            AgentExecutionResult result;
            try
            {
                result = await ExecuteAndStreamAsync(command, reporter, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                result = new AgentExecutionResult(false, -1, string.Empty, false, "agent_execution_failed");
            }

            CacheCompleted(command.TaskId, result);
            await reporter.ReportCompletedAsync(CreateCompleted(command, result), cancellationToken);
            return result;
        }
        finally
        {
            activeTasks.TryRemove(command.TaskId, out _);
            command.ClearAccessToken();
        }
    }

    private async Task<AgentExecutionResult> ExecuteAndStreamAsync(
        DeployStackCommand command,
        IAgentTaskReporter reporter,
        CancellationToken cancellationToken)
    {
        var sequence = 0L;
        if (executor is IStreamingStackExecutor streamingExecutor)
        {
            using var logLock = new SemaphoreSlim(1, 1);
            return await streamingExecutor.ExecuteAsync(
                command,
                async entry =>
                {
                    if (entry.Stream is not ("stdout" or "stderr"))
                    {
                        return;
                    }

                    await logLock.WaitAsync(cancellationToken);
                    try
                    {
                        await reporter.ReportLogAsync(
                            new TaskLog(
                                ProtocolVersion.Current,
                                command.TaskId,
                                agentId,
                                sequence++,
                                entry.Stream,
                                entry.Line,
                                DateTimeOffset.UtcNow),
                            cancellationToken);
                    }
                    finally
                    {
                        logLock.Release();
                    }
                },
                cancellationToken);
        }

        var result = await ExecuteSafelyAsync(command, cancellationToken);
        foreach (var line in result.OutputLog.Split('\n', StringSplitOptions.None))
        {
            await reporter.ReportLogAsync(
                new TaskLog(
                    ProtocolVersion.Current,
                    command.TaskId,
                    agentId,
                    sequence++,
                    "stdout",
                    line,
                    DateTimeOffset.UtcNow),
                cancellationToken);
        }

        return result;
    }

    private async Task<AgentExecutionResult> ExecuteSafelyAsync(
        DeployStackCommand command,
        CancellationToken cancellationToken)
    {
        try
        {
            return await executor.ExecuteAsync(command, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return new AgentExecutionResult(false, -1, string.Empty, false, "agent_execution_failed");
        }
    }

    private static TaskCompleted CreateCompleted(
        DeployStackCommand command,
        AgentExecutionResult result)
    {
        return new TaskCompleted(
            ProtocolVersion.Current,
            command.TaskId,
            command.AgentId,
            result.Success,
            result.ExitCode,
            result.ErrorCode,
            DateTimeOffset.UtcNow,
            result.LogTruncated);
    }

    private bool TryGetCompleted(Guid taskId, out AgentExecutionResult result)
    {
        lock (completedTasksGate)
        {
            if (!completedTasks.TryGetValue(taskId, out var cached))
            {
                result = null!;
                return false;
            }

            if (cached.ExpiresAt <= DateTimeOffset.UtcNow)
            {
                completedTasks.TryRemove(taskId, out _);
                result = null!;
                return false;
            }

            result = cached.Result;
            return true;
        }
    }

    private void CacheCompleted(Guid taskId, AgentExecutionResult result)
    {
        var cached = new CachedExecutionResult(
            result with { OutputLog = string.Empty },
            DateTimeOffset.UtcNow.Add(completedTaskTtl));
        lock (completedTasksGate)
        {
            var now = DateTimeOffset.UtcNow;
            foreach (var pair in completedTasks)
            {
                if (pair.Value.ExpiresAt <= now)
                {
                    completedTasks.TryRemove(pair.Key, out _);
                }
            }

            completedTasks[taskId] = cached;
            while (completedTasks.Count > maxCompletedTasks)
            {
                var oldest = completedTasks
                    .OrderBy(pair => pair.Value.ExpiresAt)
                    .First();
                completedTasks.TryRemove(oldest.Key, out _);
            }
        }
    }

    private sealed record CachedExecutionResult(
        AgentExecutionResult Result,
        DateTimeOffset ExpiresAt);

    private sealed class StackDeploymentLease : IAsyncDisposable
    {
        private readonly FileStream stream;

        private StackDeploymentLease(FileStream stream)
        {
            this.stream = stream;
        }

        public static LeaseResult TryAcquire(string? stackPath, string lockDirectory)
        {
            if (!OperatingSystem.IsLinux())
            {
                return new LeaseResult(false, null, "stack_lock_unavailable");
            }

            if (string.IsNullOrWhiteSpace(stackPath))
            {
                return new LeaseResult(false, null, "invalid_path");
            }

            string normalizedPath;
            try
            {
                normalizedPath = Path.GetFullPath(stackPath);
            }
            catch (ArgumentException)
            {
                return new LeaseResult(false, null, "invalid_path");
            }

            var pathBytes = Encoding.UTF8.GetBytes(normalizedPath);
            var lockName = Convert.ToHexString(SHA256.HashData(pathBytes)) + ".lock";
            CryptographicOperations.ZeroMemory(pathBytes);
            try
            {
                using var lockDirectoryHandle = SafeDirectoryHandle.OpenOrCreateAbsoluteDirectory(lockDirectory);
                var stream = lockDirectoryHandle.OpenLockFile(lockName);
                return new LeaseResult(true, new StackDeploymentLease(stream), null);
            }
            catch (PathPolicyException exception) when (exception.ErrorNumber == LinuxPathOperations.ResourceBusy)
            {
                return new LeaseResult(false, null, "stack_busy");
            }
            catch (Exception exception) when (exception is PathPolicyException
                or IOException
                or UnauthorizedAccessException
                or ArgumentException
                or NotSupportedException)
            {
                return new LeaseResult(false, null, "stack_lock_unavailable");
            }
        }

        public ValueTask DisposeAsync()
        {
            stream.Dispose();
            return ValueTask.CompletedTask;
        }

        public sealed record LeaseResult(bool Acquired, StackDeploymentLease? Lease, string? ErrorCode);
    }
}
