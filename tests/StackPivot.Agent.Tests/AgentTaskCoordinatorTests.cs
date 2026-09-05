using System.Reflection;
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

    [SkippableFact]
    public async Task ConcurrentCommandsForOneTaskExecuteOnlyOnce()
    {
        TestPlatform.RequireLinux();

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

    [SkippableFact]
    public async Task ExecutorExceptionIsReportedAndTaskCanBeReplayedWithoutReexecution()
    {
        TestPlatform.RequireLinux();

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
        Assert.Single(secondReporter.Accepted);
        Assert.Single(secondReporter.Completed);
        Assert.Equal(firstReporter.Completed[0].Success, secondReporter.Completed[0].Success);
        Assert.All(firstCommand.AccessToken, value => Assert.Equal(0, value));
        Assert.All(secondCommand.AccessToken, value => Assert.Equal(0, value));
    }

    [SkippableFact]
    public async Task StreamingExecutorReportsBothStreamsBeforeCompletionAndPreservesTruncation()
    {
        TestPlatform.RequireLinux();

        var coordinator = new AgentTaskCoordinator(AgentId, new StreamingExecutor());
        var reporter = new RecordingReporter();

        await coordinator.HandleAsync(CreateCommand(), reporter, CancellationToken.None);

        Assert.Equal(["stdout:first", "stderr:second"], reporter.Logs.Select(log => log.Stream + ":" + log.Line));
        Assert.Single(reporter.Completed);
        Assert.True(reporter.Completed[0].LogTruncated);
    }

    [Fact]
    public async Task InvalidDispatchFingerprintIsRejectedBeforeExecutionAndReporting()
    {
        var executor = new ThrowingExecutor();
        var coordinator = new AgentTaskCoordinator(AgentId, executor);
        var reporter = new RecordingReporter();
        var command = CreateCommand() with { DispatchFingerprint = new string('0', 64) };

        await coordinator.HandleAsync(command, reporter, CancellationToken.None);

        Assert.Equal(0, executor.ExecutionCount);
        Assert.Empty(reporter.Accepted);
        Assert.Empty(reporter.Logs);
        Assert.Empty(reporter.Completed);
        Assert.All(command.AccessToken, value => Assert.Equal(0, value));
    }

    [SkippableFact]
    public async Task CompletedTaskCacheIsBoundedAndCachedTasksRemainIdempotent()
    {
        TestPlatform.RequireLinux();

        var executor = new LargeOutputExecutor();
        var coordinator = new AgentTaskCoordinator(
            AgentId,
            executor,
            maxCompletedTasks: 1,
            completedTaskTtl: TimeSpan.FromMinutes(1));
        var firstCommand = CreateCommand();
        var firstReporter = new RecordingReporter();

        await coordinator.HandleAsync(firstCommand, firstReporter, CancellationToken.None);
        await coordinator.HandleAsync(firstCommand with { AccessToken = "replay"u8.ToArray() }, firstReporter, CancellationToken.None);

        Assert.Equal(1, executor.ExecutionCount);
        Assert.Equal(2, firstReporter.Completed.Count);

        var evictedCommand = CreateCommand();
        await coordinator.HandleAsync(evictedCommand, new RecordingReporter(), CancellationToken.None);
        Assert.Equal(2, executor.ExecutionCount);

        var replayReporter = new RecordingReporter();
        await coordinator.HandleAsync(firstCommand with { AccessToken = "replayed-after-eviction"u8.ToArray() }, replayReporter, CancellationToken.None);
        Assert.Equal(3, executor.ExecutionCount);
        Assert.Single(replayReporter.Completed);
        Assert.True(replayReporter.Completed[0].Success);

        var completedTasksField = typeof(AgentTaskCoordinator).GetField(
            "completedTasks",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(completedTasksField);
        var completedTasks = completedTasksField.GetValue(coordinator);
        Assert.NotNull(completedTasks);
        Assert.True((int)completedTasks!.GetType().GetProperty("Count")!.GetValue(completedTasks)! <= 1);
    }

    [SkippableFact]
    public async Task ExpiredCompletedTaskIsExecutedAgainInsteadOfReplayed()
    {
        TestPlatform.RequireLinux();

        var executor = new LargeOutputExecutor();
        var coordinator = new AgentTaskCoordinator(
            AgentId,
            executor,
            maxCompletedTasks: 4,
            completedTaskTtl: TimeSpan.FromMilliseconds(25));
        var command = CreateCommand();

        await coordinator.HandleAsync(command, new RecordingReporter(), CancellationToken.None);
        await Task.Delay(TimeSpan.FromMilliseconds(100));

        var replayReporter = new RecordingReporter();
        await coordinator.HandleAsync(command with { AccessToken = "expired-replay"u8.ToArray() }, replayReporter, CancellationToken.None);

        Assert.Equal(2, executor.ExecutionCount);
        Assert.Single(replayReporter.Completed);
        Assert.True(replayReporter.Completed[0].Success);
    }

    private static DeployStackCommand CreateCommand(byte[]? accessToken = null)
    {
        var command = new DeployStackCommand(
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
        return command with { DispatchFingerprint = DispatchFingerprint.Compute(command) };
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

    private sealed class StreamingExecutor : IStackExecutor, IStreamingStackExecutor
    {
        public Task<AgentExecutionResult> ExecuteAsync(DeployStackCommand command, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public async Task<AgentExecutionResult> ExecuteAsync(
            DeployStackCommand command,
            Func<AgentLogEntry, ValueTask> logHandler,
            CancellationToken cancellationToken)
        {
            await logHandler(new AgentLogEntry("stdout", "first"));
            await logHandler(new AgentLogEntry("stderr", "second"));
            return new AgentExecutionResult(true, 0, string.Empty, true);
        }
    }

    private sealed class LargeOutputExecutor : IStackExecutor
    {
        private static readonly string Output = new('x', 1024 * 1024);
        public int ExecutionCount { get; private set; }

        public Task<AgentExecutionResult> ExecuteAsync(
            DeployStackCommand command,
            CancellationToken cancellationToken)
        {
            ExecutionCount++;
            return Task.FromResult(new AgentExecutionResult(true, 0, Output, false));
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
