using System.Diagnostics;
using System.Text;
using StackPivot.Control.Infrastructure.Git;
using Xunit;

namespace StackPivot.Control.Tests;

public sealed class GitCommandRunnerTests
{
    [SkippableFact]
    public async Task ALongSingleLineIsMarkedAsTruncated()
    {
        if (OperatingSystem.IsWindows())
        {
            throw new Xunit.SkipException("Unix-only test: requires the repository Git process environment.");
        }

        var root = Path.Combine(Path.GetTempPath(), "stackpivot-git-runner-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            RunGit(root, "init", "--quiet");
            RunGit(root, "config", "--local", "stackpivot.long-value", new string('x', 20 * 1024));

            var runner = new GitCommandRunner(new CentralGitOptions
            {
                CommandTimeout = TimeSpan.FromSeconds(5)
            });
            var result = await runner.RunAsync(
                root,
                ["config", "--local", "--get", "stackpivot.long-value"],
                CancellationToken.None);

            Assert.Equal(0, result.ExitCode);
            Assert.True(result.OutputTruncated);
            Assert.True(Encoding.UTF8.GetByteCount(result.StandardOutput) <= 16 * 1024);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [SkippableFact]
    public async Task CommandTimeoutReturnsTimedOutResult()
    {
        RequireUnix();
        var root = CreateRepository();
        try
        {
            ConfigureSleepAlias(root);
            var runner = new GitCommandRunner(new CentralGitOptions
            {
                CommandTimeout = TimeSpan.FromMilliseconds(200)
            });

            var result = await runner.RunAsync(root, ["stackpivot-wait"], CancellationToken.None);

            Assert.True(result.TimedOut);
            Assert.Equal(-1, result.ExitCode);
            Assert.Empty(result.StandardOutput);
            Assert.Empty(result.StandardError);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [SkippableFact]
    public async Task CallerCancellationPropagatesAfterKillingGitProcess()
    {
        RequireUnix();
        var root = CreateRepository();
        try
        {
            ConfigureSleepAlias(root);
            var runner = new GitCommandRunner(new CentralGitOptions
            {
                CommandTimeout = TimeSpan.FromSeconds(30)
            });
            using var cancellation = new CancellationTokenSource();
            var execution = runner.RunAsync(root, ["stackpivot-wait"], cancellation.Token);

            await Task.Delay(TimeSpan.FromMilliseconds(100));
            cancellation.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => execution);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static void RunGit(string workingDirectory, params string[] arguments)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "git",
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };
        foreach (var argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        process.Start();
        process.WaitForExit();
        Assert.Equal(0, process.ExitCode);
    }

    private static string CreateRepository()
    {
        var root = Path.Combine(Path.GetTempPath(), "stackpivot-git-runner-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        RunGit(root, "init", "--quiet");
        return root;
    }

    private static void ConfigureSleepAlias(string root)
    {
        RunGit(root, "config", "--local", "alias.stackpivot-wait", "!sleep 30");
    }

    private static void RequireUnix()
    {
        if (OperatingSystem.IsWindows())
        {
            throw new Xunit.SkipException("Unix-only test: requires the repository Git process environment.");
        }
    }
}
