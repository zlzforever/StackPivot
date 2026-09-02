using System.Text.Json;
using StackPivot.Agent.Execution;
using StackPivot.Agent.Security;
using StackPivot.Contracts.Deployments;
using StackPivot.Contracts.SignalR;
using Xunit;

namespace StackPivot.Agent.Tests;

public sealed class GitTreePolicyTests
{
    [Fact]
    public void SensitiveEnvInAnySubdirectoryIsRejected()
    {
        const string tree = "100644 blob 0123456789012345678901234567890123456789\tworkspace_one/stack_web/compose.yaml\n"
            + "100644 blob 0123456789012345678901234567890123456789\tworkspace_one/stack_web/config/.env\n";

        var exception = Assert.Throws<GitTreePolicyException>(() => GitTreePolicy.Validate(tree, "workspace_one/stack_web"));

        Assert.Equal("policy_violation", exception.Code);
    }

    [Fact]
    public void SymlinkInFetchedTreeIsRejected()
    {
        const string tree = "100644 blob 0123456789012345678901234567890123456789\tworkspace_one/stack_web/compose.yaml\n"
            + "120000 blob 0123456789012345678901234567890123456789\tworkspace_one/stack_web/config/data\n";

        var exception = Assert.Throws<GitTreePolicyException>(() => GitTreePolicy.Validate(tree, "workspace_one/stack_web"));

        Assert.Equal("policy_violation", exception.Code);
    }

    [Fact]
    public void ValidTreeRequiresComposeAtStackRoot()
    {
        const string tree = "100644 blob 0123456789012345678901234567890123456789\tworkspace_one/stack_web/app.txt\n";

        var exception = Assert.Throws<GitTreePolicyException>(() => GitTreePolicy.Validate(tree, "workspace_one/stack_web"));

        Assert.Equal("invalid_path", exception.Code);
    }

    [Fact]
    public void RemoteHostPolicyRejectsAHostOutsideTheConfiguredAllowList()
    {
        Assert.False(CentralRemotePolicy.IsAllowed(
            "https://git.example/repository.git",
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "other.example" }));
    }

    [Fact]
    public void RemoteHostPolicyRejectsWhitespaceInTheRemote()
    {
        Assert.False(CentralRemotePolicy.IsAllowed(
            "https://git.example/repository with-space.git",
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "git.example" }));
    }

    [SkippableFact]
    public async Task MaterializationUsesReadTreeAndClearsTheCredentialBuffer()
    {
        TestPlatform.RequireLinux();

        var root = Path.Combine(Path.GetTempPath(), "stackpivot-git-" + Guid.NewGuid().ToString("N"));
        var runner = new MaterializationRunner();
        var executor = new GitCheckoutExecutor(runner, new PathPolicy(root), TimeSpan.FromSeconds(5));
        var token = "secret"u8.ToArray();

        try
        {
            var result = await executor.MaterializeAsync(
                new GitDeploymentInput(
                    "https://git.example/repository.git",
                    "git-user",
                    token,
                    "0123456789abcdef0123456789abcdef01234567",
                    "workspace_one/stack_web",
                    Path.Combine(root, "workspace_one", "stack_web")),
                CancellationToken.None);

            Assert.True(result.Success, result.ErrorCode);
            Assert.Contains(runner.Requests, request => request.Arguments.SequenceEqual(new[]
            {
                "read-tree", "--reset", "-u", "0123456789abcdef0123456789abcdef01234567:workspace_one/stack_web"
            }));
            Assert.DoesNotContain(runner.Requests, request => request.Arguments.Contains("checkout"));
            Assert.True(File.Exists(Path.Combine(root, "workspace_one", "stack_web", "compose.yaml")));
            Assert.True(File.Exists(Path.Combine(root, "workspace_one", "stack_web", ".git", "stackpivot-checkout.json")));
            Assert.All(token, value => Assert.Equal(0, value));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [SkippableFact]
    public void InMemoryAskpassIsExecutableByGit()
    {
        if (!OperatingSystem.IsLinux())
        {
            throw new Xunit.SkipException("Linux-only test: requires memfd and Linux filesystem semantics.");
        }

        using var credential = InMemoryGitCredential.Create("git-user", "secret"u8.ToArray());

        Assert.True(File.GetUnixFileMode(credential.AskpassPath).HasFlag(UnixFileMode.UserExecute));
    }

    [SkippableFact]
    public async Task TruncatedTreeOutputFailsClosedBeforeReadTree()
    {
        TestPlatform.RequireLinux();

        var root = Path.Combine(Path.GetTempPath(), "stackpivot-git-" + Guid.NewGuid().ToString("N"));
        var runner = new MaterializationRunner(treeOutputTruncated: true);
        var executor = new GitCheckoutExecutor(runner, new PathPolicy(root), TimeSpan.FromSeconds(5));
        var token = "secret"u8.ToArray();

        try
        {
            var result = await executor.MaterializeAsync(
                new GitDeploymentInput(
                    "https://git.example/repository.git",
                    "git-user",
                    token,
                    "0123456789abcdef0123456789abcdef01234567",
                    "workspace_one/stack_web",
                    Path.Combine(root, "workspace_one", "stack_web")),
                CancellationToken.None);

            Assert.False(result.Success);
            Assert.Equal("git_tree_output_truncated", result.ErrorCode);
            Assert.DoesNotContain(runner.Requests, request => request.Arguments.Contains("read-tree"));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [SkippableFact]
    public async Task FailedTreeCommandFailsClosedBeforeReadTree()
    {
        TestPlatform.RequireLinux();

        var root = Path.Combine(Path.GetTempPath(), "stackpivot-git-" + Guid.NewGuid().ToString("N"));
        var runner = new MaterializationRunner
        {
            TreeResult = new ProcessResult(
                1,
                "100644 blob 0123456789012345678901234567890123456789\tworkspace_one/stack_web/compose.yaml\n",
                "tree failed")
        };
        var executor = new GitCheckoutExecutor(runner, new PathPolicy(root), TimeSpan.FromSeconds(5));
        var token = "secret"u8.ToArray();

        try
        {
            var result = await executor.MaterializeAsync(
                new GitDeploymentInput(
                    "https://git.example/repository.git",
                    "git-user",
                    token,
                    "0123456789abcdef0123456789abcdef01234567",
                    "workspace_one/stack_web",
                    Path.Combine(root, "workspace_one", "stack_web")),
                CancellationToken.None);

            Assert.False(result.Success);
            Assert.Equal("git_tree_failed", result.ErrorCode);
            Assert.DoesNotContain(runner.Requests, request => request.Arguments.Contains("read-tree"));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task GitInitTimeoutReturnsStableTimeoutErrorCode()
    {
        var root = Path.Combine(Path.GetTempPath(), "stackpivot-git-" + Guid.NewGuid().ToString("N"));
        var runner = new MaterializationRunner(timeoutCommand: "init");
        var executor = new GitCheckoutExecutor(runner, new PathPolicy(root), TimeSpan.FromSeconds(5));
        var token = "secret"u8.ToArray();

        try
        {
            var result = await executor.MaterializeAsync(
                new GitDeploymentInput(
                    "https://git.example/repository.git",
                    "git-user",
                    token,
                    "0123456789abcdef0123456789abcdef01234567",
                    "workspace_one/stack_web",
                    Path.Combine(root, "workspace_one", "stack_web")),
                CancellationToken.None);

            Assert.False(result.Success);
            Assert.Equal("git_init_timeout", result.ErrorCode);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task GitRemoteAddTimeoutReturnsStableTimeoutErrorCode()
    {
        var root = Path.Combine(Path.GetTempPath(), "stackpivot-git-" + Guid.NewGuid().ToString("N"));
        var runner = new MaterializationRunner(timeoutCommand: "remote_add");
        var executor = new GitCheckoutExecutor(runner, new PathPolicy(root), TimeSpan.FromSeconds(5));
        var token = "secret"u8.ToArray();

        try
        {
            var result = await executor.MaterializeAsync(
                new GitDeploymentInput(
                    "https://git.example/repository.git",
                    "git-user",
                    token,
                    "0123456789abcdef0123456789abcdef01234567",
                    "workspace_one/stack_web",
                    Path.Combine(root, "workspace_one", "stack_web")),
                CancellationToken.None);

            Assert.False(result.Success);
            Assert.Equal("git_remote_timeout", result.ErrorCode);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [SkippableTheory]
    [InlineData("remote_get", "git_remote_timeout")]
    [InlineData("fetch", "git_fetch_timeout")]
    [InlineData("verify", "git_verify_timeout")]
    [InlineData("tree", "git_tree_timeout")]
    [InlineData("materialize", "git_materialize_timeout")]
    public async Task GitTimeoutAtEachLinuxOnlyStageReturnsStableTimeoutErrorCode(
        string timeoutCommand,
        string expectedErrorCode)
    {
        TestPlatform.RequireLinux();

        var root = Path.Combine(Path.GetTempPath(), "stackpivot-git-" + Guid.NewGuid().ToString("N"));
        var runner = new MaterializationRunner(timeoutCommand: timeoutCommand);
        var executor = new GitCheckoutExecutor(runner, new PathPolicy(root), TimeSpan.FromSeconds(5));
        var token = "secret"u8.ToArray();

        try
        {
            var result = await executor.MaterializeAsync(
                new GitDeploymentInput(
                    "https://git.example/repository.git",
                    "git-user",
                    token,
                    "0123456789abcdef0123456789abcdef01234567",
                    "workspace_one/stack_web",
                    Path.Combine(root, "workspace_one", "stack_web")),
                CancellationToken.None);

            Assert.False(result.Success);
            Assert.Equal(expectedErrorCode, result.ErrorCode);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [SkippableFact]
    public async Task PreviouslyManagedFilesAreRemovedFromTheStackRoot()
    {
        TestPlatform.RequireLinux();

        var root = Path.Combine(Path.GetTempPath(), "stackpivot-git-" + Guid.NewGuid().ToString("N"));
        var stackPath = Path.Combine(root, "workspace_one", "stack_web");
        var gitPath = Path.Combine(stackPath, ".git");
        Directory.CreateDirectory(gitPath);
        File.WriteAllText(Path.Combine(stackPath, "stale.txt"), "old");
        File.WriteAllText(
            Path.Combine(gitPath, "stackpivot-checkout.json"),
            JsonSerializer.Serialize(new { commit = "old", path = "workspace_one/stack_web", files = new[] { "stale.txt" } }));

        var runner = new MaterializationRunner();
        var executor = new GitCheckoutExecutor(runner, new PathPolicy(root), TimeSpan.FromSeconds(5));

        try
        {
            var result = await executor.MaterializeAsync(
                new GitDeploymentInput(
                    "https://git.example/repository.git",
                    "git-user",
                    "secret"u8.ToArray(),
                    "0123456789abcdef0123456789abcdef01234567",
                    "workspace_one/stack_web",
                    stackPath),
                CancellationToken.None);

            Assert.True(result.Success, result.ErrorCode);
            Assert.False(File.Exists(Path.Combine(stackPath, "stale.txt")));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [SkippableFact]
    public async Task NestedSymlinkInIncomingTreeIsRejectedBeforeReadTree()
    {
        TestPlatform.RequireLinux();

        var root = Path.Combine(Path.GetTempPath(), "stackpivot-git-" + Guid.NewGuid().ToString("N"));
        var stackPath = Path.Combine(root, "workspace_one", "stack_web");
        var outside = Path.Combine(root, "outside");
        Directory.CreateDirectory(stackPath);
        Directory.CreateDirectory(outside);
        Directory.CreateSymbolicLink(Path.Combine(stackPath, "config"), outside);

        var runner = new MaterializationRunner
        {
            TreeResult = new ProcessResult(
                0,
                "100644 blob 0123456789012345678901234567890123456789\tworkspace_one/stack_web/compose.yaml\n"
                + "100644 blob 0123456789012345678901234567890123456789\tworkspace_one/stack_web/config/data.txt\n",
                string.Empty)
        };
        var executor = new GitCheckoutExecutor(runner, new PathPolicy(root), TimeSpan.FromSeconds(5));

        try
        {
            var result = await executor.MaterializeAsync(
                new GitDeploymentInput(
                    "https://git.example/repository.git",
                    "git-user",
                    "secret"u8.ToArray(),
                    "0123456789abcdef0123456789abcdef01234567",
                    "workspace_one/stack_web",
                    stackPath),
                CancellationToken.None);

            Assert.False(result.Success);
            Assert.Equal("invalid_path", result.ErrorCode);
            Assert.DoesNotContain(runner.Requests, request => request.Arguments.Contains("read-tree"));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [SkippableFact]
    public async Task DanglingCheckoutMetadataSymlinkIsRejectedBeforeReadTree()
    {
        TestPlatform.RequireLinux();

        var root = Path.Combine(Path.GetTempPath(), "stackpivot-git-" + Guid.NewGuid().ToString("N"));
        var stackPath = Path.Combine(root, "workspace_one", "stack_web");
        var gitPath = Path.Combine(stackPath, ".git");
        Directory.CreateDirectory(gitPath);
        File.CreateSymbolicLink(
            Path.Combine(gitPath, "stackpivot-checkout.json"),
            Path.Combine(root, "missing-metadata"));
        var runner = new MaterializationRunner();
        var executor = new GitCheckoutExecutor(runner, new PathPolicy(root), TimeSpan.FromSeconds(5));

        try
        {
            var result = await executor.MaterializeAsync(
                new GitDeploymentInput(
                    "https://git.example/repository.git",
                    "git-user",
                    "secret"u8.ToArray(),
                    "0123456789abcdef0123456789abcdef01234567",
                    "workspace_one/stack_web",
                    stackPath),
                CancellationToken.None);

            Assert.False(result.Success);
            Assert.Equal("invalid_path", result.ErrorCode);
            Assert.DoesNotContain(runner.Requests, request => request.Arguments.Contains("read-tree"));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task ManagedMetadataPathCannotTargetGitDirectory()
    {
        var policy = new PathPolicy(Path.Combine(Path.GetTempPath(), "stackpivot-git-" + Guid.NewGuid().ToString("N")));

        await Assert.ThrowsAsync<PathPolicyException>(() =>
            policy.ValidateManagedFilePathAsync(".git/config", CancellationToken.None));
    }

    private sealed class MaterializationRunner(
        bool treeOutputTruncated = false,
        string? timeoutCommand = null) : IProcessRunner
    {
        public List<ProcessRequest> Requests { get; } = new();
        private string? TimeoutCommand { get; } = timeoutCommand;
        public ProcessResult TreeResult { get; init; } = new(
            0,
            "100644 blob 0123456789012345678901234567890123456789\tworkspace_one/stack_web/compose.yaml\n",
            string.Empty);

        public Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            switch (request.Arguments[0])
            {
                case "init":
                    Directory.CreateDirectory(Path.Combine(request.WorkingDirectory, ".git"));
                    return Task.FromResult(TimeoutOr("init", new ProcessResult(0, string.Empty, string.Empty)));
                case "remote" when request.Arguments.Contains("get-url"):
                    return Task.FromResult(TimeoutOr("remote_get", new ProcessResult(1, string.Empty, string.Empty)));
                case "remote":
                    return Task.FromResult(TimeoutOr("remote_add", new ProcessResult(0, string.Empty, string.Empty)));
                case "fetch":
                    return Task.FromResult(TimeoutOr("fetch", new ProcessResult(0, string.Empty, string.Empty)));
                case "cat-file":
                    return Task.FromResult(TimeoutOr("verify", new ProcessResult(0, string.Empty, string.Empty)));
                case "ls-tree":
                    if (TimeoutCommand == "tree")
                    {
                        return Task.FromResult(new ProcessResult(-1, string.Empty, string.Empty, TimedOut: true));
                    }

                    return Task.FromResult(TreeResult with { OutputTruncated = treeOutputTruncated || TreeResult.OutputTruncated });
                case "read-tree":
                    if (TimeoutCommand == "materialize")
                    {
                        return Task.FromResult(new ProcessResult(-1, string.Empty, string.Empty, TimedOut: true));
                    }

                    File.WriteAllText(Path.Combine(request.WorkingDirectory, "compose.yaml"), "services: {}");
                    return Task.FromResult(new ProcessResult(0, string.Empty, string.Empty));
                default:
                    return Task.FromResult(new ProcessResult(1, string.Empty, string.Empty));
            }
        }

        private ProcessResult TimeoutOr(string command, ProcessResult result) =>
            TimeoutCommand == command
                ? new ProcessResult(-1, string.Empty, string.Empty, TimedOut: true)
                : result;
    }
}
