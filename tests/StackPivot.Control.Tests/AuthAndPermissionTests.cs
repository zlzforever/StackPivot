using System.Data.Common;
using System.Security.Claims;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using StackPivot.Control.Auth;
using StackPivot.Control.Authorization;
using StackPivot.Control.Domain.Entities;
using StackPivot.Control.Infrastructure.Persistence;
using StackPivot.Control.Application.Audit;
using Xunit;

namespace StackPivot.Control.Tests;

public sealed class AuthAndPermissionTests
{
    [Fact]
    public void AgentApiKeyManagerGeneratesAndRotatesNonReversibleKeys()
    {
        var manager = new AgentApiKeyManager(Encoding.UTF8.GetBytes("pepper-that-is-long-enough-for-tests"));
        var agentId = Guid.NewGuid();

        var first = manager.Issue(agentId);
        var rotated = manager.Issue(agentId, first.Version + 1);

        Assert.True(first.ApiKey.Length >= 43);
        Assert.NotEqual(first.ApiKey, rotated.ApiKey);
        Assert.NotEqual(first.ApiKey, first.ApiKeyHash);
        Assert.False(manager.Verify(agentId, first.ApiKey, rotated.ApiKeyHash, rotated.Version, null));
        Assert.True(manager.Verify(agentId, rotated.ApiKey, rotated.ApiKeyHash, rotated.Version, null));
        Assert.False(manager.Verify(agentId, rotated.ApiKey, rotated.ApiKeyHash, rotated.Version, DateTimeOffset.UtcNow));
    }

    [Fact]
    public void SsoAdapterRequiresSubAndMapsNameAndRoles()
    {
        var context = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                new[]
                {
                    new Claim("sub", "sso-subject"),
                    new Claim(ClaimTypes.Name, "alice"),
                    new Claim(ClaimTypes.Role, "platform-admin")
                },
                "sso"))
        };

        var identity = new HttpContextSsoIdentityAdapter().Require(context);

        Assert.Equal("sso-subject", identity.Subject);
        Assert.Equal("alice", identity.UserName);
        Assert.Contains("platform-admin", identity.Roles);
    }

    [Fact]
    public void SsoAdapterRejectsClaimsFromAnUnauthenticatedPrincipal()
    {
        var context = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim("sub", "untrusted") }))
        };

        Assert.Throws<UnauthorizedAccessException>(() => new HttpContextSsoIdentityAdapter().Require(context));
    }

    [Fact]
    public void SsoAdapterRejectsAnAuthenticatedPrincipalFromAnotherScheme()
    {
        var context = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                new[] { new Claim("sub", "local-subject") },
                "local-cookie"))
        };

        Assert.Throws<UnauthorizedAccessException>(() => new HttpContextSsoIdentityAdapter().Require(context));
    }

    [Fact]
    public async Task WorkspaceAuthorizationAllowsEditorAndHidesUnauthorizedResources()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<StackPivotDbContext>()
            .UseSqlite(connection)
            .Options;

        await using (var seed = new StackPivotDbContext(options))
        {
            await seed.Database.EnsureCreatedAsync();
            var editor = new UserAccount
            {
                UserId = Guid.NewGuid(),
                UserName = "editor",
                SsoSubject = "subject-editor"
            };
            var readOnly = new UserAccount
            {
                UserId = Guid.NewGuid(),
                UserName = "readonly",
                SsoSubject = "subject-readonly"
            };
            var workspace = new Workspace
            {
                WorkspaceId = Guid.NewGuid(),
                Name = "workspace_one",
                DisplayName = "Workspace One"
            };
            seed.AddRange(editor, readOnly, workspace);
            seed.AddRange(
                new WorkspaceMember
                {
                    Id = Guid.NewGuid(),
                    WorkspaceId = workspace.WorkspaceId,
                    UserId = editor.UserId,
                    Permission = WorkspacePermission.Editor
                },
                new WorkspaceMember
                {
                    Id = Guid.NewGuid(),
                    WorkspaceId = workspace.WorkspaceId,
                    UserId = readOnly.UserId,
                    Permission = WorkspacePermission.ReadOnly
                });
            await seed.SaveChangesAsync();

            var service = new WorkspaceAuthorizationService(seed);
            var editorResult = await service.RequireAsync(
                editor.UserId,
                workspace.WorkspaceId,
                WorkspacePermission.Editor,
                CancellationToken.None);
            var readOnlyResult = await service.RequireAsync(
                readOnly.UserId,
                workspace.WorkspaceId,
                WorkspacePermission.Editor,
                CancellationToken.None);
            var unknownResult = await service.RequireAsync(
                Guid.NewGuid(),
                workspace.WorkspaceId,
                WorkspacePermission.ReadOnly,
                CancellationToken.None);

            Assert.True(editorResult.IsAllowed);
            Assert.False(readOnlyResult.IsAllowed);
            Assert.True(readOnlyResult.ResourceNotFound);
            Assert.False(unknownResult.IsAllowed);
            Assert.True(unknownResult.ResourceNotFound);
        }
    }

    [Fact]
    public async Task ConcurrentKeyRotationsUseDistinctVersionsAndAudits()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<StackPivotDbContext>().UseSqlite(connection).Options;
        await using (var seed = new StackPivotDbContext(options))
        {
            await seed.Database.EnsureCreatedAsync();
            seed.AgentNodes.Add(new AgentNode
            {
                AgentId = Guid.Parse("00000000-0000-0000-0000-000000000010"),
                Name = "agent",
                ApiKeyHash = "initial-hash",
                ApiKeyVersion = 1
            });
            await seed.SaveChangesAsync();
        }

        await using var firstDb = new StackPivotDbContext(options);
        await using var secondDb = new StackPivotDbContext(options);
        var agentId = Guid.Parse("00000000-0000-0000-0000-000000000010");
        var actorId = Guid.NewGuid();
        var first = new AgentApiKeyService(firstDb, new AgentApiKeyManager(Encoding.UTF8.GetBytes("pepper-that-is-long-enough-for-tests")), new AuditWriter(firstDb));
        var second = new AgentApiKeyService(secondDb, new AgentApiKeyManager(Encoding.UTF8.GetBytes("pepper-that-is-long-enough-for-tests")), new AuditWriter(secondDb));

        var results = await Task.WhenAll(
            first.RotateKeyAsync(agentId, actorId, Guid.NewGuid(), CancellationToken.None),
            second.RotateKeyAsync(agentId, actorId, Guid.NewGuid(), CancellationToken.None));

        Assert.Equal(2, results.Select(value => value.Version).Distinct().Count());
        await using var verify = new StackPivotDbContext(options);
        Assert.Equal(3, (await verify.AgentNodes.SingleAsync()).ApiKeyVersion);
        Assert.Equal(2, await verify.AuditLogs.CountAsync(value => value.Action == AuditActions.AgentKeyRotated));
    }

    [Fact]
    public async Task RevokeRetriesAfterAConcurrentRotationAndKeepsBothAudits()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), "stackpivot-key-" + Guid.NewGuid().ToString("N") + ".db");
        var connectionString = $"Data Source={databasePath};Default Timeout=5";
        var barrier = new SaveChangesBarrier();
        var agentId = Guid.Parse("00000000-0000-0000-0000-000000000010");
        var actorId = Guid.NewGuid();
        try
        {
            var baseOptions = new DbContextOptionsBuilder<StackPivotDbContext>()
                .UseSqlite(connectionString)
                .Options;
            await using (var seed = new StackPivotDbContext(baseOptions))
            {
                await seed.Database.EnsureCreatedAsync();
                seed.AgentNodes.Add(new AgentNode
                {
                    AgentId = agentId,
                    Name = "agent",
                    ApiKeyHash = "initial-hash",
                    ApiKeyVersion = 1
                });
                await seed.SaveChangesAsync();
            }

            var rotateOptions = new DbContextOptionsBuilder<StackPivotDbContext>()
                .UseSqlite(connectionString)
                .AddInterceptors(barrier)
                .Options;
            await using var revokeDb = new StackPivotDbContext(baseOptions);
            await using var rotateDb = new StackPivotDbContext(rotateOptions);
            barrier.Target = rotateDb;
            barrier.Enabled = true;
            var revokeService = new AgentApiKeyService(
                revokeDb,
                new AgentApiKeyManager(Encoding.UTF8.GetBytes("pepper-that-is-long-enough-for-tests")),
                new AuditWriter(revokeDb));
            var rotateService = new AgentApiKeyService(
                rotateDb,
                new AgentApiKeyManager(Encoding.UTF8.GetBytes("pepper-that-is-long-enough-for-tests")),
                new AuditWriter(rotateDb));

            var rotate = rotateService.RotateKeyAsync(agentId, actorId, Guid.NewGuid(), CancellationToken.None);
            await barrier.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var revoke = revokeService.RevokeKeyAsync(agentId, actorId, Guid.NewGuid(), CancellationToken.None);
            barrier.Enabled = false;
            barrier.Release.TrySetResult(true);
            var rotated = await rotate;
            await revoke;

            await using var verify = new StackPivotDbContext(baseOptions);
            var agent = await verify.AgentNodes.SingleAsync();
            Assert.Equal(2, rotated.Version);
            Assert.Equal(2, agent.ApiKeyVersion);
            Assert.NotNull(agent.RevokedAt);
            Assert.Equal(1, await verify.AuditLogs.CountAsync(value => value.Action == AuditActions.AgentKeyRotated));
            Assert.Equal(1, await verify.AuditLogs.CountAsync(value => value.Action == AuditActions.AgentKeyRevoked));
        }
        finally
        {
            if (File.Exists(databasePath))
            {
                File.Delete(databasePath);
            }
        }
    }

    private sealed class SaveChangesBarrier : SaveChangesInterceptor
    {
        public TaskCompletionSource<bool> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public StackPivotDbContext? Target { get; set; }
        public bool Enabled { get; set; }

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (Enabled && ReferenceEquals(eventData.Context, Target))
            {
                Started.TrySetResult(true);
                await Release.Task.WaitAsync(cancellationToken);
            }

            return result;
        }
    }

    [Fact]
    public async Task AuthenticationLastSeenUpdateSurvivesConcurrentKeyRotation()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), "stackpivot-auth-" + Guid.NewGuid().ToString("N") + ".db");
        var connectionString = $"Data Source={databasePath};Default Timeout=5";
        var barrier = new LastSeenUpdateBarrier();
        var manager = new AgentApiKeyManager(Encoding.UTF8.GetBytes("pepper-that-is-long-enough-for-tests"));
        var agentId = Guid.Parse("00000000-0000-0000-0000-000000000011");
        try
        {
            var baseOptions = new DbContextOptionsBuilder<StackPivotDbContext>()
                .UseSqlite(connectionString)
                .Options;
            var authenticationOptions = new DbContextOptionsBuilder<StackPivotDbContext>()
                .UseSqlite(connectionString)
                .AddInterceptors(barrier)
                .Options;
            var issued = manager.Issue(agentId);
            await using (var seed = new StackPivotDbContext(baseOptions))
            {
                await seed.Database.EnsureCreatedAsync();
                seed.AgentNodes.Add(new AgentNode
                {
                    AgentId = agentId,
                    Name = "agent",
                    ApiKeyHash = issued.ApiKeyHash,
                    ApiKeyVersion = issued.Version,
                    ApiKeyLast4 = issued.ApiKeyLast4
                });
                await seed.SaveChangesAsync();
            }

            await using var authenticationDb = new StackPivotDbContext(authenticationOptions);
            await using var rotationDb = new StackPivotDbContext(baseOptions);
            barrier.Target = authenticationDb;
            barrier.Enabled = true;
            var authentication = new AgentApiKeyAuthenticationService(
                authenticationDb,
                new AgentApiKeyService(authenticationDb, manager));
            var rotation = new AgentApiKeyService(rotationDb, manager);
            var authenticationTask = authentication.AuthenticateAsync(issued.ApiKey, CancellationToken.None);
            await barrier.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));

            await rotation.RotateKeyAsync(agentId, CancellationToken.None);
            barrier.Enabled = false;
            barrier.Release.TrySetResult(true);

            var identity = await authenticationTask;
            Assert.NotNull(identity);
            Assert.Equal(agentId, identity!.AgentId);
            Assert.Equal(issued.Version, identity.ApiKeyVersion);
        }
        finally
        {
            if (File.Exists(databasePath))
            {
                File.Delete(databasePath);
            }
        }
    }

    private sealed class LastSeenUpdateBarrier : DbCommandInterceptor
    {
        public TaskCompletionSource<bool> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public StackPivotDbContext? Target { get; set; }
        public bool Enabled { get; set; }
        private int blocked;

        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (Enabled
                && ReferenceEquals(eventData.Context, Target)
                && command.CommandText.Contains("last_seen_at", StringComparison.OrdinalIgnoreCase)
                && Interlocked.Exchange(ref blocked, 1) == 0)
            {
                Started.TrySetResult(true);
                await Release.Task.WaitAsync(cancellationToken);
            }

            return result;
        }
    }
}
