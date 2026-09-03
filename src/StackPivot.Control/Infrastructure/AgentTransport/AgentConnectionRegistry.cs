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
    int ApiKeyVersion = 0,
    long RegistrationVersion = 0);

public sealed class AgentConnectionLock : IDisposable, IAsyncDisposable
{
    private SemaphoreSlim? semaphore;

    internal AgentConnectionLock(SemaphoreSlim semaphore)
    {
        this.semaphore = semaphore;
    }

    public void Dispose()
    {
        Interlocked.Exchange(ref semaphore, null)?.Release();
        GC.SuppressFinalize(this);
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }
}

public sealed class AgentConnectionRegistry
{
    private readonly ConcurrentDictionary<Guid, AgentConnection> connections = new();
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> agentLocks = new();
    private readonly Dictionary<Guid, long> registrationVersions = new();
    private readonly object mutationLock = new();

    public async Task<AgentConnectionLock> AcquireAgentLockAsync(
        Guid agentId,
        CancellationToken cancellationToken)
    {
        if (agentId == Guid.Empty)
        {
            throw new ArgumentException("Agent id is required.", nameof(agentId));
        }

        var semaphore = agentLocks.GetOrAdd(agentId, static _ => new SemaphoreSlim(1, 1));
        await semaphore.WaitAsync(cancellationToken);
        return new AgentConnectionLock(semaphore);
    }

    public async Task ExecuteExclusiveAsync(
        Guid agentId,
        Func<Task> action,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(action);
        await ExecuteExclusiveAsync(
            agentId,
            async () =>
            {
                await action();
                return true;
            },
            cancellationToken);
    }

    public async Task<T> ExecuteExclusiveAsync<T>(
        Guid agentId,
        Func<Task<T>> action,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(action);
        await using var lease = await AcquireAgentLockAsync(agentId, cancellationToken);
        return await action();
    }

    public Task ExecuteExclusiveAndDisconnectAsync(
        Guid agentId,
        Func<Task> action,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(action);
        return ExecuteExclusiveAndDisconnectAsync(
            agentId,
            async () =>
            {
                await action();
                return true;
            },
            cancellationToken);
    }

    public async Task<T> ExecuteExclusiveAndDisconnectAsync<T>(
        Guid agentId,
        Func<Task<T>> action,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(action);
        AgentConnection? disconnected = null;
        try
        {
            return await ExecuteExclusiveAsync(
                agentId,
                async () =>
                {
                    try
                    {
                        return await action();
                    }
                    finally
                    {
                        disconnected = DisconnectHeld(agentId);
                    }
                },
                cancellationToken);
        }
        finally
        {
            if (disconnected?.Abort is not null)
            {
                await disconnected.Abort();
            }
        }
    }

    public long GetRegistrationVersion(Guid agentId)
    {
        lock (mutationLock)
        {
            return registrationVersions.TryGetValue(agentId, out var version) ? version : 0;
        }
    }

    public async Task<bool> RegisterAsync(AgentConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        AgentConnection? previous;
        bool registered;
        await using (var lease = await AcquireAgentLockAsync(connection.AgentId, CancellationToken.None))
        {
            var registrationVersion = GetRegistrationVersionUnderLock(connection.AgentId);
            registered = TryRegisterUnderLock(connection, registrationVersion, out previous);
        }

        if (previous?.Abort is not null)
        {
            await previous.Abort();
        }

        return registered;
    }

    public async Task<bool> RegisterAsync(AgentConnection connection, long registrationVersion)
    {
        ArgumentNullException.ThrowIfNull(connection);
        AgentConnection? previous;
        bool registered;
        await using (var lease = await AcquireAgentLockAsync(connection.AgentId, CancellationToken.None))
        {
            registered = TryRegisterUnderLock(connection, registrationVersion, out previous);
        }

        if (previous?.Abort is not null)
        {
            await previous.Abort();
        }

        return registered;
    }

    public Task<AgentConnection?> FindAsync(Guid agentId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        connections.TryGetValue(agentId, out var connection);
        return Task.FromResult(connection);
    }

    public bool IsRegistered(Guid agentId, string connectionId)
    {
        return FindUnderLock(agentId) is { } connection
            && string.Equals(connection.ConnectionId, connectionId, StringComparison.Ordinal);
    }

    public bool IsRegistered(Guid agentId, string connectionId, int apiKeyVersion)
    {
        return FindUnderLock(agentId) is { } connection
            && string.Equals(connection.ConnectionId, connectionId, StringComparison.Ordinal)
            && connection.ApiKeyVersion == apiKeyVersion;
    }

    public bool IsRegistered(
        Guid agentId,
        string connectionId,
        int apiKeyVersion,
        long registrationVersion)
    {
        lock (mutationLock)
        {
            return connections.TryGetValue(agentId, out var connection)
                && string.Equals(connection.ConnectionId, connectionId, StringComparison.Ordinal)
                && connection.ApiKeyVersion == apiKeyVersion
                && connection.RegistrationVersion == registrationVersion
                && GetRegistrationVersionUnderLock(agentId) == registrationVersion;
        }
    }

    public async Task<bool> RemoveAsync(string connectionId)
    {
        Guid? agentId = null;
        lock (mutationLock)
        {
            foreach (var pair in connections)
            {
                if (string.Equals(pair.Value.ConnectionId, connectionId, StringComparison.Ordinal))
                {
                    agentId = pair.Key;
                    break;
                }
            }
        }

        return agentId is not null && await RemoveAsync(agentId.Value, connectionId);
    }

    public async Task<bool> RemoveAsync(Guid agentId, string connectionId)
    {
        await using var lease = await AcquireAgentLockAsync(agentId, CancellationToken.None);
        return TryRemoveUnderLock(agentId, connectionId, out _);
    }

    public async Task DisconnectAsync(Guid agentId)
    {
        AgentConnection? connection;
        await using (var lease = await AcquireAgentLockAsync(agentId, CancellationToken.None))
        {
            connection = DisconnectHeld(agentId);
        }

        if (connection?.Abort is not null)
        {
            await connection.Abort();
        }
    }

    internal AgentConnection? FindUnderLock(Guid agentId)
    {
        lock (mutationLock)
        {
            connections.TryGetValue(agentId, out var connection);
            return connection;
        }
    }

    internal bool TryRegisterUnderLock(
        AgentConnection connection,
        long registrationVersion,
        out AgentConnection? previous)
    {
        previous = null;
        lock (mutationLock)
        {
            var currentVersion = GetRegistrationVersionUnderLock(connection.AgentId);
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

            var nextVersion = hasCurrent && !replacesCurrent
                ? currentVersion
                : checked(currentVersion + 1);
            registrationVersions[connection.AgentId] = nextVersion;
            connections[connection.AgentId] = connection with { RegistrationVersion = nextVersion };
            return true;
        }
    }

    internal AgentConnection? DisconnectHeld(Guid agentId)
    {
        lock (mutationLock)
        {
            var currentVersion = GetRegistrationVersionUnderLock(agentId);
            registrationVersions[agentId] = checked(currentVersion + 1);
            connections.TryRemove(agentId, out var connection);
            return connection;
        }
    }

    internal bool TryRemoveUnderLock(
        Guid agentId,
        string connectionId,
        out AgentConnection? removed)
    {
        lock (mutationLock)
        {
            if (!connections.TryGetValue(agentId, out var connection)
                || !string.Equals(connection.ConnectionId, connectionId, StringComparison.Ordinal))
            {
                removed = null;
                return false;
            }

            connections.TryRemove(agentId, out removed);
            registrationVersions[agentId] = checked(GetRegistrationVersionUnderLock(agentId) + 1);
            return true;
        }
    }

    private long GetRegistrationVersionUnderLock(Guid agentId) =>
        registrationVersions.TryGetValue(agentId, out var version) ? version : 0;
}

public sealed class SignalRAgentTransport(AgentConnectionRegistry registry) : IAgentTransport
{
    public async Task<bool> IsConnectedAsync(Guid agentId, CancellationToken cancellationToken)
    {
        await using var lease = await registry.AcquireAgentLockAsync(agentId, cancellationToken);
        return registry.FindUnderLock(agentId) is not null;
    }

    public async Task SendDeployAsync(DeployStackCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        await using var lease = await registry.AcquireAgentLockAsync(command.AgentId, cancellationToken);
        var connection = registry.FindUnderLock(command.AgentId)
            ?? throw new AgentOfflineException();
        await connection.Client.SendAsync(
            AgentHubMethods.DeployStack,
            command,
            cancellationToken);
    }
}
