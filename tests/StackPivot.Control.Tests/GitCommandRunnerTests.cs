using System.Diagnostics;
using System.Text;
using StackPivot.Control.Infrastructure.Git;
using Xunit;

namespace StackPivot.Control.Tests;

public sealed class GitCommandRunnerTests
{
    [Fact]
    public async Task ALongSingleLineIsMarkedAsTruncated()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
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
}
