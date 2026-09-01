using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using StackPivot.Control.Application.Deployments;
using StackPivot.Control.Application.Audit;
using StackPivot.Control.Authorization;
using StackPivot.Control.Domain.Entities;
using StackPivot.Control.Infrastructure.Persistence;
using StackPivot.Control.Infrastructure.Git;
using StackPivot.Contracts.Deployments;
using Xunit;

namespace StackPivot.Control.Tests;

public sealed class DeploymentServiceTests
{
    [Fact]
    public async Task RequestCreatesOnePendingTaskAndAuditForEachBoundAgent()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<StackPivotDbContext>().UseSqlite(connection).Options;
        await using var db = new StackPivotDbContext(options);
        await db.Database.EnsureCreatedAsync();

        var user = new UserAccount
        {
            UserId = Guid.NewGuid(),
            UserName = "editor",
            SsoSubject = "editor-subject"
        };
        var workspace = new Workspace
        {
            WorkspaceId = Guid.NewGuid(),
            Name = "workspace_one",
            DisplayName = "Workspace One"
        };
        var stack = new Stack
        {
            StackId = Guid.NewGuid(),
            WorkspaceId = workspace.WorkspaceId,
            FolderName = "stack_web",
            DisplayName = "Web Stack"
        };
        var agents = Enumerable.Range(0, 2).Select(_ => new AgentNode
        {
            AgentId = Guid.NewGuid(),
            Name = Guid.NewGuid().ToString("N"),
            ApiKeyHash = Guid.NewGuid().ToString("N"),
            ApiKeyVersion = 1
        }).ToArray();
        db.AddRange(user, workspace, stack);
        db.AddRange(agents);
        db.Add(new WorkspaceMember
        {
            Id = Guid.NewGuid(),
            WorkspaceId = workspace.WorkspaceId,
            UserId = user.UserId,
            Permission = WorkspacePermission.Editor
        });
        db.AddRange(agents.Select(agent => new StackAgentBinding
        {
            Id = Guid.NewGuid(),
            StackId = stack.StackId,
            AgentId = agent.AgentId
        }));
        await db.SaveChangesAsync();

        var preflight = new FakePreflight();
        var service = new DeploymentService(
            db,
            new WorkspaceAuthorizationService(db),
            preflight,
            new AuditWriter(db));
        var request = new DeployStackRequest(
            "0123456789abcdef0123456789abcdef01234567",
            DeploymentMode.BoundAgents,
            null);
        var requestId = Guid.NewGuid();
        var idempotencyKey = Guid.NewGuid();

        var result = await service.RequestAsync(
            user.UserId,
            stack.StackId,
            request,
            requestId,
            idempotencyKey,
            CancellationToken.None);

        Assert.Equal(requestId, result.RequestId);
        Assert.Equal(2, result.Tasks.Count);
        Assert.All(result.Tasks, task => Assert.Equal("pending", task.Status));
        Assert.Equal(2, await db.ServiceOperationHistories.CountAsync());
        Assert.All(await db.ServiceOperationHistories.ToListAsync(), history =>
            Assert.Equal("pending", history.TaskStatus));
        Assert.Contains(await db.AuditLogs.ToListAsync(), audit => audit.Action == AuditActions.DeployRequested);
        Assert.Equal(1, preflight.CallCount);
    }

    [Fact]
    public async Task ReusingIdempotencyKeyWithDifferentBodyIsRejected()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<StackPivotDbContext>().UseSqlite(connection).Options;
        await using var db = new StackPivotDbContext(options);
        await db.Database.EnsureCreatedAsync();

        var fixture = await DeploymentFixture.SeedAsync(db);
        var service = new DeploymentService(
            db,
            new WorkspaceAuthorizationService(db),
            new FakePreflight(),
            new AuditWriter(db));
        var key = Guid.NewGuid();
        var first = new DeployStackRequest(
            "0123456789abcdef0123456789abcdef01234567",
            DeploymentMode.BoundAgents,
            null);
        var second = new DeployStackRequest(
            "abcdef0123456789abcdef0123456789abcdef01",
            DeploymentMode.BoundAgents,
            null);

        await service.RequestAsync(
            fixture.UserId,
            fixture.StackId,
            first,
            Guid.NewGuid(),
            key,
            CancellationToken.None);

        var exception = await Assert.ThrowsAsync<DeploymentRequestException>(() => service.RequestAsync(
            fixture.UserId,
            fixture.StackId,
            second,
            Guid.NewGuid(),
            key,
            CancellationToken.None));

        Assert.Equal("idempotency_key_reused", exception.Code);
        Assert.Equal(409, exception.StatusCode);
    }

    [Fact]
    public async Task SameIdempotencyKeyCannotReplayARequestForAnotherStack()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<StackPivotDbContext>().UseSqlite(connection).Options;
        await using var db = new StackPivotDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var fixture = await DeploymentFixture.SeedTwoStacksAsync(db);
        var service = new DeploymentService(db, new WorkspaceAuthorizationService(db), new FakePreflight(), new AuditWriter(db));
        var request = new DeployStackRequest("0123456789abcdef0123456789abcdef01234567", DeploymentMode.BoundAgents, null);
        var key = Guid.NewGuid();

        await service.RequestAsync(fixture.UserId, fixture.FirstStackId, request, Guid.NewGuid(), key, CancellationToken.None);

        var exception = await Assert.ThrowsAsync<DeploymentRequestException>(() => service.RequestAsync(
            fixture.UserId, fixture.SecondStackId, request, Guid.NewGuid(), key, CancellationToken.None));

        Assert.Equal("idempotency_key_reused", exception.Code);
        Assert.Equal(409, exception.StatusCode);
    }

    private sealed class FakePreflight : ICentralGitPreflight
    {
        public int CallCount { get; private set; }

        public Task<DeploymentPreflight> ValidateAsync(
            Guid stackId,
            string fullCommitHash,
            CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult(new DeploymentPreflight(
                "https://git.example/repository.git",
                "git-user",
                "workspace_one/stack_web",
                "/opt/agent-main/workspace_one/stack_web",
                "git-key-v1"));
        }
    }

    private sealed record DeploymentFixture(Guid UserId, Guid StackId)
    {
        public static async Task<DeploymentFixture> SeedAsync(StackPivotDbContext db)
        {
            var user = new UserAccount
            {
                UserId = Guid.NewGuid(),
                UserName = "editor",
                SsoSubject = Guid.NewGuid().ToString("N")
            };
            var workspace = new Workspace
            {
                WorkspaceId = Guid.NewGuid(),
                Name = "workspace_one",
                DisplayName = "Workspace One"
            };
            var stack = new Stack
            {
                StackId = Guid.NewGuid(),
                WorkspaceId = workspace.WorkspaceId,
                FolderName = "stack_web",
                DisplayName = "Web Stack"
            };
            var agent = new AgentNode
            {
                AgentId = Guid.NewGuid(),
                Name = "agent-one",
                ApiKeyHash = Guid.NewGuid().ToString("N"),
                ApiKeyVersion = 1
            };
            db.AddRange(user, workspace, stack, agent);
            db.AddRange(
                new WorkspaceMember
                {
                    Id = Guid.NewGuid(),
                    WorkspaceId = workspace.WorkspaceId,
                    UserId = user.UserId,
                    Permission = WorkspacePermission.Editor
                },
                new StackAgentBinding
                {
                    Id = Guid.NewGuid(),
                    StackId = stack.StackId,
                    AgentId = agent.AgentId
                });
            await db.SaveChangesAsync();
            return new DeploymentFixture(user.UserId, stack.StackId);
        }

        public static async Task<TwoStackFixture> SeedTwoStacksAsync(StackPivotDbContext db)
        {
            var user = new UserAccount { UserId = Guid.NewGuid(), UserName = "editor", SsoSubject = Guid.NewGuid().ToString("N") };
            var workspace = new Workspace { WorkspaceId = Guid.NewGuid(), Name = "workspace_one", DisplayName = "Workspace One" };
            var firstStack = new Stack { StackId = Guid.NewGuid(), WorkspaceId = workspace.WorkspaceId, FolderName = "stack_one", DisplayName = "Stack One" };
            var secondStack = new Stack { StackId = Guid.NewGuid(), WorkspaceId = workspace.WorkspaceId, FolderName = "stack_two", DisplayName = "Stack Two" };
            var agent = new AgentNode { AgentId = Guid.NewGuid(), Name = "agent-one", ApiKeyHash = Guid.NewGuid().ToString("N"), ApiKeyVersion = 1 };
            db.AddRange(user, workspace, firstStack, secondStack, agent);
            db.Add(new WorkspaceMember { Id = Guid.NewGuid(), WorkspaceId = workspace.WorkspaceId, UserId = user.UserId, Permission = WorkspacePermission.Editor });
            db.AddRange(
                new StackAgentBinding { Id = Guid.NewGuid(), StackId = firstStack.StackId, AgentId = agent.AgentId },
                new StackAgentBinding { Id = Guid.NewGuid(), StackId = secondStack.StackId, AgentId = agent.AgentId });
            await db.SaveChangesAsync();
            return new TwoStackFixture(user.UserId, firstStack.StackId, secondStack.StackId);
        }
    }

    private sealed record TwoStackFixture(Guid UserId, Guid FirstStackId, Guid SecondStackId);
}
