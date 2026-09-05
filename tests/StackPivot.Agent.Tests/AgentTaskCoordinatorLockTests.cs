using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using StackPivot.Agent.Connection;
using StackPivot.Agent.Execution;
using StackPivot.Contracts.Agents;
using StackPivot.Contracts.Deployments;
using StackPivot.Contracts.SignalR;
using System.Reflection;
using Xunit;

namespace StackPivot.Agent.Tests;

public sealed class AgentTaskCoordinatorLockTests
{
    private static readonly Guid AgentId = Guid.Parse("00000000-0000-0000-0000-000000000001");

    [SkippableFact]
    public async Task DifferentTaskIdsForOneStackAreSerializedAcrossCoordinatorInstances()
    {
        TestPlatform.RequireLinux();

        var root = Path.Combine(Path.GetTempPath(), "stackpivot-lock-" + Guid.NewGuid().ToString("N"));
        var stackPath = Path.Combine(root, "workspace_one", "stack_web");
        var lockDirectory = Path.Combine(root, "locks");
        var firstExecutor = new BlockingExecutor();
        var secondExecutor = new ImmediateExecutor();
        var firstCoordinator = new AgentTaskCoordinator(AgentId, firstExecutor, lockDirectory);
        var secondCoordinator = new AgentTaskCoordinator(AgentId, secondExecutor, lockDirectory);
        var firstReporter = new RecordingReporter();
        var secondReporter = new RecordingReporter();
        var first = firstCoordinator.HandleAsync(CreateCommand(stackPath), firstReporter, CancellationToken.None);

        await firstExecutor.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var second = secondCoordinator.HandleAsync(CreateCommand(stackPath), secondReporter, CancellationToken.None);
        try
        {
            Assert.Same(second, await Task.WhenAny(second, Task.Delay(TimeSpan.FromSeconds(5))));
            Assert.Equal(0, secondExecutor.ExecutionCount);
            Assert.Single(secondReporter.Accepted);
            Assert.Equal(secondReporter.Completed[0].TaskId, secondReporter.Accepted[0].TaskId);
            var busy = Assert.Single(secondReporter.Completed);
            Assert.False(busy.Success);
            Assert.Equal("stack_busy", busy.ErrorCode);
        }
        finally
        {
            firstExecutor.Release.TrySetResult(true);
            await Task.WhenAll(first, second);
            DeleteDirectory(root);
        }

        Assert.Equal(1, firstExecutor.ExecutionCount);
        Assert.True(Assert.Single(firstReporter.Completed).Success);
    }

    [SkippableFact]
    public async Task BusyFailureReporterDoesNotLeaveTheTaskPermanentlyActive()
    {
        TestPlatform.RequireLinux();

        var root = Path.Combine(Path.GetTempPath(), "stackpivot-lock-replay-" + Guid.NewGuid().ToString("N"));
        var stackPath = Path.Combine(root, "workspace_one", "stack_web");
        var lockDirectory = Path.Combine(root, "locks");
        var firstExecutor = new BlockingExecutor();
        var firstCoordinator = new AgentTaskCoordinator(AgentId, firstExecutor, lockDirectory);
        var secondCoordinator = new AgentTaskCoordinator(AgentId, new ImmediateExecutor(), lockDirectory);
        var firstReporter = new RecordingReporter();
        var command = CreateCommand(stackPath);
        var first = firstCoordinator.HandleAsync(command, firstReporter, CancellationToken.None);

        try
        {
            await firstExecutor.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));

            var failingReporter = new ThrowingCompletedReporter();
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                secondCoordinator.HandleAsync(command, failingReporter, CancellationToken.None));

            var activeTasksField = typeof(AgentTaskCoordinator).GetField(
                "activeTasks",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(activeTasksField);
            var activeTasks = activeTasksField.GetValue(secondCoordinator);
            Assert.NotNull(activeTasks);
            Assert.Equal(0, (int)activeTasks!.GetType().GetProperty("Count")!.GetValue(activeTasks)!);

            firstExecutor.Release.TrySetResult(true);
            await first;

            var replayReporter = new RecordingReporter();
            await secondCoordinator.HandleAsync(command, replayReporter, CancellationToken.None);

            var replay = Assert.Single(replayReporter.Completed);
            var replayAccepted = Assert.Single(replayReporter.Accepted);
            Assert.Equal(replay.TaskId, replayAccepted.TaskId);
            Assert.False(replay.Success);
            Assert.Equal("stack_busy", replay.ErrorCode);
        }
        finally
        {
            firstExecutor.Release.TrySetResult(true);
            await first;
            DeleteDirectory(root);
        }
    }

    [SkippableFact]
    public async Task PreexistingFifoLockIsRejectedBeforeTaskExecution()
    {
        TestPlatform.RequireLinux();

        var root = Path.Combine(Path.GetTempPath(), "stackpivot-lock-fifo-" + Guid.NewGuid().ToString("N"));
        var stackPath = Path.Combine(root, "workspace_one", "stack_web");
        var lockDirectory = Path.Combine(root, "locks");
        Directory.CreateDirectory(lockDirectory);
        var lockName = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(stackPath)))) + ".lock";
        var lockPath = Path.Combine(lockDirectory, lockName);
        var executor = new ImmediateExecutor();
        var coordinator = new AgentTaskCoordinator(AgentId, executor, lockDirectory);
        var reporter = new RecordingReporter();

        try
        {
            Assert.Equal(0, mkfifo(lockPath, 0x180u));

            await coordinator.HandleAsync(CreateCommand(stackPath), reporter, CancellationToken.None);

            var accepted = Assert.Single(reporter.Accepted);
            var completed = Assert.Single(reporter.Completed);
            Assert.Equal(accepted.TaskId, completed.TaskId);
            Assert.False(completed.Success);
            Assert.Equal("stack_lock_unavailable", completed.ErrorCode);
            Assert.Equal(0, executor.ExecutionCount);
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [SkippableFact]
    public async Task LockUnavailableIsAcceptedBeforeFailure()
    {
        TestPlatform.RequireLinux();

        var root = Path.Combine(Path.GetTempPath(), "stackpivot-lock-unavailable-" + Guid.NewGuid().ToString("N"));
        var lockPath = Path.Combine(root, "lock-file");
        Directory.CreateDirectory(root);
        File.WriteAllText(lockPath, string.Empty);
        try
        {
            var reporter = new RecordingReporter();
            var coordinator = new AgentTaskCoordinator(AgentId, new ImmediateExecutor(), lockPath);

            await coordinator.HandleAsync(CreateCommand(Path.Combine(root, "workspace_one", "stack_web")), reporter, CancellationToken.None);

            var accepted = Assert.Single(reporter.Accepted);
            var completed = Assert.Single(reporter.Completed);
            Assert.Equal(completed.TaskId, accepted.TaskId);
            Assert.False(completed.Success);
            Assert.Equal("stack_lock_unavailable", completed.ErrorCode);
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [SkippableFact]
    public async Task FailedAcceptanceIsNotCachedForReplay()
    {
        TestPlatform.RequireLinux();

        var root = Path.Combine(Path.GetTempPath(), "stackpivot-lock-acceptance-" + Guid.NewGuid().ToString("N"));
        var lockPath = Path.Combine(root, "lock-file");
        Directory.CreateDirectory(root);
        File.WriteAllText(lockPath, string.Empty);
        var command = CreateCommand(Path.Combine(root, "workspace_one", "stack_web"));
        try
        {
            var coordinator = new AgentTaskCoordinator(AgentId, new ImmediateExecutor(), lockPath);

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                coordinator.HandleAsync(command, new ThrowingAcceptedReporter(), CancellationToken.None));

            var replayReporter = new RecordingReporter();
            await coordinator.HandleAsync(command, replayReporter, CancellationToken.None);

            var accepted = Assert.Single(replayReporter.Accepted);
            var completed = Assert.Single(replayReporter.Completed);
            Assert.Equal(completed.TaskId, accepted.TaskId);
            Assert.False(completed.Success);
            Assert.Equal("stack_lock_unavailable", completed.ErrorCode);
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [SkippableFact]
    public async Task SymlinkedLockDirectoryFailsClosed()
    {
        TestPlatform.RequireLinux();

        var root = Path.Combine(Path.GetTempPath(), "stackpivot-lock-symlink-" + Guid.NewGuid().ToString("N"));
        var outside = Path.Combine(root, "outside");
        var lockDirectory = Path.Combine(root, "locks");
        Directory.CreateDirectory(outside);
        Directory.CreateSymbolicLink(lockDirectory, outside);
        try
        {
            var reporter = new RecordingReporter();
            var coordinator = new AgentTaskCoordinator(AgentId, new ImmediateExecutor(), lockDirectory);

            await coordinator.HandleAsync(CreateCommand(Path.Combine(root, "workspace_one", "stack_web")), reporter, CancellationToken.None);

            var accepted = Assert.Single(reporter.Accepted);
            var completed = Assert.Single(reporter.Completed);
            Assert.Equal(completed.TaskId, accepted.TaskId);
            Assert.False(completed.Success);
            Assert.Equal("stack_lock_unavailable", completed.ErrorCode);
            Assert.Empty(Directory.EnumerateFileSystemEntries(outside));
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    private static DeployStackCommand CreateCommand(string stackPath)
    {
        var command = new DeployStackCommand(
            ProtocolVersion.Current,
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            AgentId,
            "https://git.example/repository.git",
            "git-user",
            "secret"u8.ToArray(),
            "0123456789abcdef0123456789abcdef01234567",
            "workspace_one/stack_web",
            stackPath,
            DateTimeOffset.UtcNow.AddMinutes(5));
        return command with { DispatchFingerprint = DispatchFingerprint.Compute(command) };
    }

    private static void DeleteDirectory(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }

    private sealed class BlockingExecutor : IStackExecutor
    {
        public TaskCompletionSource<bool> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int ExecutionCount => Volatile.Read(ref executionCount);
        private int executionCount;

        public async Task<AgentExecutionResult> ExecuteAsync(
            DeployStackCommand command,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref executionCount);
            Started.TrySetResult(true);
            await Release.Task.WaitAsync(cancellationToken);
            return new AgentExecutionResult(true, 0, string.Empty, false);
        }
    }

    private sealed class ImmediateExecutor : IStackExecutor
    {
        public int ExecutionCount => Volatile.Read(ref executionCount);
        private int executionCount;

        public Task<AgentExecutionResult> ExecuteAsync(
            DeployStackCommand command,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref executionCount);
            return Task.FromResult(new AgentExecutionResult(true, 0, string.Empty, false));
        }
    }

    private sealed class RecordingReporter : IAgentTaskReporter
    {
        public List<TaskAccepted> Accepted { get; } = new();
        public List<TaskCompleted> Completed { get; } = new();

        public Task ReportAcceptedAsync(TaskAccepted accepted, CancellationToken cancellationToken)
        {
            Accepted.Add(accepted);
            return Task.CompletedTask;
        }

        public Task ReportLogAsync(TaskLog log, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task ReportCompletedAsync(TaskCompleted completed, CancellationToken cancellationToken)
        {
            Completed.Add(completed);
            return Task.CompletedTask;
        }
    }

    private sealed class ThrowingCompletedReporter : IAgentTaskReporter
    {
        public Task ReportAcceptedAsync(TaskAccepted accepted, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task ReportLogAsync(TaskLog log, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task ReportCompletedAsync(TaskCompleted completed, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("report failed");
    }

    private sealed class ThrowingAcceptedReporter : IAgentTaskReporter
    {
        public Task ReportAcceptedAsync(TaskAccepted accepted, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("acceptance report failed");

        public Task ReportLogAsync(TaskLog log, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task ReportCompletedAsync(TaskCompleted completed, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("completion report must not be attempted");
    }

#pragma warning disable CA2101
    [DllImport("libc", EntryPoint = "mkfifo", SetLastError = true, CharSet = CharSet.Ansi)]
    private static extern int mkfifo([MarshalAs(UnmanagedType.LPStr)] string path, uint mode);
#pragma warning restore CA2101
}
