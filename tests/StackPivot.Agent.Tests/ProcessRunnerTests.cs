using System.Diagnostics;
using StackPivot.Agent.Execution;
using StackPivot.Agent.Security;
using Xunit;

namespace StackPivot.Agent.Tests;

public sealed class ProcessRunnerTests
{
    private static readonly string[] Arguments = ["%s", "literal;echo injected"];

    [Fact]
    public async Task ArgumentsArePassedWithoutShellExpansion()
    {
        var runner = new ProcessRunner();
        var result = await runner.RunAsync(
            new ProcessRequest(
                "printf",
                Arguments,
                Directory.GetCurrentDirectory()),
            CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("literal;echo injected", result.StandardOutput);
    }

    [Fact]
    public async Task IncrementalReaderReportsBoundedLinesWithTheirStream()
    {
        var lines = new List<ProcessOutputLine>();
        var runner = new ProcessRunner();

        var result = await runner.RunAsync(
            new ProcessRequest(
                "printf",
                ["alpha\\nbeta"],
                Directory.GetCurrentDirectory(),
                OutputHandler: line =>
                {
                    lines.Add(line);
                    return ValueTask.CompletedTask;
                }),
            CancellationToken.None);

        Assert.False(result.OutputTruncated);
        Assert.Equal(["alpha", "beta"], lines.Select(line => line.Text));
        Assert.All(lines, line => Assert.Equal("stdout", line.Stream));
    }

    [SkippableFact]
    public async Task DirectoryHandleKeepsAProcessInTheOpenedDirectoryAfterPathReplacement()
    {
        TestPlatform.RequireLinux();

        var root = Path.Combine(Path.GetTempPath(), "stackpivot-process-fd-" + Guid.NewGuid().ToString("N"));
        var outside = Path.Combine(Path.GetTempPath(), "stackpivot-process-outside-" + Guid.NewGuid().ToString("N"));
        var stackPath = Path.Combine(root, "workspace_one", "stack_web");
        var movedStackPath = Path.Combine(root, "workspace_one", "stack_web-original");
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(outside);

        try
        {
            var policy = new PathPolicy(root);
            await using var safePath = await policy.OpenStackPathAsync(
                "workspace_one/stack_web",
                CancellationToken.None);
            Directory.Move(stackPath, movedStackPath);
            Directory.CreateSymbolicLink(stackPath, outside);

            var result = await new ProcessRunner().RunAsync(
                new ProcessRequest(
                    "pwd",
                    [],
                    safePath.FullPath,
                    WorkingDirectoryHandle: safePath.DirectoryHandle),
                CancellationToken.None);

            Assert.Equal(0, result.ExitCode);
            Assert.Contains("stack_web-original", result.StandardOutput, StringComparison.Ordinal);
            Assert.DoesNotContain("stackpivot-process-outside", result.StandardOutput, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }

            if (Directory.Exists(outside))
            {
                Directory.Delete(outside, recursive: true);
            }
        }
    }

    [Fact]
    public async Task ALongSingleLineIsBoundedAndMarkedTruncated()
    {
        var lines = new List<ProcessOutputLine>();
        var runner = new ProcessRunner();

        var result = await runner.RunAsync(
            new ProcessRequest(
                "printf",
                [new string('x', 20 * 1024)],
                Directory.GetCurrentDirectory(),
                OutputHandler: line =>
                {
                    lines.Add(line);
                    return ValueTask.CompletedTask;
                }),
            CancellationToken.None);

        Assert.True(result.OutputTruncated);
        Assert.Single(lines);
        Assert.True(System.Text.Encoding.UTF8.GetByteCount(lines[0].Text) <= 16 * 1024);
    }

    [SkippableFact]
    public async Task TimeoutReturnsTimedOutResultAfterKillingTheProcess()
    {
        TestPlatform.RequireUnix();
        var runner = new ProcessRunner();

        var result = await runner.RunAsync(
            new ProcessRequest(
                "sleep",
                ["30"],
                Directory.GetCurrentDirectory(),
                Timeout: TimeSpan.FromMilliseconds(100)),
            CancellationToken.None);

        Assert.True(result.TimedOut);
        Assert.Equal(-1, result.ExitCode);
        Assert.Empty(result.StandardOutput);
        Assert.Empty(result.StandardError);
    }

    [SkippableFact]
    public async Task CallerCancellationKillsTheProcessAndPropagatesCancellation()
    {
        TestPlatform.RequireUnix();
        var runner = new ProcessRunner();
        using var cancellation = new CancellationTokenSource();
        var execution = runner.RunAsync(
            new ProcessRequest("sleep", ["30"], Directory.GetCurrentDirectory()),
            cancellation.Token);

        await Task.Delay(TimeSpan.FromMilliseconds(100));
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => execution);
    }

    [SkippableFact]
    public async Task TimeoutKillsTheEntireProcessTree()
    {
        TestPlatform.RequireUnix();
        var harnessPath = Path.Combine(AppContext.BaseDirectory, "StackPivot.ProcessHarness.dll");
        Assert.True(File.Exists(harnessPath), $"Process harness was not copied to {AppContext.BaseDirectory}.");
        var childPidPath = Path.Combine(Path.GetTempPath(), "stackpivot-child-pid-" + Guid.NewGuid().ToString("N"));
        var runner = new ProcessRunner();
        var execution = runner.RunAsync(
            new ProcessRequest(
                "dotnet",
                [harnessPath, "spawn-child", childPidPath],
                Directory.GetCurrentDirectory(),
                Timeout: TimeSpan.FromSeconds(2)),
            CancellationToken.None);

        try
        {
            var childPid = await WaitForPidAsync(childPidPath);
            var result = await execution;

            Assert.True(result.TimedOut);
            Assert.Equal(-1, result.ExitCode);
            Assert.True(await WaitForProcessExitAsync(childPid), $"Child process {childPid} survived the process-tree kill.");
        }
        finally
        {
            if (File.Exists(childPidPath))
            {
                File.Delete(childPidPath);
            }
        }
    }

    private static async Task<int> WaitForPidAsync(string path)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            if (File.Exists(path)
                && int.TryParse(await File.ReadAllTextAsync(path), out var pid))
            {
                return pid;
            }

            await Task.Delay(20);
        }

        throw new Xunit.Sdk.XunitException("The process harness did not publish its child PID.");
    }

    private static async Task<bool> WaitForProcessExitAsync(int pid)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                using var process = Process.GetProcessById(pid);
                if (process.HasExited)
                {
                    return true;
                }
            }
            catch (ArgumentException)
            {
                return true;
            }

            await Task.Delay(20);
        }

        return false;
    }
}
