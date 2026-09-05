using Microsoft.AspNetCore.SignalR;
using StackPivot.Control.Infrastructure.AgentTransport;
using Xunit;

namespace StackPivot.Control.Tests;

public sealed class AgentConnectionRegistryTests
{
    [Fact]
    public async Task DisconnectingAnAgentRemovesAndAbortsItsConnection()
    {
        var registry = new AgentConnectionRegistry();
        var agentId = Guid.NewGuid();
        var aborted = false;
        await registry.RegisterAsync(new AgentConnection(
            agentId,
            "connection-1",
            new NoOpClientProxy(),
            () =>
            {
                aborted = true;
                return Task.CompletedTask;
            }));

        await registry.DisconnectAsync(agentId);

        Assert.True(aborted);
        Assert.Null(await registry.FindAsync(agentId, CancellationToken.None));
    }

    [Fact]
    public async Task OldConnectionRemovalCannotRemoveItsReplacement()
    {
        var registry = new AgentConnectionRegistry();
        var agentId = Guid.NewGuid();
        var oldAborted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        await registry.RegisterAsync(new AgentConnection(
            agentId,
            "connection-1",
            new NoOpClientProxy(),
            () =>
            {
                oldAborted.TrySetResult(true);
                return Task.CompletedTask;
            }));
        await registry.RegisterAsync(new AgentConnection(agentId, "connection-2", new NoOpClientProxy()));

        var removed = await registry.RemoveAsync("connection-1");

        Assert.False(removed);
        Assert.NotNull(await registry.FindAsync(agentId, CancellationToken.None));
        Assert.Equal("connection-2", (await registry.FindAsync(agentId, CancellationToken.None))!.ConnectionId);
        Assert.True(oldAborted.Task.IsCompleted);
    }

    [Fact]
    public async Task RegistrationUsingAnOldVersionCannotReturnAfterDisconnect()
    {
        var registry = new AgentConnectionRegistry();
        var agentId = Guid.NewGuid();
        var registrationVersion = registry.GetRegistrationVersion(agentId);

        await registry.DisconnectAsync(agentId);

        var registered = await registry.RegisterAsync(
            new AgentConnection(agentId, "old-connection", new NoOpClientProxy()),
            registrationVersion);

        Assert.False(registered);
        Assert.Null(await registry.FindAsync(agentId, CancellationToken.None));
    }

    [Fact]
    public async Task RegistrationUsingAnOldVersionCannotReturnAfterConnectionRemoval()
    {
        var registry = new AgentConnectionRegistry();
        var agentId = Guid.NewGuid();
        await registry.RegisterAsync(new AgentConnection(agentId, "connection-1", new NoOpClientProxy()));
        var registrationVersion = registry.GetRegistrationVersion(agentId);

        Assert.True(await registry.RemoveAsync("connection-1"));

        var registered = await registry.RegisterAsync(
            new AgentConnection(agentId, "stale-connection", new NoOpClientProxy()),
            registrationVersion);

        Assert.False(registered);
        Assert.Null(await registry.FindAsync(agentId, CancellationToken.None));
    }

    [Fact]
    public async Task ConcurrentReplacementInvalidationLeavesNoStaleConnection()
    {
        var registry = new AgentConnectionRegistry();
        var agentId = Guid.NewGuid();
        var oldAbort = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        await registry.RegisterAsync(new AgentConnection(
            agentId,
            "connection-1",
            new NoOpClientProxy(),
            () => oldAbort.Task));

        var registration = registry.RegisterAsync(
            new AgentConnection(agentId, "connection-2", new NoOpClientProxy()),
            registry.GetRegistrationVersion(agentId));
        await registry.DisconnectAsync(agentId);
        oldAbort.TrySetResult(true);
        Assert.True(await registration);

        Assert.Null(await registry.FindAsync(agentId, CancellationToken.None));
    }

    [Fact]
    public async Task StaleRegistrationCannotOverwriteAReplacement()
    {
        var registry = new AgentConnectionRegistry();
        var agentId = Guid.NewGuid();
        var replacementAbortStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseReplacementAbort = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        await registry.RegisterAsync(new AgentConnection(
            agentId,
            "connection-1",
            new NoOpClientProxy(),
            async () =>
            {
                replacementAbortStarted.TrySetResult(true);
                await releaseReplacementAbort.Task;
            }));

        var replacement = registry.RegisterAsync(
            new AgentConnection(agentId, "connection-2", new NoOpClientProxy()),
            registry.GetRegistrationVersion(agentId));
        await replacementAbortStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var staleRegistered = await registry.RegisterAsync(
            new AgentConnection(agentId, "stale-connection", new NoOpClientProxy()),
            registrationVersion: 0);

        Assert.False(staleRegistered);
        Assert.Equal("connection-2", (await registry.FindAsync(agentId, CancellationToken.None))!.ConnectionId);
        releaseReplacementAbort.TrySetResult(true);
        Assert.True(await replacement);
    }

    [Fact]
    public async Task RegistrationCapturedBeforeFirstConnectionCannotOverwriteIt()
    {
        var registry = new AgentConnectionRegistry();
        var agentId = Guid.NewGuid();
        var staleVersion = registry.GetRegistrationVersion(agentId);

        Assert.True(await registry.RegisterAsync(
            new AgentConnection(agentId, "connection-2", new NoOpClientProxy()),
            staleVersion));

        var staleRegistered = await registry.RegisterAsync(
            new AgentConnection(agentId, "stale-connection", new NoOpClientProxy()),
            staleVersion);

        Assert.False(staleRegistered);
        Assert.Equal("connection-2", (await registry.FindAsync(agentId, CancellationToken.None))!.ConnectionId);
    }

    [Fact]
    public async Task RegistrationIsBoundToItsApiKeyVersion()
    {
        var registry = new AgentConnectionRegistry();
        var agentId = Guid.NewGuid();
        await registry.RegisterAsync(new AgentConnection(
            agentId,
            "connection-1",
            new NoOpClientProxy(),
            ApiKeyVersion: 3));

        Assert.True(registry.IsRegistered(agentId, "connection-1", 3));
        Assert.False(registry.IsRegistered(agentId, "connection-1", 4));
    }

    [Fact]
    public async Task ReplacingAConnectionAdvancesItsRegistrationVersion()
    {
        var registry = new AgentConnectionRegistry();
        var agentId = Guid.NewGuid();

        await registry.RegisterAsync(new AgentConnection(
            agentId,
            "connection-1",
            new NoOpClientProxy(),
            ApiKeyVersion: 3));
        var firstVersion = registry.GetRegistrationVersion(agentId);

        Assert.True(await registry.RegisterAsync(
            new AgentConnection(agentId, "connection-2", new NoOpClientProxy(), ApiKeyVersion: 4),
            firstVersion));
        var secondVersion = registry.GetRegistrationVersion(agentId);

        Assert.True(secondVersion > firstVersion);
        Assert.False(registry.IsRegistered(agentId, "connection-1", 3, firstVersion));
        Assert.True(registry.IsRegistered(agentId, "connection-2", 4, secondVersion));
    }

    [Fact]
    public async Task RegistrationWaitsForAnExclusiveAgentMutation()
    {
        var registry = new AgentConnectionRegistry();
        var agentId = Guid.NewGuid();

        await using var lease = await registry.AcquireAgentLockAsync(agentId, CancellationToken.None);
        var registration = registry.RegisterAsync(
            new AgentConnection(agentId, "connection-1", new NoOpClientProxy(), ApiKeyVersion: 3));

        Assert.False(registration.IsCompleted);

        lease.Dispose();
        Assert.True(await registration);
        Assert.True(registry.IsRegistered(agentId, "connection-1", 3));
    }

    [Fact]
    public async Task FailedExclusiveKeyMutationStillRemovesAndAbortsTheConnection()
    {
        var registry = new AgentConnectionRegistry();
        var agentId = Guid.NewGuid();
        var aborted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        await registry.RegisterAsync(new AgentConnection(
            agentId,
            "connection-1",
            new NoOpClientProxy(),
            () =>
            {
                aborted.TrySetResult(true);
                return Task.CompletedTask;
            }));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            registry.ExecuteExclusiveAndDisconnectAsync(
                agentId,
                () => Task.FromException(new InvalidOperationException("database commit result is unknown")),
                CancellationToken.None));

        Assert.Equal("database commit result is unknown", exception.Message);
        Assert.True(aborted.Task.IsCompletedSuccessfully);
        Assert.Null(await registry.FindAsync(agentId, CancellationToken.None));
    }

    private sealed class NoOpClientProxy : IClientProxy
    {
        public Task SendCoreAsync(string method, object?[] args, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
