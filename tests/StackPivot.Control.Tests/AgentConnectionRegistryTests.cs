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

    private sealed class NoOpClientProxy : IClientProxy
    {
        public Task SendCoreAsync(string method, object?[] args, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
