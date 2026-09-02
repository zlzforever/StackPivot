using StackPivot.Agent;
using StackPivot.Agent.Connection;
using StackPivot.Agent.Execution;
using StackPivot.Agent.Security;

if (!OperatingSystem.IsLinux())
{
    throw new PlatformNotSupportedException("StackPivot Agent supports Linux only.");
}

var builder = Host.CreateApplicationBuilder(args);
var agentOptions = AgentOptions.FromConfiguration(
    builder.Configuration,
    allowInlineApiKey: builder.Environment.IsDevelopment());
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
