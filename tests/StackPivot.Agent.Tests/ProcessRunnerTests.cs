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
}
