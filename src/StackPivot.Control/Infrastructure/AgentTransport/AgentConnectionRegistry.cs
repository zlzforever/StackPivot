using System.Collections.Concurrent;
using Microsoft.AspNetCore.SignalR;
using StackPivot.Control.Application.Deployments;
using StackPivot.Contracts.Deployments;
using StackPivot.Contracts.SignalR;

namespace StackPivot.Control.Infrastructure.AgentTransport;

public sealed record AgentConnection(
    Guid AgentId,
    string ConnectionId,
    IClientProxy Client,
    Func<Task>? Abort = null,
    int ApiKeyVersion = 0);

public sealed class AgentConnectionRegistry
{
    private readonly ConcurrentDictionary<Guid, AgentConnection> connections = new();
    private readonly Dictionary<Guid, long> registrationVersions = new();
    private readonly object mutationLock = new();

    public long GetRegistrationVersion(Guid agentId)
    {
        lock (mutationLock)
        {
            return registrationVersions.TryGetValue(agentId, out var version) ? version : 0;
        }
    }

    public async Task RegisterAsync(AgentConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        await RegisterAsync(connection, GetRegistrationVersion(connection.AgentId));
    }

    public async Task<bool> RegisterAsync(AgentConnection connection, long registrationVersion)
    {
        ArgumentNullException.ThrowIfNull(connection);
        AgentConnection? previous = null;
        lock (mutationLock)
        {
            var currentVersion = registrationVersions.TryGetValue(connection.AgentId, out var version)
                ? version
                : 0;
            if (registrationVersion != currentVersion)
            {
                return false;
            }

            var hasCurrent = connections.TryGetValue(connection.AgentId, out var current);
            var replacesCurrent = current is not null
                && (!string.Equals(current.ConnectionId, connection.ConnectionId, StringComparison.Ordinal)
                    || current.ApiKeyVersion != connection.ApiKeyVersion);
            if (replacesCurrent)
            {
                previous = current;
            }

            if (!hasCurrent || replacesCurrent)
            {
                registrationVersions[connection.AgentId] = checked(currentVersion + 1);
            }

            connections[connection.AgentId] = connection;
        }

        if (previous?.Abort is not null)
        {
            await previous.Abort();
        }

        return true;
    }

    public Task<AgentConnection?> FindAsync(Guid agentId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        connections.TryGetValue(agentId, out var connection);
        return Task.FromResult(connection);
    }

    public bool IsRegistered(Guid agentId, string connectionId)
    {
        return connections.TryGetValue(agentId, out var connection)
            && string.Equals(connection.ConnectionId, connectionId, StringComparison.Ordinal);
    }

    public bool IsRegistered(Guid agentId, string connectionId, int apiKeyVersion)
    {
        return connections.TryGetValue(agentId, out var connection)
            && string.Equals(connection.ConnectionId, connectionId, StringComparison.Ordinal)
            && connection.ApiKeyVersion == apiKeyVersion;
    }

    public async Task<bool> RemoveAsync(string connectionId)
    {
        lock (mutationLock)
        {
            foreach (var pair in connections)
            {
                if (string.Equals(pair.Value.ConnectionId, connectionId, StringComparison.Ordinal)
                    && connections.TryRemove(pair.Key, out _))
                {
                    var currentVersion = registrationVersions.TryGetValue(pair.Key, out var version)
                        ? version
                        : 0;
                    registrationVersions[pair.Key] = checked(currentVersion + 1);
                    return true;
                }
            }

            return false;
        }
    }

    public async Task DisconnectAsync(Guid agentId)
    {
        AgentConnection? connection = null;
        lock (mutationLock)
        {
            var currentVersion = registrationVersions.TryGetValue(agentId, out var version)
                ? version
                : 0;
            registrationVersions[agentId] = checked(currentVersion + 1);
            connections.TryRemove(agentId, out connection);
        }

        if (connection?.Abort is not null)
        {
            await connection.Abort();
        }
    }
}

public sealed class SignalRAgentTransport(AgentConnectionRegistry registry) : IAgentTransport
{
    public async Task<bool> IsConnectedAsync(Guid agentId, CancellationToken cancellationToken)
    {
        return await registry.FindAsync(agentId, cancellationToken) is not null;
    }

    public async Task SendDeployAsync(DeployStackCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var connection = await registry.FindAsync(command.AgentId, cancellationToken)
            ?? throw new InvalidOperationException("Agent is offline.");
        await connection.Client.SendAsync(
            AgentHubMethods.DeployStack,
            command,
            cancellationToken);
    }
}
