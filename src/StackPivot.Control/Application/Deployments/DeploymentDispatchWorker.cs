namespace StackPivot.Control.Application.Deployments;

public sealed partial class DeploymentDispatchWorker : BackgroundService
{
    private readonly IServiceScopeFactory scopeFactory;
    private readonly ILogger<DeploymentDispatchWorker> logger;

    public DeploymentDispatchWorker(
        IServiceScopeFactory scopeFactory,
        ILogger<DeploymentDispatchWorker> logger)
    {
        this.scopeFactory = scopeFactory;
        this.logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                await scope.ServiceProvider
                    .GetRequiredService<DeploymentDispatcher>()
                    .DispatchPendingAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                LogDispatchCycleFailed(exception.GetType().Name);
            }
        }
    }

    [LoggerMessage(EventId = 1002, Level = LogLevel.Error, Message = "Deployment dispatch cycle failed: {ErrorType}")]
    private partial void LogDispatchCycleFailed(string errorType);
}
