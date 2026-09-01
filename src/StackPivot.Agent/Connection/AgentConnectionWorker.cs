using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.SignalR.Client;
using StackPivot.Agent;
using StackPivot.Agent.Execution;
using StackPivot.Contracts.Agents;
using StackPivot.Contracts.Deployments;
using StackPivot.Contracts.SignalR;

namespace StackPivot.Agent.Connection;

public sealed partial class AgentConnectionWorker : BackgroundService
{
    private readonly AgentOptions options;
    private readonly AgentTaskCoordinator taskCoordinator;
    private readonly ILogger<AgentConnectionWorker> logger;

    public AgentConnectionWorker(
        AgentOptions options,
        IStackExecutor executor,
        ILogger<AgentConnectionWorker> logger)
    {
        this.options = options;
        taskCoordinator = new AgentTaskCoordinator(options.AgentId, executor);
        this.logger = logger;
    }

    private static readonly TimeSpan[] RetryDelays =
    [
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(10),
        TimeSpan.FromSeconds(20),
        TimeSpan.FromSeconds(30)
    ];
    private static readonly string[] Capabilities = ["fullDeploy"];
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var retryIndex = 0;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunConnectionAsync(stoppingToken);
                retryIndex = 0;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                LogConnectionCycleEnded(exception.GetType().Name);
            }

            var delay = RetryDelays[Math.Min(retryIndex, RetryDelays.Length - 1)];
            retryIndex++;
            await Task.Delay(delay, stoppingToken);
        }
    }

    private async Task RunConnectionAsync(CancellationToken cancellationToken)
    {
        using var connectionCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        await using var connection = new HubConnectionBuilder()
            .WithUrl(options.ControlHubUrl, builder =>
            {
                builder.Headers["X-Agent-Api-Key"] = options.ApiKey;
            })
            .AddJsonProtocol(protocol =>
            {
                protocol.PayloadSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
                protocol.PayloadSerializerOptions.PropertyNameCaseInsensitive = false;
                protocol.PayloadSerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
                protocol.PayloadSerializerOptions.UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow;
                protocol.PayloadSerializerOptions.Converters.Add(new DeploymentModeJsonConverter());
            })
            .Build();
        connection.Closed += _ =>
        {
            connectionCancellation.Cancel();
            return Task.CompletedTask;
        };
        connection.KeepAliveInterval = TimeSpan.FromSeconds(20);
        connection.ServerTimeout = TimeSpan.FromSeconds(60);
        connection.On<DeployStackCommand>(
            AgentHubMethods.DeployStack,
            command => HandleDeployAsync(connection, command, connectionCancellation.Token));
        var helloAck = new TaskCompletionSource<AgentHelloAck>(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.On<AgentHelloAck>(AgentHubMethods.RegisterAgentAck, ack =>
        {
            try
            {
                ProtocolValidation.EnsureSchemaVersion(ack.SchemaVersion);
                helloAck.TrySetResult(ack);
            }
            catch (Exception exception)
            {
                helloAck.TrySetException(exception);
            }
            return Task.CompletedTask;
        });

        await connection.StartAsync(connectionCancellation.Token);
        var hello = new AgentHello(
            ProtocolVersion.Current,
            options.AgentId,
            "1.0.0",
            "linux",
            2,
            Capabilities,
            DateTimeOffset.UtcNow);
        await connection.SendAsync(AgentHubMethods.RegisterAgent, hello, connectionCancellation.Token);
        var ack = await helloAck.Task.WaitAsync(TimeSpan.FromSeconds(15), connectionCancellation.Token);
        if (!ack.Accepted)
        {
            throw new InvalidOperationException("Agent registration was rejected.");
        }

        using var heartbeatTimer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        while (await heartbeatTimer.WaitForNextTickAsync(connectionCancellation.Token))
        {
            if (connection.State != HubConnectionState.Connected)
            {
                return;
            }

            await connection.SendAsync(
                AgentHubMethods.Heartbeat,
                new HeartbeatMessage(ProtocolVersion.Current, options.AgentId, DateTimeOffset.UtcNow),
                connectionCancellation.Token);
        }
    }

    private async Task HandleDeployAsync(
        HubConnection connection,
        StackPivot.Contracts.Deployments.DeployStackCommand command,
        CancellationToken cancellationToken)
    {
        await taskCoordinator.HandleAsync(
            command,
            new SignalRTaskReporter(connection),
            cancellationToken);
    }

    private sealed class SignalRTaskReporter(HubConnection connection) : IAgentTaskReporter
    {
        public Task ReportAcceptedAsync(TaskAccepted accepted, CancellationToken cancellationToken) =>
            connection.SendAsync(AgentHubMethods.ReportTaskAccepted, accepted, cancellationToken);

        public Task ReportLogAsync(TaskLog log, CancellationToken cancellationToken) =>
            connection.SendAsync(AgentHubMethods.ReportTaskLog, log, cancellationToken);

        public Task ReportCompletedAsync(TaskCompleted completed, CancellationToken cancellationToken) =>
            connection.SendAsync(AgentHubMethods.ReportTaskCompleted, completed, cancellationToken);
    }

    [LoggerMessage(EventId = 1001, Level = LogLevel.Warning, Message = "Agent connection cycle ended: {ErrorType}")]
    private partial void LogConnectionCycleEnded(string errorType);
}
