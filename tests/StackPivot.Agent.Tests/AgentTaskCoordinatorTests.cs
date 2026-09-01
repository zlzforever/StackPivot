using StackPivot.Agent;
using StackPivot.Agent.Connection;
using StackPivot.Agent.Execution;
using StackPivot.Contracts.Agents;
using StackPivot.Contracts.Deployments;
using StackPivot.Contracts.SignalR;
using Xunit;

namespace StackPivot.Agent.Tests;

public sealed class AgentTaskCoordinatorTests
{
    private static readonly Guid AgentId = Guid.Parse("00000000-0000-0000-0000-000000000001");

    [Fact]
    public async Task ConcurrentCommandsForOneTaskExecuteOnlyOnce()
    {
        var executor = new BlockingExecutor();
        var coordinator = new AgentTaskCoordinator(AgentId, executor);
        var firstReporter = new RecordingReporter();
        var secondReporter = new RecordingReporter();
        var firstCommand = CreateCommand();
        var firstToken = firstCommand.AccessToken;
        var secondCommand = CreateCommand();
        secondCommand = secondCommand with { TaskId = firstCommand.TaskId };

        var first = coordinator.HandleAsync(firstCommand, firstReporter, CancellationToken.None);
        await executor.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = coordinator.HandleAsync(secondCommand, secondReporter, CancellationToken.None);
        executor.Release.TrySetResult(true);

        await Task.WhenAll(first, second);

        Assert.Equal(1, executor.ExecutionCount);
        Assert.Single(firstReporter.Accepted);
        Assert.Single(firstReporter.Completed);
        Assert.Empty(secondReporter.Accepted);
        Assert.Empty(secondReporter.Completed);
        Assert.All(firstToken, value => Assert.Equal(0, value));
        Assert.All(secondCommand.AccessToken, value => Assert.Equal(0, value));
    }

    [Fact]
    public async Task ExecutorExceptionIsReportedAndTaskCanBeReplayedWithoutReexecution()
    {
        var executor = new ThrowingExecutor();
        var coordinator = new AgentTaskCoordinator(AgentId, executor);
        var firstReporter = new RecordingReporter();
        var secondReporter = new RecordingReporter();
        var firstCommand = CreateCommand();
        var secondCommand = CreateCommand();
        secondCommand = secondCommand with { TaskId = firstCommand.TaskId };

        await coordinator.HandleAsync(firstCommand, firstReporter, CancellationToken.None);
        await coordinator.HandleAsync(secondCommand, secondReporter, CancellationToken.None);

        Assert.Equal(1, executor.ExecutionCount);
        Assert.Single(firstReporter.Completed);
        Assert.False(firstReporter.Completed[0].Success);
        Assert.Equal("agent_execution_failed", firstReporter.Completed[0].ErrorCode);
        Assert.Single(secondReporter.Completed);
        Assert.Equal(firstReporter.Completed[0].Success, secondReporter.Completed[0].Success);
        Assert.All(firstCommand.AccessToken, value => Assert.Equal(0, value));
        Assert.All(secondCommand.AccessToken, value => Assert.Equal(0, value));
    }

    private static DeployStackCommand CreateCommand(byte[]? accessToken = null)
    {
        return new DeployStackCommand(
            ProtocolVersion.Current,
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            AgentId,
            "https://git.example/repository.git",
            "git-user",
            accessToken ?? "secret"u8.ToArray(),
            "0123456789abcdef0123456789abcdef01234567",
            "workspace_one/stack_web",
            "/opt/agent-main/workspace_one/stack_web",
            DateTimeOffset.UtcNow.AddMinutes(5));
    }

    private sealed class BlockingExecutor : IStackExecutor
    {
        public TaskCompletionSource<bool> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int ExecutionCount { get; private set; }

        public async Task<AgentExecutionResult> ExecuteAsync(DeployStackCommand command, CancellationToken cancellationToken)
        {
            ExecutionCount++;
            Started.TrySetResult(true);
            await Release.Task.WaitAsync(cancellationToken);
            return new AgentExecutionResult(true, 0, string.Empty, false);
        }
    }

    private sealed class ThrowingExecutor : IStackExecutor
    {
        public int ExecutionCount { get; private set; }

        public Task<AgentExecutionResult> ExecuteAsync(DeployStackCommand command, CancellationToken cancellationToken)
        {
            ExecutionCount++;
            throw new InvalidOperationException("test failure");
        }
    }

    private sealed class RecordingReporter : IAgentTaskReporter
    {
        public List<TaskAccepted> Accepted { get; } = new();
        public List<TaskLog> Logs { get; } = new();
        public List<TaskCompleted> Completed { get; } = new();

        public Task ReportAcceptedAsync(TaskAccepted accepted, CancellationToken cancellationToken)
        {
            Accepted.Add(accepted);
            return Task.CompletedTask;
        }

        public Task ReportLogAsync(TaskLog log, CancellationToken cancellationToken)
        {
            Logs.Add(log);
            return Task.CompletedTask;
        }

        public Task ReportCompletedAsync(TaskCompleted completed, CancellationToken cancellationToken)
        {
            Completed.Add(completed);
            return Task.CompletedTask;
        }
    }
}
