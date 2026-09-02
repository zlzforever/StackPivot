using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using StackPivot.Agent.Execution;
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
    private readonly ConcurrentDictionary<Guid, Lazy<Task<AgentExecutionResult>>> activeTasks = new();
    private readonly ConcurrentDictionary<Guid, AgentExecutionResult> completedTasks = new();

    public AgentTaskCoordinator(
        Guid agentId,
        IStackExecutor executor,
        string? stackLockDirectory = null)
    {
        if (agentId == Guid.Empty)
        {
            throw new ArgumentException("Agent id is required.", nameof(agentId));
        }

        this.agentId = agentId;
        this.executor = executor ?? throw new ArgumentNullException(nameof(executor));
        this.stackLockDirectory = stackLockDirectory
            ?? Path.Combine(Path.GetTempPath(), "stackpivot-agent-locks");
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
            if (completedTasks.TryGetValue(command.TaskId, out var completed))
            {
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
            var lockResult = StackDeploymentLease.TryAcquire(command.AgentStackLocalPath, stackLockDirectory);
            if (!lockResult.Acquired)
            {
                var failure = new AgentExecutionResult(false, -1, string.Empty, false, lockResult.ErrorCode);
                completedTasks[command.TaskId] = failure;
                await reporter.ReportCompletedAsync(CreateCompleted(command, failure), cancellationToken);
                return failure;
            }

            await using var stackLease = lockResult.Lease!;
            AgentExecutionResult result;
            try
            {
                await reporter.ReportAcceptedAsync(
                    new TaskAccepted(ProtocolVersion.Current, command.TaskId, agentId, DateTimeOffset.UtcNow),
                    cancellationToken);
                result = await ExecuteAndStreamAsync(command, reporter, cancellationToken);

                completedTasks[command.TaskId] = result;
                await reporter.ReportCompletedAsync(CreateCompleted(command, result), cancellationToken);
                return result;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                result = new AgentExecutionResult(false, -1, string.Empty, false, "agent_execution_failed");
                completedTasks[command.TaskId] = result;
                try
                {
                    await reporter.ReportCompletedAsync(CreateCompleted(command, result), CancellationToken.None);
                }
                catch (OperationCanceledException)
                {
                }
                catch
                {
                    // The connection can close while reporting a failed task.
                }

                return result;
            }
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

    private sealed class StackDeploymentLease : IAsyncDisposable
    {
        private readonly FileStream stream;

        private StackDeploymentLease(FileStream stream)
        {
            this.stream = stream;
        }

        public static LeaseResult TryAcquire(string? stackPath, string lockDirectory)
        {
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

            try
            {
                Directory.CreateDirectory(lockDirectory);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                return new LeaseResult(false, null, "stack_lock_unavailable");
            }

            var pathBytes = Encoding.UTF8.GetBytes(normalizedPath);
            var lockName = Convert.ToHexString(SHA256.HashData(pathBytes)) + ".lock";
            CryptographicOperations.ZeroMemory(pathBytes);
            var lockPath = Path.Combine(lockDirectory, lockName);
            try
            {
                var stream = new FileStream(
                    lockPath,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    bufferSize: 1,
                    options: FileOptions.SequentialScan);
                return new LeaseResult(true, new StackDeploymentLease(stream), null);
            }
            catch (IOException)
            {
                return new LeaseResult(false, null, "stack_busy");
            }
            catch (Exception exception) when (exception is UnauthorizedAccessException or ArgumentException or NotSupportedException)
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
