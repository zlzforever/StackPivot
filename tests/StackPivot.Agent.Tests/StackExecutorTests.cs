using StackPivot.Agent;
using StackPivot.Agent.Execution;
using StackPivot.Agent.Security;
using StackPivot.Contracts.Deployments;
using StackPivot.Contracts.SignalR;
using Xunit;

namespace StackPivot.Agent.Tests;

public sealed class StackExecutorTests
{
    private static readonly string[] VersionArguments = ["compose", "version"];
    private static readonly string[] UpArguments = ["compose", "up", "-d"];
    private static readonly string[] GitEvents = ["git"];

    [Fact]
    public async Task ValidTaskChecksComposeBeforeGitAndClearsToken()
    {
        var root = Path.Combine(Path.GetTempPath(), "stackpivot-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var processRunner = new ComposeExecutorTests.RecordingProcessRunner(
                new ProcessResult(0, "Docker Compose version v2.24.0", string.Empty),
                new ProcessResult(0, "started", string.Empty));
            var compose = new ComposeExecutor(processRunner, TimeSpan.FromSeconds(5));
            var events = new List<string>();
            var git = new FakeGitCheckout(events);
            var executor = new StackExecutor(
                new AgentOptions(Guid.Parse("00000000-0000-0000-0000-000000000001"), "wss://control.example/hubs/agent", "api-key", root),
                new PathPolicy(root),
                compose,
                git);
            var token = "secret"u8.ToArray();
            var command = new DeployStackCommand(
                ProtocolVersion.Current,
                Guid.NewGuid(),
                Guid.NewGuid(),
                Guid.NewGuid(),
                Guid.Parse("00000000-0000-0000-0000-000000000001"),
                "https://git.example/repository.git",
                "git-user",
                token,
                "0123456789abcdef0123456789abcdef01234567",
                "workspace_one/stack_web",
                Path.Combine(root, "workspace_one", "stack_web"),
                DateTimeOffset.UtcNow.AddMinutes(5));

            var result = await executor.ExecuteAsync(command, CancellationToken.None);

            Assert.True(result.Success);
            Assert.Equal(VersionArguments, processRunner.Requests[0].Arguments);
            Assert.Equal(UpArguments, processRunner.Requests[1].Arguments);
            Assert.Equal(GitEvents, events);
            Assert.All(token, value => Assert.Equal(0, value));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task MissingLocalPathReturnsInvalidPathAndClearsToken()
    {
        var root = Path.Combine(Path.GetTempPath(), "stackpivot-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var processRunner = new ComposeExecutorTests.RecordingProcessRunner(
                new ProcessResult(0, "Docker Compose version v2.24.0", string.Empty));
            var executor = new StackExecutor(
                new AgentOptions(AgentId, "wss://control.example/hubs/agent", "api-key", root),
                new PathPolicy(root),
                new ComposeExecutor(processRunner, TimeSpan.FromSeconds(5)),
                new FakeGitCheckout(new List<string>()));
            var token = "secret"u8.ToArray();
            var command = new DeployStackCommand(
                ProtocolVersion.Current,
                Guid.NewGuid(),
                Guid.NewGuid(),
                Guid.NewGuid(),
                AgentId,
                "https://git.example/repository.git",
                "git-user",
                token,
                "0123456789abcdef0123456789abcdef01234567",
                "workspace_one/stack_web",
                null!,
                DateTimeOffset.UtcNow.AddMinutes(5));

            var result = await executor.ExecuteAsync(command, CancellationToken.None);

            Assert.False(result.Success);
            Assert.Equal("invalid_path", result.ErrorCode);
            Assert.All(token, value => Assert.Equal(0, value));
            Assert.Empty(processRunner.Requests);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task MissingAccessTokenIsRejectedAndDoesNotMaskCleanup()
    {
        var root = Path.Combine(Path.GetTempPath(), "stackpivot-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var processRunner = new ComposeExecutorTests.RecordingProcessRunner();
            var executor = new StackExecutor(
                new AgentOptions(AgentId, "wss://control.example/hubs/agent", "api-key", root),
                new PathPolicy(root),
                new ComposeExecutor(processRunner, TimeSpan.FromSeconds(5)),
                new FakeGitCheckout(new List<string>()));
            var command = new DeployStackCommand(
                ProtocolVersion.Current,
                Guid.NewGuid(),
                Guid.NewGuid(),
                Guid.NewGuid(),
                AgentId,
                "https://git.example/repository.git",
                "git-user",
                null!,
                "0123456789abcdef0123456789abcdef01234567",
                "workspace_one/stack_web",
                Path.Combine(root, "workspace_one", "stack_web"),
                DateTimeOffset.UtcNow.AddMinutes(5));

            var result = await executor.ExecuteAsync(command, CancellationToken.None);

            Assert.False(result.Success);
            Assert.Equal("invalid_credential", result.ErrorCode);
            Assert.Empty(processRunner.Requests);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static readonly Guid AgentId = Guid.Parse("00000000-0000-0000-0000-000000000001");

    private sealed class FakeGitCheckout(List<string> events) : IGitCheckoutExecutor
    {
        public Task<GitCheckoutResult> MaterializeAsync(GitDeploymentInput input, CancellationToken cancellationToken)
        {
            events.Add("git");
            return Task.FromResult(new GitCheckoutResult(true, null, Array.Empty<string>()));
        }
    }
}
