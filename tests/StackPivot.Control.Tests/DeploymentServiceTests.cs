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

    [Fact]
    public async Task RequestPersistsPreflightSnapshotOnEveryTask()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<StackPivotDbContext>().UseSqlite(connection).Options;
        await using var db = new StackPivotDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var fixture = await DeploymentFixture.SeedAsync(db);
        var service = new DeploymentService(db, new WorkspaceAuthorizationService(db), new SnapshotPreflight(), new AuditWriter(db));

        await service.RequestAsync(
            fixture.UserId,
            fixture.StackId,
            new DeployStackRequest("0123456789abcdef0123456789abcdef01234567", DeploymentMode.BoundAgents, null),
            Guid.NewGuid(),
            Guid.NewGuid(),
            CancellationToken.None);

        var history = await db.ServiceOperationHistories.SingleAsync();
        Assert.Equal("https://snapshotted.example/repository.git", history.GitRepoSnapshot);
        Assert.Equal("snapshot-user", history.GitUserNameSnapshot);
        Assert.Equal("workspace_snapshot/stack_snapshot", history.StackGitRelativePathSnapshot);
        Assert.Equal("/opt/agent-main/workspace_snapshot/stack_snapshot", history.AgentStackLocalPathSnapshot);
        Assert.Equal("snapshot-key-v7", history.TokenKeyId);
    }

    [Fact]
    public async Task OperationsUseOpaqueCursorWithStableTieBreaking()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<StackPivotDbContext>().UseSqlite(connection).Options;
        await using var db = new StackPivotDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var fixture = await DeploymentFixture.SeedAsync(db);
        var request = new DeploymentRequestEntity
        {
            RequestId = Guid.NewGuid(),
            StackId = fixture.StackId,
            UserId = fixture.UserId,
            IdempotencyKey = Guid.NewGuid().ToString(),
            RequestFingerprint = "cursor-fixture",
            TargetCommitHash = "0123456789abcdef0123456789abcdef01234567",
            Mode = DeploymentMode.BoundAgents,
            CreatedAt = DateTimeOffset.UtcNow
        };
        db.DeploymentRequests.Add(request);
        var firstTime = DateTimeOffset.UtcNow.AddMinutes(-1);
        for (var index = 0; index < 3; index++)
        {
            db.ServiceOperationHistories.Add(new ServiceOperationHistory
            {
                HistoryId = Guid.NewGuid(),
                TaskId = Guid.NewGuid(),
                RequestId = request.RequestId,
                StackId = fixture.StackId,
                AgentId = Guid.NewGuid(),
                UserId = fixture.UserId,
                TargetCommitHash = "0123456789abcdef0123456789abcdef01234567",
                TaskStatus = "success",
                OutputLog = string.Empty,
                LastSequence = -1,
                LastEventAt = firstTime
            });
        }

        await db.SaveChangesAsync();
        var service = new DeploymentService(db, new WorkspaceAuthorizationService(db), new FakePreflight(), new AuditWriter(db));

        var first = await service.GetOperationsPageAsync(fixture.UserId, fixture.StackId, 2, null, CancellationToken.None);
        var firstPage = first ?? throw new InvalidOperationException("Expected an operations page.");
        var second = await service.GetOperationsPageAsync(fixture.UserId, fixture.StackId, 2, firstPage.NextCursor, CancellationToken.None);
        var secondPage = second ?? throw new InvalidOperationException("Expected a second operations page.");

        Assert.Equal(2, firstPage.Items.Count);
        Assert.False(string.IsNullOrWhiteSpace(firstPage.NextCursor));
        Assert.Single(secondPage.Items);
        Assert.Null(secondPage.NextCursor);
        Assert.DoesNotContain(firstPage.NextCursor!, "0123456789abcdef", StringComparison.Ordinal);
        Assert.DoesNotContain(secondPage.Items[0].TaskId, firstPage.Items.Select(item => item.TaskId));
    }

    [Fact]
    public async Task ConcurrentContextsAllowOnlyOnePendingDeploymentForTheSameTarget()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), "stackpivot-deployment-" + Guid.NewGuid().ToString("N") + ".db");
        var connectionString = $"Data Source={databasePath};Default Timeout=5";
        var preflight = new ConcurrentPreflight();
        var request = new DeployStackRequest(
            "0123456789abcdef0123456789abcdef01234567",
            DeploymentMode.BoundAgents,
            null);
        try
        {
            var options = new DbContextOptionsBuilder<StackPivotDbContext>()
                .UseSqlite(connectionString)
                .Options;
            DeploymentFixture fixture;
            await using (var seed = new StackPivotDbContext(options))
            {
                await seed.Database.EnsureCreatedAsync();
                fixture = await DeploymentFixture.SeedAsync(seed);
            }

            await using var firstDb = new StackPivotDbContext(options);
            await using var secondDb = new StackPivotDbContext(options);
            var firstService = new DeploymentService(
                firstDb,
                new WorkspaceAuthorizationService(firstDb),
                preflight,
                new AuditWriter(firstDb));
            var secondService = new DeploymentService(
                secondDb,
                new WorkspaceAuthorizationService(secondDb),
                preflight,
                new AuditWriter(secondDb));

            var first = CaptureAsync(() => firstService.RequestAsync(
                fixture.UserId,
                fixture.StackId,
                request,
                Guid.NewGuid(),
                Guid.NewGuid(),
                CancellationToken.None));
            var second = CaptureAsync(() => secondService.RequestAsync(
                fixture.UserId,
                fixture.StackId,
                request,
                Guid.NewGuid(),
                Guid.NewGuid(),
                CancellationToken.None));
            await preflight.BothReached.Task.WaitAsync(TimeSpan.FromSeconds(5));
            preflight.Release.TrySetResult(true);
            var results = await Task.WhenAll(first, second);

            Assert.Single(results, value => value.Exception is null);
            var conflict = Assert.Single(results, value => value.Exception is not null).Exception;
            var requestException = Assert.IsType<DeploymentRequestException>(conflict);
            Assert.Equal("deployment_in_progress", requestException.Code);
            Assert.Equal(409, requestException.StatusCode);

            await using var verify = new StackPivotDbContext(options);
            Assert.Equal(1, await verify.ServiceOperationHistories.CountAsync(value => value.TaskStatus == "pending"));
        }
        finally
        {
            preflight.Release.TrySetResult(true);
            if (File.Exists(databasePath))
            {
                File.Delete(databasePath);
            }
        }
    }

    [Fact]
    public async Task ConcurrentContextsWithTheSameIdempotencyKeyReturnTheWinningRequest()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), "stackpivot-idempotency-" + Guid.NewGuid().ToString("N") + ".db");
        var connectionString = $"Data Source={databasePath};Default Timeout=5";
        var preflight = new WinnerVisiblePreflight();
        var request = new DeployStackRequest(
            "0123456789abcdef0123456789abcdef01234567",
            DeploymentMode.BoundAgents,
            null);
        var idempotencyKey = Guid.NewGuid();
        try
        {
            var options = new DbContextOptionsBuilder<StackPivotDbContext>()
                .UseSqlite(connectionString)
                .Options;
            DeploymentFixture fixture;
            await using (var seed = new StackPivotDbContext(options))
            {
                await seed.Database.EnsureCreatedAsync();
                fixture = await DeploymentFixture.SeedAsync(seed);
            }

            await using var firstDb = new StackPivotDbContext(options);
            await using var secondDb = new StackPivotDbContext(options);
            var firstService = new DeploymentService(
                firstDb,
                new WorkspaceAuthorizationService(firstDb),
                preflight,
                new AuditWriter(firstDb));
            var secondService = new DeploymentService(
                secondDb,
                new WorkspaceAuthorizationService(secondDb),
                preflight,
                new AuditWriter(secondDb));

            var first = CaptureAsync(() => firstService.RequestAsync(
                fixture.UserId,
                fixture.StackId,
                request,
                Guid.NewGuid(),
                idempotencyKey,
                CancellationToken.None));
            var second = CaptureAsync(() => secondService.RequestAsync(
                fixture.UserId,
                fixture.StackId,
                request,
                Guid.NewGuid(),
                idempotencyKey,
                CancellationToken.None));
            await preflight.FirstReached.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await preflight.SecondReached.Task.WaitAsync(TimeSpan.FromSeconds(5));
            preflight.ReleaseFirst.TrySetResult(true);
            await first;
            preflight.ReleaseSecond.TrySetResult(true);
            var results = await Task.WhenAll(first, second);

            Assert.All(results, value => Assert.Null(value.Exception));
            Assert.Equal(results[0].Result!.RequestId, results[1].Result!.RequestId);
            await using var verify = new StackPivotDbContext(options);
            Assert.Equal(1, await verify.DeploymentRequests.CountAsync());
            Assert.Equal(1, await verify.ServiceOperationHistories.CountAsync());
        }
        finally
        {
            preflight.ReleaseFirst.TrySetResult(true);
            preflight.ReleaseSecond.TrySetResult(true);
            if (File.Exists(databasePath))
            {
                File.Delete(databasePath);
            }
        }
    }

    private static async Task<CapturedResult> CaptureAsync(Func<Task<DeploymentRequestResult>> operation)
    {
        try
        {
            return new CapturedResult(await operation(), null);
        }
        catch (Exception exception)
        {
            return new CapturedResult(null, exception);
        }
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

    private sealed class SnapshotPreflight : ICentralGitPreflight
    {
        public Task<DeploymentPreflight> ValidateAsync(Guid stackId, string fullCommitHash, CancellationToken cancellationToken)
        {
            return Task.FromResult(new DeploymentPreflight(
                "https://snapshotted.example/repository.git",
                "snapshot-user",
                "workspace_snapshot/stack_snapshot",
                "/opt/agent-main/workspace_snapshot/stack_snapshot",
                "snapshot-key-v7"));
        }
    }

    private sealed class ConcurrentPreflight : ICentralGitPreflight
    {
        private int reached;

        public TaskCompletionSource<bool> BothReached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<DeploymentPreflight> ValidateAsync(
            Guid stackId,
            string fullCommitHash,
            CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref reached) == 2)
            {
                BothReached.TrySetResult(true);
            }

            await Release.Task.WaitAsync(cancellationToken);
            return new DeploymentPreflight(
                "https://git.example/repository.git",
                "git-user",
                "workspace_one/stack_web",
                "/opt/agent-main/workspace_one/stack_web",
                "git-key-v1");
        }
    }

    private sealed class WinnerVisiblePreflight : ICentralGitPreflight
    {
        private int reached;

        public TaskCompletionSource<bool> FirstReached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> SecondReached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> ReleaseFirst { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> ReleaseSecond { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<DeploymentPreflight> ValidateAsync(
            Guid stackId,
            string fullCommitHash,
            CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref reached) == 1)
            {
                FirstReached.TrySetResult(true);
                await SecondReached.Task.WaitAsync(cancellationToken);
                await ReleaseFirst.Task.WaitAsync(cancellationToken);
            }
            else
            {
                SecondReached.TrySetResult(true);
                await ReleaseSecond.Task.WaitAsync(cancellationToken);
            }

            return new DeploymentPreflight(
                "https://git.example/repository.git",
                "git-user",
                "workspace_one/stack_web",
                "/opt/agent-main/workspace_one/stack_web",
                "git-key-v1");
        }
    }

    private sealed record CapturedResult(DeploymentRequestResult? Result, Exception? Exception);

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
