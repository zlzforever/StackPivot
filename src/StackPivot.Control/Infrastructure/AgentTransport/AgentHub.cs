using System.Globalization;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using StackPivot.Control.Application.Audit;
using StackPivot.Control.Application.Deployments;
using StackPivot.Control.Auth;
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
        var registrationVersion = registry.GetRegistrationVersion(identity);
        var agent = await dbContext.AgentNodes
            .SingleOrDefaultAsync(
                value => value.AgentId == identity
                    && value.ApiKeyVersion == apiKeyVersion
                    && value.RevokedAt == null,
                Context.ConnectionAborted);
        var accepted = agent is not null
            && hello.AgentId == identity
            && string.Equals(hello.Os, "linux", StringComparison.OrdinalIgnoreCase)
            && hello.ComposeMajorVersion == 2;
        if (accepted)
        {
            accepted = await registry.RegisterAsync(
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
                registrationVersion);
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
    }

    public async Task ReportTaskAccepted(TaskAccepted accepted)
    {
        await EnsureAgentAsync(accepted.AgentId);
        await dispatcher.HandleAcceptedAsync(accepted, Context.ConnectionAborted);
    }

    public async Task ReportTaskLog(TaskLog log)
    {
        await EnsureAgentAsync(log.AgentId);
        await dispatcher.HandleLogAsync(log, Context.ConnectionAborted);
    }

    public async Task ReportTaskCompleted(TaskCompleted completed)
    {
        await EnsureAgentAsync(completed.AgentId);
        await dispatcher.HandleCompletedAsync(completed, Context.ConnectionAborted);
    }

    public async Task Heartbeat(HeartbeatMessage heartbeat)
    {
        await EnsureAgentAsync(heartbeat.AgentId);
        await dispatcher.HandleHeartbeatAsync(heartbeat, Context.ConnectionAborted);
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        var removed = await registry.RemoveAsync(Context.ConnectionId);
        var identity = Context.User?.FindFirstValue("agent_id");
        if (removed && Guid.TryParse(identity, out var agentId))
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

        await base.OnDisconnectedAsync(exception);
    }

    private Guid GetAgentId()
    {
        var value = Context.User?.FindFirstValue("agent_id");
        return Guid.TryParse(value, out var agentId) && agentId != Guid.Empty
            ? agentId
            : throw new HubException("Agent identity is invalid.");
    }

    private async Task EnsureAgentAsync(Guid payloadAgentId)
    {
        var identity = GetAgentId();
        if (identity != payloadAgentId)
        {
            throw new HubException("Agent identity does not match payload.");
        }

        if (!registry.IsRegistered(identity, Context.ConnectionId, GetApiKeyVersion()))
        {
            throw new HubException("Agent connection is not registered.");
        }

        var apiKeyVersion = GetApiKeyVersion();
        var current = await dbContext.AgentNodes
            .AsNoTracking()
            .AnyAsync(
                value => value.AgentId == identity
                    && value.ApiKeyVersion == apiKeyVersion
                    && value.RevokedAt == null,
                Context.ConnectionAborted);
        if (!current || !registry.IsRegistered(identity, Context.ConnectionId, apiKeyVersion))
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
