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

    private sealed class NoOpClientProxy : IClientProxy
    {
        public Task SendCoreAsync(string method, object?[] args, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
