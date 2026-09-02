using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using StackPivot.Control.Domain.Entities;
using StackPivot.Control.Infrastructure.Git;
using StackPivot.Control.Infrastructure.Persistence;
using Xunit;

namespace StackPivot.Control.Tests;

public sealed class CentralGitPreflightTests
{
    private static readonly string[] ExpectedCatFile = ["cat-file", "-e", "0123456789abcdef0123456789abcdef01234567^{commit}"];
    private static readonly string[] ExpectedLsTree = ["ls-tree", "-r", "--name-only", "0123456789abcdef0123456789abcdef01234567", "--", "workspace_one/stack_web"];

    [Theory]
    [InlineData("http://git.example/repository.git")]
    [InlineData("https://user:password@git.example/repository.git")]
    [InlineData("https://unknown.example/repository.git")]
    [InlineData("https://git.example/repository.git\n--upload-pack=evil")]
    public void RemotePolicyRejectsNonHttpsOrUnconfiguredHosts(string remote)
    {
        var hosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "git.example" };

        Assert.False(CentralGitPreflight.IsAllowedRemote(remote, hosts));
    }

    [Fact]
    public async Task PreflightUsesParameterizedCommitAndStackTreeChecks()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<StackPivotDbContext>().UseSqlite(connection).Options;
        await using var db = new StackPivotDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var workspace = new Workspace { WorkspaceId = Guid.NewGuid(), Name = "workspace_one", DisplayName = "Workspace" };
        var stack = new Stack { StackId = Guid.NewGuid(), WorkspaceId = workspace.WorkspaceId, FolderName = "stack_web", DisplayName = "Web" };
        db.AddRange(workspace, stack, new GlobalGitSetting
        {
            Id = 1,
            GitRepo = "https://git.example/repository.git",
            GitUserName = "git-user",
            AccessTokenEncrypted = "ciphertext",
            TokenKeyId = "git-key-v1"
        });
        await db.SaveChangesAsync();
        var runner = new RecordingGitRunner(
            new GitCommandResult(0, string.Empty, string.Empty),
            new GitCommandResult(0, "workspace_one/stack_web/compose.yaml\nworkspace_one/stack_web/app.env\n", string.Empty));
        var preflight = new CentralGitPreflight(
            db,
            runner,
            new CentralGitOptions
            {
                MainRoot = "/opt/main",
                AllowedRemoteHosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "git.example" }
            });

        var result = await preflight.ValidateAsync(
            stack.StackId,
            "0123456789abcdef0123456789abcdef01234567",
            CancellationToken.None);

        Assert.Equal("workspace_one/stack_web", result.StackGitRelativePath);
        Assert.Equal(ExpectedCatFile, runner.Arguments[0]);
        Assert.Equal(ExpectedLsTree, runner.Arguments[1]);
    }

    [Fact]
    public async Task PreflightRejectsSensitiveEnvInNestedStackPath()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<StackPivotDbContext>().UseSqlite(connection).Options;
        await using var db = new StackPivotDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var workspace = new Workspace { WorkspaceId = Guid.NewGuid(), Name = "workspace_one", DisplayName = "Workspace" };
        var stack = new Stack { StackId = Guid.NewGuid(), WorkspaceId = workspace.WorkspaceId, FolderName = "stack_web", DisplayName = "Web" };
        db.AddRange(workspace, stack, new GlobalGitSetting
        {
            Id = 1,
            GitRepo = "https://git.example/repository.git",
            GitUserName = "git-user",
            AccessTokenEncrypted = "ciphertext",
            TokenKeyId = "git-key-v1"
        });
        await db.SaveChangesAsync();
        var runner = new RecordingGitRunner(
            new GitCommandResult(0, string.Empty, string.Empty),
            new GitCommandResult(0, "workspace_one/stack_web/compose.yaml\nworkspace_one/stack_web/config/.env\n", string.Empty));
        var preflight = new CentralGitPreflight(db, runner, new CentralGitOptions
        {
            MainRoot = "/opt/main",
            AllowedRemoteHosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "git.example" }
        });

        var exception = await Assert.ThrowsAsync<DeploymentValidationException>(() => preflight.ValidateAsync(
            stack.StackId,
            "0123456789abcdef0123456789abcdef01234567",
            CancellationToken.None));

        Assert.Equal("policy_violation", exception.Code);
    }

    [Fact]
    public async Task PreflightRejectsTruncatedTreeOutput()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<StackPivotDbContext>().UseSqlite(connection).Options;
        await using var db = new StackPivotDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var workspace = new Workspace { WorkspaceId = Guid.NewGuid(), Name = "workspace_one", DisplayName = "Workspace" };
        var stack = new Stack { StackId = Guid.NewGuid(), WorkspaceId = workspace.WorkspaceId, FolderName = "stack_web", DisplayName = "Web" };
        db.AddRange(workspace, stack, new GlobalGitSetting
        {
            Id = 1,
            GitRepo = "https://git.example/repository.git",
            GitUserName = "git-user",
            AccessTokenEncrypted = "ciphertext",
            TokenKeyId = "git-key-v1"
        });
        await db.SaveChangesAsync();
        var runner = new RecordingGitRunner(
            new GitCommandResult(0, string.Empty, string.Empty),
            new GitCommandResult(0, "workspace_one/stack_web/compose.yaml\n", string.Empty, OutputTruncated: true));
        var preflight = new CentralGitPreflight(db, runner, new CentralGitOptions
        {
            MainRoot = "/opt/main",
            AllowedRemoteHosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "git.example" }
        });

        var exception = await Assert.ThrowsAsync<DeploymentValidationException>(() => preflight.ValidateAsync(
            stack.StackId,
            "0123456789abcdef0123456789abcdef01234567",
            CancellationToken.None));

        Assert.Equal("git_output_truncated", exception.Code);
        Assert.Equal(422, exception.StatusCode);
    }

    private sealed class RecordingGitRunner(params GitCommandResult[] results) : IGitCommandRunner
    {
        private int resultIndex;
        public List<IReadOnlyList<string>> Arguments { get; } = new();

        public Task<GitCommandResult> RunAsync(string workingDirectory, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
        {
            Arguments.Add(arguments.ToArray());
            return Task.FromResult(results[Math.Min(resultIndex++, results.Length - 1)]);
        }
    }
}
