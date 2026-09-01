using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using StackPivot.Control.Application.Audit;
using StackPivot.Control.Application.Deployments;
using StackPivot.Control.Authorization;
using StackPivot.Control.Domain.Entities;
using StackPivot.Control.Infrastructure.Persistence;
using StackPivot.Control.Infrastructure.Security;
using StackPivot.Contracts.Agents;
using StackPivot.Contracts.Deployments;
using Xunit;

namespace StackPivot.Control.Tests;

public sealed class DeploymentEventTests
{
    [Fact]
    public async Task TaskLogsAcceptOnlyTheNextSequenceAndAreRedacted()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<StackPivotDbContext>().UseSqlite(connection).Options;
        await using var db = new StackPivotDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var fixture = await SeedAsync(db);
        var dispatcher = new DeploymentDispatcher(
            db,
            new OfflineTransport(),
            new AesGcmGitCredentialProtector(Enumerable.Range(1, 32).Select(value => (byte)value).ToArray()),
            new AuditWriter(db));

        var fixtureTask = await db.ServiceOperationHistories.SingleAsync();
        fixtureTask.DispatchedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();
        await dispatcher.HandleAcceptedAsync(new TaskAccepted(1, fixture.TaskId, fixture.AgentId, DateTimeOffset.UtcNow), CancellationToken.None);
        await dispatcher.HandleLogAsync(new TaskLog(1, fixture.TaskId, fixture.AgentId, 0, "stdout", "Authorization: Bearer hidden-token", DateTimeOffset.UtcNow), CancellationToken.None);
        await dispatcher.HandleLogAsync(new TaskLog(1, fixture.TaskId, fixture.AgentId, 0, "stdout", "duplicate", DateTimeOffset.UtcNow), CancellationToken.None);
        await dispatcher.HandleLogAsync(new TaskLog(1, fixture.TaskId, fixture.AgentId, 2, "stdout", "out-of-order", DateTimeOffset.UtcNow), CancellationToken.None);
        await dispatcher.HandleLogAsync(new TaskLog(1, fixture.TaskId, fixture.AgentId, 1, "stdout", "password=hunter2", DateTimeOffset.UtcNow), CancellationToken.None);

        var history = await db.ServiceOperationHistories.SingleAsync();
        Assert.Equal(1, history.LastSequence);
        Assert.DoesNotContain("hidden-token", history.OutputLog);
        Assert.DoesNotContain("hunter2", history.OutputLog);
        Assert.Contains("[REDACTED]", history.OutputLog);
        Assert.DoesNotContain("duplicate", history.OutputLog);
        Assert.DoesNotContain("out-of-order", history.OutputLog);
    }

    [Fact]
    public async Task LogsAndCompletionBeforeAcceptanceAreIgnored()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<StackPivotDbContext>().UseSqlite(connection).Options;
        await using var db = new StackPivotDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var fixture = await SeedAsync(db);
        var history = await db.ServiceOperationHistories.SingleAsync();
        history.DispatchedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();
        var dispatcher = new DeploymentDispatcher(
            db,
            new OfflineTransport(),
            new AesGcmGitCredentialProtector(Enumerable.Range(1, 32).Select(value => (byte)value).ToArray()),
            new AuditWriter(db));

        await dispatcher.HandleLogAsync(new TaskLog(1, fixture.TaskId, fixture.AgentId, 0, "stdout", "premature", DateTimeOffset.UtcNow), CancellationToken.None);
        await dispatcher.HandleCompletedAsync(new TaskCompleted(1, fixture.TaskId, fixture.AgentId, true, 0, null, DateTimeOffset.UtcNow), CancellationToken.None);

        history = await db.ServiceOperationHistories.SingleAsync();
        Assert.Equal("pending", history.TaskStatus);
        Assert.Equal(-1, history.LastSequence);
        Assert.Empty(history.OutputLog);
    }

    [Fact]
    public async Task AcceptedEventIsHandledBeforeDispatchMarkerIsPersisted()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<StackPivotDbContext>().UseSqlite(connection).Options;
        await using var db = new StackPivotDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var fixture = await SeedAsync(db);
        var dispatcher = new DeploymentDispatcher(
            db,
            new OfflineTransport(),
            new AesGcmGitCredentialProtector(Enumerable.Range(1, 32).Select(value => (byte)value).ToArray()),
            new AuditWriter(db));

        await dispatcher.HandleAcceptedAsync(
            new TaskAccepted(1, fixture.TaskId, fixture.AgentId, DateTimeOffset.UtcNow),
            CancellationToken.None);

        var history = await db.ServiceOperationHistories.SingleAsync();
        Assert.NotNull(history.StartTime);
        Assert.Null(history.DispatchedAt);
    }

    [Fact]
    public async Task DispatchSendExceptionFailsTheTaskInsteadOfLeavingItPending()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<StackPivotDbContext>().UseSqlite(connection).Options;
        await using var db = new StackPivotDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var fixture = await SeedAsync(db);
        await db.GlobalGitSettings.AddAsync(new GlobalGitSetting
        {
            Id = 1,
            GitRepo = "https://git.example/repository.git",
            GitUserName = "git-user",
            AccessTokenEncrypted = new AesGcmGitCredentialProtector(Enumerable.Range(1, 32).Select(value => (byte)value).ToArray()).Protect("git-token", "git-key-v1"),
            TokenKeyId = "git-key-v1"
        });
        await db.SaveChangesAsync();
        var dispatcher = new DeploymentDispatcher(
            db,
            new ThrowingTransport(),
            new AesGcmGitCredentialProtector(Enumerable.Range(1, 32).Select(value => (byte)value).ToArray()),
            new AuditWriter(db));

        await dispatcher.DispatchPendingAsync(CancellationToken.None);

        var history = await db.ServiceOperationHistories.SingleAsync();
        Assert.Equal("failed", history.TaskStatus);
        Assert.Equal("agent_offline", history.ErrorCode);
        Assert.Contains(await db.AuditLogs.ToListAsync(), audit => audit.Action == AuditActions.TaskFailed);
    }

    [Fact]
    public async Task SuccessfullyDispatchedTaskIsNotSentAgainBeforeAcceptance()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<StackPivotDbContext>().UseSqlite(connection).Options;
        await using var db = new StackPivotDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var fixture = await SeedAsync(db);
        var protector = new AesGcmGitCredentialProtector(Enumerable.Range(1, 32).Select(value => (byte)value).ToArray());
        await db.GlobalGitSettings.AddAsync(new GlobalGitSetting
        {
            Id = 1,
            GitRepo = "https://git.example/repository.git",
            GitUserName = "git-user",
            AccessTokenEncrypted = protector.Protect("git-token", "git-key-v1"),
            TokenKeyId = "git-key-v1"
        });
        await db.SaveChangesAsync();
        var transport = new RecordingTransport();
        var dispatcher = new DeploymentDispatcher(db, transport, protector, new AuditWriter(db));

        await dispatcher.DispatchPendingAsync(CancellationToken.None);
        await dispatcher.DispatchPendingAsync(CancellationToken.None);

        Assert.Single(transport.Commands);
        var history = await db.ServiceOperationHistories.SingleAsync();
        Assert.NotNull(history.DispatchedAt);
        Assert.Equal("git-key-v1", history.TokenKeyId);
    }

    [Fact]
    public async Task StaleDispatchedTaskIsFailedInsteadOfRemainingActive()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<StackPivotDbContext>().UseSqlite(connection).Options;
        await using var db = new StackPivotDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var fixture = await SeedAsync(db);
        var staleAt = DateTimeOffset.UtcNow.AddHours(-1);
        var history = await db.ServiceOperationHistories.SingleAsync();
        history.DispatchedAt = staleAt;
        history.LastEventAt = staleAt;
        await db.SaveChangesAsync();

        var transport = new RecordingTransport();
        var dispatcher = new DeploymentDispatcher(
            db,
            transport,
            new AesGcmGitCredentialProtector(Enumerable.Range(1, 32).Select(value => (byte)value).ToArray()),
            new AuditWriter(db));

        await dispatcher.DispatchPendingAsync(CancellationToken.None);

        history = await db.ServiceOperationHistories.SingleAsync(value => value.TaskId == fixture.TaskId);
        Assert.Equal("failed", history.TaskStatus);
        Assert.Equal("agent_timeout", history.ErrorCode);
        Assert.NotNull(history.FinishTime);
        Assert.Empty(transport.Commands);
        Assert.Contains(await db.AuditLogs.ToListAsync(), audit =>
            audit.Action == AuditActions.TaskFailed && audit.ErrorCode == "agent_timeout");
    }

    [Fact]
    public async Task HeartbeatUsesControlServerTimeInsteadOfAgentTimestamp()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<StackPivotDbContext>().UseSqlite(connection).Options;
        await using var db = new StackPivotDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var fixture = await SeedAsync(db);
        var dispatcher = new DeploymentDispatcher(
            db,
            new OfflineTransport(),
            new AesGcmGitCredentialProtector(Enumerable.Range(1, 32).Select(value => (byte)value).ToArray()),
            new AuditWriter(db));
        var oldTimestamp = DateTimeOffset.UtcNow.AddYears(-1);

        await dispatcher.HandleHeartbeatAsync(new HeartbeatMessage(1, fixture.AgentId, oldTimestamp), CancellationToken.None);

        var agent = await db.AgentNodes.SingleAsync(value => value.AgentId == fixture.AgentId);
        Assert.True(agent.LastSeenAt > oldTimestamp);
        Assert.True(agent.LastSeenAt >= DateTimeOffset.UtcNow.AddSeconds(-5));
    }

    [Fact]
    public void DeploymentLogTruncationDoesNotSplitUtf8Characters()
    {
        var line = new string('\u754c', DeploymentLogSanitizer.MaxLineBytes);

        var sanitized = DeploymentLogSanitizer.SanitizeLine(line);

        Assert.True(System.Text.Encoding.UTF8.GetByteCount(sanitized) <= DeploymentLogSanitizer.MaxLineBytes);
        Assert.DoesNotContain('\ufffd', sanitized);
    }

    [Fact]
    public async Task OfflinePendingTaskIsFailedWithStableErrorCode()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<StackPivotDbContext>().UseSqlite(connection).Options;
        await using var db = new StackPivotDbContext(options);
        await db.Database.EnsureCreatedAsync();
        await SeedAsync(db);
        var dispatcher = new DeploymentDispatcher(
            db,
            new OfflineTransport(),
            new AesGcmGitCredentialProtector(Enumerable.Range(1, 32).Select(value => (byte)value).ToArray()),
            new AuditWriter(db));

        await dispatcher.DispatchPendingAsync(CancellationToken.None);

        var history = await db.ServiceOperationHistories.SingleAsync();
        Assert.Equal("failed", history.TaskStatus);
        Assert.Equal("agent_offline", history.ErrorCode);
    }

    private static async Task<Fixture> SeedAsync(StackPivotDbContext db)
    {
        var user = new UserAccount { UserId = Guid.NewGuid(), UserName = "editor", SsoSubject = Guid.NewGuid().ToString("N") };
        var workspace = new Workspace { WorkspaceId = Guid.NewGuid(), Name = "workspace_one", DisplayName = "Workspace One" };
        var stack = new Stack { StackId = Guid.NewGuid(), WorkspaceId = workspace.WorkspaceId, FolderName = "stack_web", DisplayName = "Web" };
        var agent = new AgentNode { AgentId = Guid.NewGuid(), Name = "agent", ApiKeyHash = Guid.NewGuid().ToString("N"), ApiKeyVersion = 1 };
        var taskId = Guid.NewGuid();
        db.AddRange(user, workspace, stack, agent);
        db.AddRange(
            new WorkspaceMember { Id = Guid.NewGuid(), WorkspaceId = workspace.WorkspaceId, UserId = user.UserId, Permission = WorkspacePermission.Editor },
            new StackAgentBinding { Id = Guid.NewGuid(), StackId = stack.StackId, AgentId = agent.AgentId },
            new DeploymentRequestEntity { RequestId = Guid.NewGuid(), StackId = stack.StackId, UserId = user.UserId, IdempotencyKey = Guid.NewGuid().ToString(), RequestFingerprint = "fingerprint", TargetCommitHash = "0123456789abcdef0123456789abcdef01234567", Mode = DeploymentMode.BoundAgents, CreatedAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync();
        var request = await db.DeploymentRequests.SingleAsync();
        db.ServiceOperationHistories.Add(new ServiceOperationHistory
        {
            HistoryId = Guid.NewGuid(), TaskId = taskId, RequestId = request.RequestId, StackId = stack.StackId, AgentId = agent.AgentId, UserId = user.UserId,
            TargetCommitHash = request.TargetCommitHash, TaskStatus = "pending", CommandText = "docker compose up -d", OutputLog = string.Empty, LastSequence = -1, LastEventAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();
        return new Fixture(taskId, agent.AgentId);
    }

    private sealed record Fixture(Guid TaskId, Guid AgentId);

    private sealed class OfflineTransport : IAgentTransport
    {
        public Task<bool> IsConnectedAsync(Guid agentId, CancellationToken cancellationToken) => Task.FromResult(false);
        public Task SendDeployAsync(DeployStackCommand command, CancellationToken cancellationToken) => throw new InvalidOperationException();
    }

    private sealed class ThrowingTransport : IAgentTransport
    {
        public Task<bool> IsConnectedAsync(Guid agentId, CancellationToken cancellationToken) => Task.FromResult(true);
        public Task SendDeployAsync(DeployStackCommand command, CancellationToken cancellationToken) => throw new TimeoutException("send failed");
    }

    private sealed class RecordingTransport : IAgentTransport
    {
        public List<DeployStackCommand> Commands { get; } = new();
        public Task<bool> IsConnectedAsync(Guid agentId, CancellationToken cancellationToken) => Task.FromResult(true);
        public Task SendDeployAsync(DeployStackCommand command, CancellationToken cancellationToken)
        {
            Commands.Add(command);
            return Task.CompletedTask;
        }
    }
}
