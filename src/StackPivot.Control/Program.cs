using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using StackPivot.Control.Api;
using StackPivot.Control.Application.Audit;
using StackPivot.Control.Application.Deployments;
using StackPivot.Control.Auth;
using StackPivot.Control.Authorization;
using StackPivot.Control.Components;
using StackPivot.Control.Infrastructure.AgentTransport;
using StackPivot.Control.Infrastructure.Git;
using StackPivot.Control.Infrastructure.Persistence;
using StackPivot.Control.Infrastructure.Security;
using StackPivot.Contracts.SignalR;

var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("StackPivot")
    ?? "Data Source=stackpivot.db";
var pepperText = builder.Configuration["AgentApiKey:Pepper"]
    ?? Environment.GetEnvironmentVariable("STACKPIVOT_AGENT_API_PEPPER");
var pepper = RequiredSecretConfiguration.ReadBase64(pepperText, "AgentApiKey:Pepper", 32);

builder.Services.AddDbContext<StackPivotDbContext>(options => options.UseSqlite(connectionString));
builder.Services.AddSingleton(new AgentApiKeyManager(pepper));
CryptographicOperations.ZeroMemory(pepper);
builder.Services.AddScoped<AgentApiKeyService>();
builder.Services.AddScoped<AgentApiKeyAuthenticationService>();
builder.Services.AddScoped<ISsoIdentityAdapter, HttpContextSsoIdentityAdapter>();
builder.Services.AddScoped<IUserIdentityService, UserIdentityService>();
builder.Services.AddScoped<WorkspaceAuthorizationService>();
var allowedHosts = (builder.Configuration["CentralGit:AllowedRemoteHosts"] ?? string.Empty)
    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
    .ToHashSet(StringComparer.OrdinalIgnoreCase);
builder.Services.AddSingleton(new CentralGitOptions
{
    MainRoot = builder.Configuration["CentralGit:MainRoot"] ?? "/opt/main",
    AllowedRemoteHosts = allowedHosts,
    RejectSensitiveEnv = !bool.TryParse(builder.Configuration["CentralGit:AllowSensitiveEnv"], out var allowSensitiveEnv) || !allowSensitiveEnv
});
var gitKeyText = builder.Configuration["GitCredential:Key"]
    ?? Environment.GetEnvironmentVariable("STACKPIVOT_GIT_KEY");
var gitKey = RequiredSecretConfiguration.ReadBase64(gitKeyText, "GitCredential:Key", 32);
var gitKeyId = builder.Configuration["GitCredential:KeyId"] ?? "default";
var gitCredentialProtector = new AesGcmGitCredentialProtector(gitKey, gitKeyId);
CryptographicOperations.ZeroMemory(gitKey);
builder.Services.AddSingleton<IGitCredentialProtector>(gitCredentialProtector);
builder.Services.AddScoped<IGitCommandRunner, GitCommandRunner>();
builder.Services.AddScoped<ICentralGitPreflight, CentralGitPreflight>();
builder.Services.AddScoped<AuditWriter>();
builder.Services.AddScoped<DeploymentService>();
builder.Services.AddScoped<IDeploymentService>(services => services.GetRequiredService<DeploymentService>());
builder.Services.AddScoped<DeploymentDispatcher>();
builder.Services.AddSingleton<AgentConnectionRegistry>();
builder.Services.AddSingleton<IAgentTransport, SignalRAgentTransport>();
builder.Services.AddHostedService<DeploymentDispatchWorker>();
builder.Services.AddAuthentication(options =>
    {
        options.DefaultAuthenticateScheme = "sso";
        options.DefaultChallengeScheme = "sso";
    })
    .AddCookie("sso", options =>
    {
        options.Events.OnRedirectToLogin = context =>
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return Task.CompletedTask;
        };
        options.Events.OnRedirectToAccessDenied = context =>
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return Task.CompletedTask;
        };
    })
    .AddScheme<AuthenticationSchemeOptions, AgentApiKeyAuthenticationHandler>(
        AgentApiKeyDefaults.AuthenticationScheme,
        _ => { });
builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("sso", policy =>
    {
        policy.AddAuthenticationSchemes("sso");
        policy.RequireAuthenticatedUser();
    });
});
builder.Services.AddAntiforgery(options => options.HeaderName = "X-CSRF-TOKEN");
builder.Services.AddProblemDetails();
builder.Services.AddRazorComponents().AddInteractiveServerComponents();
builder.Services.AddCascadingAuthenticationState();
builder.Services.AddSignalR()
    .AddJsonProtocol(options =>
    {
        options.PayloadSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
        options.PayloadSerializerOptions.PropertyNameCaseInsensitive = false;
        options.PayloadSerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
        options.PayloadSerializerOptions.UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow;
        options.PayloadSerializerOptions.Converters.Add(new DeploymentModeJsonConverter());
    });

var app = builder.Build();
app.UseExceptionHandler();
app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();
app.MapHub<AgentHub>("/hubs/agent");
app.MapDeploymentEndpoints();
app.MapWorkspaceEndpoints();
app.MapAgentAdminEndpoints();
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

using (var scope = app.Services.CreateScope())
{
    var dbContext = scope.ServiceProvider.GetRequiredService<StackPivotDbContext>();
    dbContext.Database.Migrate();
}

app.Run();

public partial class Program;
