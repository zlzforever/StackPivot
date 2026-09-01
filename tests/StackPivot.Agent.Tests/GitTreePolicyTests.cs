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
    public async Task MaterializationUsesReadTreeAndClearsTheCredentialBuffer()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

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

    private sealed class MaterializationRunner : IProcessRunner
    {
        public List<ProcessRequest> Requests { get; } = new();

        public Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            switch (request.Arguments[0])
            {
                case "init":
                    Directory.CreateDirectory(Path.Combine(request.WorkingDirectory, ".git"));
                    return Task.FromResult(new ProcessResult(0, string.Empty, string.Empty));
                case "remote" when request.Arguments.Contains("get-url"):
                    return Task.FromResult(new ProcessResult(1, string.Empty, string.Empty));
                case "remote":
                    return Task.FromResult(new ProcessResult(0, string.Empty, string.Empty));
                case "fetch":
                case "cat-file":
                    return Task.FromResult(new ProcessResult(0, string.Empty, string.Empty));
                case "ls-tree":
                    return Task.FromResult(new ProcessResult(
                        0,
                        "100644 blob 0123456789012345678901234567890123456789\tworkspace_one/stack_web/compose.yaml\n",
                        string.Empty));
                case "read-tree":
                    File.WriteAllText(Path.Combine(request.WorkingDirectory, "compose.yaml"), "services: {}");
                    return Task.FromResult(new ProcessResult(0, string.Empty, string.Empty));
                default:
                    return Task.FromResult(new ProcessResult(1, string.Empty, string.Empty));
            }
        }
    }
}
