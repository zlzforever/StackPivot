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

    [SkippableFact]
    public async Task NestedSymlinkInManagedFilePathIsRejected()
    {
        TestPlatform.RequireLinux();

        var root = Path.Combine(Path.GetTempPath(), "stackpivot-path-" + Guid.NewGuid().ToString("N"));
        var stackPath = Path.Combine(root, "workspace_one", "stack_web");
        Directory.CreateDirectory(stackPath);
        Directory.CreateDirectory(Path.Combine(root, "outside"));
        Directory.CreateSymbolicLink(Path.Combine(stackPath, "config"), Path.Combine(root, "outside"));
        var policy = new PathPolicy(root);

        try
        {
            await Assert.ThrowsAsync<PathPolicyException>(() =>
                policy.ValidateManagedFilePathAsync("workspace_one/stack_web/config/secret.txt", CancellationToken.None));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [SkippableFact]
    public async Task DanglingSymlinkInManagedFilePathIsRejected()
    {
        TestPlatform.RequireLinux();

        var root = Path.Combine(Path.GetTempPath(), "stackpivot-path-" + Guid.NewGuid().ToString("N"));
        var stackPath = Path.Combine(root, "workspace_one", "stack_web");
        Directory.CreateDirectory(stackPath);
        File.CreateSymbolicLink(Path.Combine(stackPath, "config"), Path.Combine(root, "missing"));
        var policy = new PathPolicy(root);

        try
        {
            await Assert.ThrowsAsync<PathPolicyException>(() =>
                policy.ValidateManagedFilePathAsync("workspace_one/stack_web/config/secret.txt", CancellationToken.None));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
