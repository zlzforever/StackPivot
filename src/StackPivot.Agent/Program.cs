using StackPivot.Agent;
using StackPivot.Agent.Connection;
using StackPivot.Agent.Execution;
using StackPivot.Agent.Security;

if (!OperatingSystem.IsLinux())
{
    throw new PlatformNotSupportedException("StackPivot Agent supports Linux only.");
}

var builder = Host.CreateApplicationBuilder(args);
var agentIdText = builder.Configuration["StackPivot:AgentId"]
    ?? throw new InvalidOperationException("StackPivot:AgentId is required.");
var controlHubUrl = builder.Configuration["StackPivot:ControlHubUrl"]
    ?? throw new InvalidOperationException("StackPivot:ControlHubUrl is required.");
var apiKey = builder.Configuration["StackPivot:ApiKey"]
    ?? throw new InvalidOperationException("StackPivot:ApiKey is required.");
if (!Guid.TryParse(agentIdText, out var agentId))
{
    throw new InvalidOperationException("StackPivot:AgentId must be a UUID.");
}

if (!Uri.TryCreate(controlHubUrl, UriKind.Absolute, out var hubUri)
    || !string.Equals(hubUri.Scheme, Uri.UriSchemeWss, StringComparison.OrdinalIgnoreCase))
{
    throw new InvalidOperationException("StackPivot:ControlHubUrl must use wss.");
}

var allowedRemoteHosts = (builder.Configuration["StackPivot:AllowedRemoteHosts"] ?? string.Empty)
    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
    .ToHashSet(StringComparer.OrdinalIgnoreCase);
var agentOptions = new AgentOptions(agentId, controlHubUrl, apiKey, "/opt/agent-main")
{
    AllowedRemoteHosts = allowedRemoteHosts
};
builder.Services.AddSingleton(agentOptions);
builder.Services.AddSingleton<PathPolicy>(services => new PathPolicy(services.GetRequiredService<AgentOptions>().AgentRoot));
builder.Services.AddSingleton<IProcessRunner, ProcessRunner>();
builder.Services.AddSingleton<GitCheckoutExecutor>(services =>
    new GitCheckoutExecutor(
        services.GetRequiredService<IProcessRunner>(),
        services.GetRequiredService<PathPolicy>(),
        TimeSpan.FromMinutes(15),
        services.GetRequiredService<AgentOptions>().AllowedRemoteHosts));
builder.Services.AddSingleton<ComposeExecutor>(services =>
    new ComposeExecutor(services.GetRequiredService<IProcessRunner>(), TimeSpan.FromMinutes(15)));
builder.Services.AddSingleton<IStackExecutor, StackExecutor>();
builder.Services.AddHostedService<AgentConnectionWorker>();

await builder.Build().RunAsync();
