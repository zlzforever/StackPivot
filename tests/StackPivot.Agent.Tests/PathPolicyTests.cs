using StackPivot.Agent.Security;
using Xunit;

namespace StackPivot.Agent.Tests;

public sealed class PathPolicyTests
{
    [Theory]
    [InlineData("workspace_one/stack_web")]
    [InlineData("workspace_01/stack_ignored")]
    public async Task StackPathStaysUnderConfiguredRoot(string relativePath)
    {
        var policy = new PathPolicy("/opt/agent-main");

        var result = await policy.ValidateStackPathAsync(relativePath, CancellationToken.None);

        Assert.StartsWith("/opt/agent-main/", result.FullPath, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("../outside")]
    [InlineData("workspace/../outside")]
    [InlineData("/etc/passwd")]
    [InlineData("workspace\\..\\outside")]
    public async Task StackPathEscapeIsRejected(string relativePath)
    {
        var policy = new PathPolicy("/opt/agent-main");

        await Assert.ThrowsAsync<PathPolicyException>(() =>
            policy.ValidateStackPathAsync(relativePath, CancellationToken.None));
    }
}
