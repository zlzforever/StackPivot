using StackPivot.Agent.Execution;
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
}
