using System.Text.Json;
using StackPivot.Agent.Execution;
using StackPivot.Agent.Security;
using StackPivot.Contracts.Deployments;
using StackPivot.Contracts.SignalR;
using Xunit;

namespace StackPivot.Agent.Tests;

public sealed class GitTreePolicyTests
{
    private static readonly IReadOnlySet<string> AllowedRemoteHosts =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "git.example" };

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

    [Theory]
    [InlineData("https://@git.example/repository.git")]
    [InlineData("https://git.example/repository.git?token=secret")]
    [InlineData("https://git.example/repository.git#fragment")]
    public void RemoteHostPolicyRejectsExplicitAuthorityAndUrlSuffixes(string remote)
    {
        Assert.False(CentralRemotePolicy.IsAllowed(
            remote,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "git.example" }));
    }

    [SkippableFact]
    public async Task MaterializationUsesReadTreeAndClearsTheCredentialBuffer()
    {
        TestPlatform.RequireLinux();

        var root = Path.Combine(Path.GetTempPath(), "stackpivot-git-" + Guid.NewGuid().ToString("N"));
        var runner = new MaterializationRunner();
        var executor = new GitCheckoutExecutor(runner, new PathPolicy(root), TimeSpan.FromSeconds(5), AllowedRemoteHosts);
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
            Assert.Contains(runner.Requests, request => request.Arguments.Contains("read-tree")
                && request.Arguments.Contains("--reset")
                && request.Arguments.Contains("0123456789abcdef0123456789abcdef01234567:workspace_one/stack_web")
                && request.Arguments.Any(argument => argument.StartsWith("--work-tree=", StringComparison.Ordinal)
                    && argument.StartsWith("--work-tree=/proc/self/fd/", StringComparison.Ordinal)));
            Assert.DoesNotContain(runner.Requests, request => request.Arguments.Contains("checkout"));
            var repositoryRequests = runner.Requests
                .Where(request => !request.Arguments.Contains("init"))
                .ToArray();
            Assert.NotEmpty(repositoryRequests);
            Assert.All(repositoryRequests, request =>
            {
                Assert.NotNull(request.WorkingDirectoryHandle);
                Assert.Contains("--git-dir=.", request.Arguments);
                Assert.DoesNotContain("--work-tree=..", request.Arguments);
                Assert.Contains(request.Arguments, argument => argument.StartsWith("--work-tree=/proc/self/fd/", StringComparison.Ordinal));
            });
            var fetch = Assert.Single(runner.Requests, request => request.Arguments.Contains("fetch"));
            Assert.Contains("https://git.example/repository.git", fetch.Arguments);
            Assert.DoesNotContain("origin", fetch.Arguments);
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

        var mode = File.GetUnixFileMode(credential.AskpassPath);
        const UnixFileMode permissionBits = UnixFileMode.UserRead
            | UnixFileMode.UserWrite
            | UnixFileMode.UserExecute
            | UnixFileMode.GroupRead
            | UnixFileMode.GroupWrite
            | UnixFileMode.GroupExecute
            | UnixFileMode.OtherRead
            | UnixFileMode.OtherWrite
            | UnixFileMode.OtherExecute;
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, mode & permissionBits);
    }

    [SkippableFact]
    public async Task TruncatedTreeOutputFailsClosedBeforeReadTree()
    {
        TestPlatform.RequireLinux();

        var root = Path.Combine(Path.GetTempPath(), "stackpivot-git-" + Guid.NewGuid().ToString("N"));
        var runner = new MaterializationRunner(treeOutputTruncated: true);
        var executor = new GitCheckoutExecutor(runner, new PathPolicy(root), TimeSpan.FromSeconds(5), AllowedRemoteHosts);
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
        var executor = new GitCheckoutExecutor(runner, new PathPolicy(root), TimeSpan.FromSeconds(5), AllowedRemoteHosts);
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

    [SkippableFact]
    public async Task GitInitTimeoutReturnsStableTimeoutErrorCode()
    {
        TestPlatform.RequireLinux();

        var root = Path.Combine(Path.GetTempPath(), "stackpivot-git-" + Guid.NewGuid().ToString("N"));
        var runner = new MaterializationRunner(timeoutCommand: "init");
        var executor = new GitCheckoutExecutor(runner, new PathPolicy(root), TimeSpan.FromSeconds(5), AllowedRemoteHosts);
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

    [SkippableFact]
    public async Task GitRemoteAddTimeoutReturnsStableTimeoutErrorCode()
    {
        TestPlatform.RequireLinux();

        var root = Path.Combine(Path.GetTempPath(), "stackpivot-git-" + Guid.NewGuid().ToString("N"));
        var runner = new MaterializationRunner(timeoutCommand: "remote_add");
        var executor = new GitCheckoutExecutor(runner, new PathPolicy(root), TimeSpan.FromSeconds(5), AllowedRemoteHosts);
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
        var executor = new GitCheckoutExecutor(runner, new PathPolicy(root), TimeSpan.FromSeconds(5), AllowedRemoteHosts);
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
        var executor = new GitCheckoutExecutor(runner, new PathPolicy(root), TimeSpan.FromSeconds(5), AllowedRemoteHosts);

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
    public async Task OversizedCheckoutMetadataFailsClosedBeforeReadTree()
    {
        TestPlatform.RequireLinux();

        var root = Path.Combine(Path.GetTempPath(), "stackpivot-git-metadata-size-" + Guid.NewGuid().ToString("N"));
        var stackPath = Path.Combine(root, "workspace_one", "stack_web");
        var gitPath = Path.Combine(stackPath, ".git");
        Directory.CreateDirectory(gitPath);
        File.WriteAllText(
            Path.Combine(gitPath, "stackpivot-checkout.json"),
            "{\"commit\":\"old\",\"path\":\"workspace_one/stack_web\",\"files\":[\""
                + new string('x', 2 * 1024 * 1024)
                + "\"]}");
        var runner = new MaterializationRunner();
        var executor = new GitCheckoutExecutor(runner, new PathPolicy(root), TimeSpan.FromSeconds(5), AllowedRemoteHosts);

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
    public async Task MaterializationWritesThroughTheOpenedDirectoryAfterThePathIsReplaced()
    {
        TestPlatform.RequireLinux();

        var root = Path.Combine(Path.GetTempPath(), "stackpivot-git-race-" + Guid.NewGuid().ToString("N"));
        var stackPath = Path.Combine(root, "workspace_one", "stack_web");
        var originalPath = Path.Combine(root, "workspace_one", "stack_web-original");
        var outside = Path.Combine(Path.GetTempPath(), "stackpivot-git-race-outside-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stackPath);
        Directory.CreateDirectory(outside);
        var runner = new MaterializationRunner
        {
            BeforeReadTree = _ =>
            {
                Directory.Move(stackPath, originalPath);
                Directory.CreateSymbolicLink(stackPath, outside);
            }
        };
        var executor = new GitCheckoutExecutor(runner, new PathPolicy(root), TimeSpan.FromSeconds(5), AllowedRemoteHosts);

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
            Assert.True(File.Exists(Path.Combine(originalPath, "compose.yaml")));
            Assert.False(File.Exists(Path.Combine(outside, "compose.yaml")));
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
        var executor = new GitCheckoutExecutor(runner, new PathPolicy(root), TimeSpan.FromSeconds(5), AllowedRemoteHosts);

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
        var executor = new GitCheckoutExecutor(runner, new PathPolicy(root), TimeSpan.FromSeconds(5), AllowedRemoteHosts);

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

    [SkippableFact]
    public async Task MaterializationFailsClosedOnUnsupportedOperatingSystem()
    {
        if (OperatingSystem.IsLinux())
        {
            throw new Xunit.SkipException("Linux-only Agent implementation test.");
        }

        var root = Path.Combine(Path.GetTempPath(), "stackpivot-git-platform-" + Guid.NewGuid().ToString("N"));
        var token = "secret"u8.ToArray();
        try
        {
            var executor = new GitCheckoutExecutor(
                new MaterializationRunner(),
                new PathPolicy(root),
                TimeSpan.FromSeconds(5),
                AllowedRemoteHosts);

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
            Assert.Equal("platform_unsupported", result.ErrorCode);
            Assert.Empty(Directory.Exists(root) ? Directory.EnumerateFileSystemEntries(root) : []);
        }
        finally
        {
            Assert.All(token, value => Assert.Equal(0, value));
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private sealed class MaterializationRunner(
        bool treeOutputTruncated = false,
        string? timeoutCommand = null) : IProcessRunner
    {
        public List<ProcessRequest> Requests { get; } = new();
        public Action<ProcessRequest>? BeforeReadTree { get; init; }
        private string? TimeoutCommand { get; } = timeoutCommand;
        public ProcessResult TreeResult { get; init; } = new(
            0,
            "100644 blob 0123456789012345678901234567890123456789\tworkspace_one/stack_web/compose.yaml\n",
            string.Empty);

        public Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            var command = request.Arguments.First(argument =>
                argument is "init" or "remote" or "fetch" or "cat-file" or "ls-tree" or "read-tree");
            switch (command)
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

                    BeforeReadTree?.Invoke(request);
                    var workTree = request.Arguments
                        .Single(argument => argument.StartsWith("--work-tree=", StringComparison.Ordinal))["--work-tree=".Length..];
                    Directory.CreateDirectory(workTree);
                    File.WriteAllText(Path.Combine(workTree, "compose.yaml"), "services: {}");
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
