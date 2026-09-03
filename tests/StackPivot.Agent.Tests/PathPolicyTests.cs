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

    [SkippableFact]
    public async Task ExistingDirectoryInManagedFilePathIsRejectedAsNonRegular()
    {
        TestPlatform.RequireLinux();

        var root = Path.Combine(Path.GetTempPath(), "stackpivot-path-directory-" + Guid.NewGuid().ToString("N"));
        var stackPath = Path.Combine(root, "workspace_one", "stack_web");
        Directory.CreateDirectory(Path.Combine(stackPath, "directory"));
        var policy = new PathPolicy(root);

        try
        {
            await Assert.ThrowsAsync<PathPolicyException>(() =>
                policy.ValidateManagedFilePathAsync("workspace_one/stack_web/directory", CancellationToken.None));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [SkippableFact]
    public async Task OpenFileRejectsAnExistingDirectoryAsNonRegular()
    {
        TestPlatform.RequireLinux();

        var root = Path.Combine(Path.GetTempPath(), "stackpivot-path-open-directory-" + Guid.NewGuid().ToString("N"));
        var stackPath = Path.Combine(root, "workspace_one", "stack_web");
        Directory.CreateDirectory(Path.Combine(stackPath, "directory"));
        var policy = new PathPolicy(root);

        try
        {
            await using var safePath = await policy.OpenStackPathAsync("workspace_one/stack_web", CancellationToken.None);
            Assert.Throws<PathPolicyException>(() =>
                safePath.DirectoryHandle!.OpenFile("directory", FileMode.Open, FileAccess.Read));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [SkippableFact]
    public async Task OpenStackPathKeepsFileOperationsInsideTheOpenedDirectoryAfterReplacement()
    {
        TestPlatform.RequireLinux();

        var root = Path.Combine(Path.GetTempPath(), "stackpivot-path-fd-" + Guid.NewGuid().ToString("N"));
        var outside = Path.Combine(Path.GetTempPath(), "stackpivot-path-outside-" + Guid.NewGuid().ToString("N"));
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

            using (var file = safePath.DirectoryHandle!.OpenFile("marker.txt", FileMode.CreateNew, FileAccess.Write))
            using (var writer = new StreamWriter(file))
            {
                writer.Write("inside");
            }

            Assert.True(File.Exists(Path.Combine(movedStackPath, "marker.txt")));
            Assert.False(File.Exists(Path.Combine(outside, "marker.txt")));
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

    [SkippableFact]
    public async Task OpenStackPathFailsClosedOnUnsupportedOperatingSystem()
    {
        if (OperatingSystem.IsLinux())
        {
            throw new Xunit.SkipException("Linux-only Agent implementation test.");
        }

        var root = Path.Combine(Path.GetTempPath(), "stackpivot-path-platform-" + Guid.NewGuid().ToString("N"));
        try
        {
            var policy = new PathPolicy(root);

            await Assert.ThrowsAsync<PlatformNotSupportedException>(() =>
                policy.OpenStackPathAsync("workspace_one/stack_web", CancellationToken.None));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }
}
