using System.Security.Claims;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using StackPivot.Control.Application.Audit;
using StackPivot.Control.Application.Deployments;
using StackPivot.Control.Auth;
using StackPivot.Control.Domain.Entities;
using StackPivot.Control.Infrastructure.AgentTransport;
using StackPivot.Control.Infrastructure.Persistence;
using StackPivot.Control.Infrastructure.Security;
using StackPivot.Contracts.Agents;
using Xunit;

namespace StackPivot.Control.Tests;

public sealed class AgentHubTests
{
    [Fact]
    public async Task RevokedConnectionCannotReportHeartbeatBeforeRegistryCleanup()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<StackPivotDbContext>().UseSqlite(connection).Options;
        await using var db = new StackPivotDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var agentId = Guid.NewGuid();
        var lastSeenAt = DateTimeOffset.UtcNow.AddMinutes(-5);
        db.AgentNodes.Add(new AgentNode
        {
            AgentId = agentId,
            Name = "agent",
            ApiKeyHash = "hash",
            ApiKeyVersion = 3,
            LastSeenAt = lastSeenAt
        });
        await db.SaveChangesAsync();

        var registry = new AgentConnectionRegistry();
        await registry.RegisterAsync(new AgentConnection(
            agentId,
            "connection-1",
            new NoOpClientProxy(),
            ApiKeyVersion: 3));
        var protector = new AesGcmGitCredentialProtector(Enumerable.Range(1, 32).Select(value => (byte)value).ToArray());
        var hub = new AgentHub(
            db,
            registry,
            new DeploymentDispatcher(db, new NoOpTransport(), protector, new AuditWriter(db)),
            new AuditWriter(db))
        {
            Context = new TestHubCallerContext(
                "connection-1",
                new ClaimsPrincipal(new ClaimsIdentity(
                    new[]
                    {
                        new Claim("agent_id", agentId.ToString()),
                        new Claim("agent_key_version", "3")
                    },
                    AgentApiKeyDefaults.AuthenticationScheme)))
        };

        var agent = await db.AgentNodes.SingleAsync();
        agent.RevokedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();

        await Assert.ThrowsAsync<HubException>(() => hub.Heartbeat(
            new HeartbeatMessage(1, agentId, DateTimeOffset.UtcNow)));

        var persisted = await db.AgentNodes.AsNoTracking().SingleAsync();
        Assert.Equal(lastSeenAt, persisted.LastSeenAt);
    }

    private sealed class TestHubCallerContext : HubCallerContext
    {
        private readonly string connectionId;
        private readonly ClaimsPrincipal user;

        public TestHubCallerContext(string connectionId, ClaimsPrincipal user)
        {
            this.connectionId = connectionId;
            this.user = user;
        }

        public override string ConnectionId => connectionId;
        public override string? UserIdentifier => user.FindFirstValue(ClaimTypes.NameIdentifier);
        public override ClaimsPrincipal User => user;
        public override IDictionary<object, object?> Items { get; } = new Dictionary<object, object?>();
        public override IFeatureCollection Features { get; } = new FeatureCollection();
        public override CancellationToken ConnectionAborted => CancellationToken.None;
        public override void Abort() { }
    }

    private sealed class NoOpClientProxy : IClientProxy
    {
        public Task SendCoreAsync(string method, object?[] args, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class NoOpTransport : IAgentTransport
    {
        public Task<bool> IsConnectedAsync(Guid agentId, CancellationToken cancellationToken) => Task.FromResult(false);
        public Task SendDeployAsync(StackPivot.Contracts.Deployments.DeployStackCommand command, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
