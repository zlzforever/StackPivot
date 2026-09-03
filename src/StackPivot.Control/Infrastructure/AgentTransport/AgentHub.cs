using System.Globalization;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using StackPivot.Control.Application.Audit;
using StackPivot.Control.Application.Deployments;
using StackPivot.Control.Auth;
using StackPivot.Control.Domain.Entities;
using StackPivot.Control.Infrastructure.Persistence;
using StackPivot.Contracts.Agents;
using StackPivot.Contracts.SignalR;

namespace StackPivot.Control.Infrastructure.AgentTransport;

[Authorize(AuthenticationSchemes = AgentApiKeyDefaults.AuthenticationScheme)]
public sealed class AgentHub(
    StackPivotDbContext dbContext,
    AgentConnectionRegistry registry,
    DeploymentDispatcher dispatcher,
    AuditWriter auditWriter) : Hub
{
    public async Task RegisterAgent(AgentHello hello)
    {
        ProtocolValidation.EnsureSchemaVersion(hello.SchemaVersion);
        var identity = GetAgentId();
        var apiKeyVersion = GetApiKeyVersion();
        AgentConnection? previous = null;
        AgentNode? agent = null;
        var accepted = false;
        await using (var lease = await registry.AcquireAgentLockAsync(identity, Context.ConnectionAborted))
        {
            var registrationVersion = registry.GetRegistrationVersion(identity);
            agent = await dbContext.AgentNodes
                .SingleOrDefaultAsync(
                    value => value.AgentId == identity
                        && value.ApiKeyVersion == apiKeyVersion
                        && value.RevokedAt == null,
                    Context.ConnectionAborted);
            accepted = agent is not null
                && hello.AgentId == identity
                && string.Equals(hello.Os, "linux", StringComparison.OrdinalIgnoreCase)
                && hello.ComposeMajorVersion == 2;
            if (accepted)
            {
                agent!.LastSeenAt = DateTimeOffset.UtcNow;
                agent.CapabilitiesJson = System.Text.Json.JsonSerializer.Serialize(hello.Capabilities);
                auditWriter.Add(
                    AuditActions.AgentConnected,
                    null,
                    null,
                    identity,
                    "agent",
                    identity.ToString(),
                    "connected");
                await dbContext.SaveChangesAsync(Context.ConnectionAborted);
                accepted = registry.TryRegisterUnderLock(
                    new AgentConnection(
                        identity,
                        Context.ConnectionId,
                        Clients.Caller,
                        () =>
                        {
                            Context.Abort();
                            return Task.CompletedTask;
                        },
                        ApiKeyVersion: apiKeyVersion),
                    registrationVersion,
                    out previous);
            }
        }

        if (previous?.Abort is not null)
        {
            await previous.Abort();
        }

        var ack = new AgentHelloAck(
            ProtocolVersion.Current,
            accepted,
            DateTimeOffset.UtcNow,
            30,
            accepted ? null : "agent_registration_rejected");
        await Clients.Caller.SendAsync(AgentHubMethods.RegisterAgentAck, ack, Context.ConnectionAborted);
        if (!accepted)
        {
            Context.Abort();
            return;
        }
    }

    public async Task ReportTaskAccepted(TaskAccepted accepted)
    {
        var identity = GetAgentId();
        var apiKeyVersion = GetApiKeyVersion();
        await using var lease = await registry.AcquireAgentLockAsync(identity, Context.ConnectionAborted);
        await EnsureAgentAsync(accepted.AgentId, identity, apiKeyVersion);
        await dispatcher.HandleAcceptedAsync(accepted, Context.ConnectionAborted);
    }

    public async Task ReportTaskLog(TaskLog log)
    {
        var identity = GetAgentId();
        var apiKeyVersion = GetApiKeyVersion();
        await using var lease = await registry.AcquireAgentLockAsync(identity, Context.ConnectionAborted);
        await EnsureAgentAsync(log.AgentId, identity, apiKeyVersion);
        await dispatcher.HandleLogAsync(log, Context.ConnectionAborted);
    }

    public async Task ReportTaskCompleted(TaskCompleted completed)
    {
        var identity = GetAgentId();
        var apiKeyVersion = GetApiKeyVersion();
        await using var lease = await registry.AcquireAgentLockAsync(identity, Context.ConnectionAborted);
        await EnsureAgentAsync(completed.AgentId, identity, apiKeyVersion);
        await dispatcher.HandleCompletedAsync(completed, Context.ConnectionAborted);
    }

    public async Task Heartbeat(HeartbeatMessage heartbeat)
    {
        var identity = GetAgentId();
        var apiKeyVersion = GetApiKeyVersion();
        await using var lease = await registry.AcquireAgentLockAsync(identity, Context.ConnectionAborted);
        await EnsureAgentAsync(heartbeat.AgentId, identity, apiKeyVersion);
        await dispatcher.HandleHeartbeatAsync(heartbeat, Context.ConnectionAborted);
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        var identity = Context.User?.FindFirstValue("agent_id");
        if (Guid.TryParse(identity, out var agentId) && agentId != Guid.Empty)
        {
            await using var lease = await registry.AcquireAgentLockAsync(agentId, CancellationToken.None);
            if (registry.TryRemoveUnderLock(agentId, Context.ConnectionId, out _))
            {
                await dispatcher.HandleAgentDisconnectedAsync(agentId, CancellationToken.None);
                auditWriter.Add(
                    AuditActions.AgentDisconnected,
                    null,
                    null,
                    agentId,
                    "agent",
                    agentId.ToString(),
                    "disconnected");
                await dbContext.SaveChangesAsync(CancellationToken.None);
            }
        }

        await base.OnDisconnectedAsync(exception);
    }

    private Guid GetAgentId()
    {
        var value = Context.User?.FindFirstValue("agent_id");
        return Guid.TryParse(value, out var agentId) && agentId != Guid.Empty
            ? agentId
            : throw new HubException("Agent identity is invalid.");
    }

    private async Task EnsureAgentAsync(Guid payloadAgentId, Guid identity, int apiKeyVersion)
    {
        if (identity != payloadAgentId)
        {
            throw new HubException("Agent identity does not match payload.");
        }

        var registrationVersion = registry.GetRegistrationVersion(identity);
        if (!registry.IsRegistered(identity, Context.ConnectionId, apiKeyVersion, registrationVersion))
        {
            throw new HubException("Agent connection is not registered.");
        }

        var current = await dbContext.AgentNodes
            .AsNoTracking()
            .AnyAsync(
                value => value.AgentId == identity
                    && value.ApiKeyVersion == apiKeyVersion
                    && value.RevokedAt == null,
                Context.ConnectionAborted);
        if (!current || !registry.IsRegistered(identity, Context.ConnectionId, apiKeyVersion, registrationVersion))
        {
            throw new HubException("Agent credentials are no longer valid.");
        }
    }

    private int GetApiKeyVersion()
    {
        var value = Context.User?.FindFirstValue("agent_key_version");
        return int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var version) && version > 0
            ? version
            : throw new HubException("Agent key version is invalid.");
    }
}
