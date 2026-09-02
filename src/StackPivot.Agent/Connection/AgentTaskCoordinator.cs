using System.Collections.Concurrent;
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
    private readonly ConcurrentDictionary<Guid, Lazy<Task<AgentExecutionResult>>> activeTasks = new();
    private readonly ConcurrentDictionary<Guid, AgentExecutionResult> completedTasks = new();

    public AgentTaskCoordinator(Guid agentId, IStackExecutor executor)
    {
        if (agentId == Guid.Empty)
        {
            throw new ArgumentException("Agent id is required.", nameof(agentId));
        }

        this.agentId = agentId;
        this.executor = executor ?? throw new ArgumentNullException(nameof(executor));
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
}
