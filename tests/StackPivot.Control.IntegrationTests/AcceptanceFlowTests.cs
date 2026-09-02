using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using StackPivot.Control.Auth;
using StackPivot.Control.Authorization;
using StackPivot.Control.Domain.Entities;
using StackPivot.Control.Infrastructure.Git;
using StackPivot.Control.Infrastructure.Persistence;
using Xunit;

namespace StackPivot.Control.IntegrationTests;

public sealed class AcceptanceFlowTests(AcceptanceFlowFactory factory)
    : IClassFixture<AcceptanceFlowFactory>
{
    [Fact]
    public async Task HealthEndpointStartsWithoutAuthentication()
    {
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/health");

        Assert.True(response.IsSuccessStatusCode);
    }

    [Fact]
    public void OidcKeepsTheLiteralSubClaimForSsoMapping()
    {
        var options = factory.Services
            .GetRequiredService<IOptionsMonitor<OpenIdConnectOptions>>()
            .Get(SsoAuthenticationDefaults.Scheme);

        Assert.False(options.MapInboundClaims);
    }

    [Fact]
    public async Task ProtectedWriteWithoutAntiforgeryTokenReturnsStableBadRequest()
    {
        using var client = factory.CreateAuthenticatedClient();
        using var request = CreateDeploymentRequest(Guid.NewGuid(), Guid.NewGuid(), "0123456789abcdef0123456789abcdef01234567");

        using var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("\"code\":\"antiforgery_failed\"", body, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("not-a-uuid")]
    public async Task ProtectedWriteRejectsMissingOrMalformedRequestId(string? requestId)
    {
        using var client = factory.CreateAuthenticatedClient();
        using var request = CreateDeploymentRequest(Guid.NewGuid(), null, "0123456789abcdef0123456789abcdef01234567");
        if (requestId is not null)
        {
            request.Headers.Add("X-Request-Id", requestId);
        }

        factory.AddAntiforgeryHeaders(request);
        using var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        Assert.True(response.StatusCode == HttpStatusCode.UnprocessableEntity, body);
        Assert.Contains("\"code\":\"invalid_request\"", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FirstSsoRequestUpsertsSubjectAndValidWriteReturnsPendingTaskWithoutApiKey()
    {
        var fixture = await factory.SeedStackAsync();
        const string subject = "integration-first-sso-subject";
        using var client = factory.CreateAuthenticatedClient(subject);

        using var meResponse = await client.GetAsync("/api/me");
        var meBody = await meResponse.Content.ReadAsStringAsync();
        using var meDocument = JsonDocument.Parse(meBody);
        Assert.Equal(HttpStatusCode.OK, meResponse.StatusCode);
        Assert.Equal(subject, meDocument.RootElement.GetProperty("userName").GetString());

        var requestId = Guid.NewGuid();
        using var request = CreateDeploymentRequest(fixture.StackId, requestId, "0123456789abcdef0123456789abcdef01234567");
        factory.AddAntiforgeryHeaders(request, subject);
        using var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(body);

        Assert.True(response.StatusCode == HttpStatusCode.Accepted, body);
        Assert.Equal(requestId, document.RootElement.GetProperty("requestId").GetGuid());
        Assert.Equal(fixture.StackId, document.RootElement.GetProperty("stackId").GetGuid());
        var task = Assert.Single(document.RootElement.GetProperty("tasks").EnumerateArray().ToArray());
        Assert.Equal(fixture.AgentId, task.GetProperty("agentId").GetGuid());
        Assert.Equal("pending", task.GetProperty("status").GetString());
        Assert.DoesNotContain("apiKey", body, StringComparison.OrdinalIgnoreCase);

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<StackPivotDbContext>();
        var user = await db.Users.SingleAsync(value => value.SsoSubject == subject);
        Assert.Equal(subject, user.SsoSubject);
    }

    [Fact]
    public async Task DeploymentTargetResponseContainsNoApiKeyFields()
    {
        var fixture = await factory.SeedStackAsync();
        using var client = factory.CreateAuthenticatedClient();

        using var response = await client.GetAsync($"/api/stacks/{fixture.StackId}/deployment-targets");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(body);
        var target = Assert.Single(document.RootElement.EnumerateArray().ToArray());
        Assert.Equal(fixture.AgentId, target.GetProperty("agentId").GetGuid());
        Assert.Equal("agent", target.GetProperty("name").GetString());
        Assert.DoesNotContain("apiKey", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("git", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SingleAgentDeploymentCreatesOnlyTheSelectedBoundTarget()
    {
        var fixture = await factory.SeedStackWithAgentsAsync();
        using var client = factory.CreateAuthenticatedClient();
        using var request = CreateDeploymentRequest(
            fixture.StackId,
            Guid.NewGuid(),
            "0123456789abcdef0123456789abcdef01234567",
            "singleAgent",
            fixture.SecondBoundAgentId);
        factory.AddAntiforgeryHeaders(request);

        using var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(body);

        Assert.True(response.StatusCode == HttpStatusCode.Accepted, body);
        var task = Assert.Single(document.RootElement.GetProperty("tasks").EnumerateArray().ToArray());
        Assert.Equal(fixture.SecondBoundAgentId, task.GetProperty("agentId").GetGuid());
        Assert.Equal("pending", task.GetProperty("status").GetString());
    }

    [Fact]
    public async Task SingleAgentDeploymentRejectsAnUnboundTarget()
    {
        var fixture = await factory.SeedStackWithAgentsAsync();
        using var client = factory.CreateAuthenticatedClient();
        using var request = CreateDeploymentRequest(
            fixture.StackId,
            Guid.NewGuid(),
            "0123456789abcdef0123456789abcdef01234567",
            "singleAgent",
            fixture.UnboundAgentId);
        factory.AddAntiforgeryHeaders(request);

        using var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        Assert.True(response.StatusCode == HttpStatusCode.UnprocessableEntity, body);
        Assert.Contains("\"code\":\"invalid_target\"", body, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("not-a-commit", "invalid_commit")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "invalid_path")]
    [InlineData("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", "policy_violation")]
    public async Task DeploymentWriteMapsCommitPathAndRemoteValidationTo422(string commit, string expectedCode)
    {
        var fixture = await factory.SeedStackAsync();
        using var client = factory.CreateAuthenticatedClient();
        using var request = CreateDeploymentRequest(fixture.StackId, Guid.NewGuid(), commit);
        factory.AddAntiforgeryHeaders(request);

        using var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        Assert.True(response.StatusCode == HttpStatusCode.UnprocessableEntity, body);
        Assert.Contains($"\"code\":\"{expectedCode}\"", body, StringComparison.Ordinal);
    }

    private static HttpRequestMessage CreateDeploymentRequest(
        Guid stackId,
        Guid? requestId,
        string commit,
        string mode = "boundAgents",
        Guid? agentId = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/stacks/{stackId}/deployments")
        {
            Content = JsonContent.Create(new
            {
                targetCommitHash = commit,
                mode,
                agentId
            })
        };
        if (requestId is not null)
        {
            request.Headers.Add("X-Request-Id", requestId.Value.ToString());
        }

        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        return request;
    }
}

public sealed class AcceptanceFlowFactory : WebApplicationFactory<Program>
{
    private const string TestAuthenticationScheme = "integration-test-sso";
    private readonly string databasePath = Path.Combine(
        Path.GetTempPath(),
        "stackpivot-acceptance-" + Guid.NewGuid().ToString("N") + ".db");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("AgentApiKey:Pepper", Convert.ToBase64String(new byte[32]));
        builder.UseSetting("GitCredential:Key", Convert.ToBase64String(new byte[32]));
        builder.UseSetting("ConnectionStrings:StackPivot", $"Data Source={databasePath}");
        builder.UseSetting("Sso:Authority", "https://sso.example.test");
        builder.UseSetting("Sso:ClientId", "stackpivot-integration-tests");
        builder.UseSetting("Sso:ClientSecret", "integration-test-secret");
        builder.ConfigureTestServices(services =>
        {
            services.AddAuthentication()
                .AddScheme<AuthenticationSchemeOptions, TestAuthenticationHandler>(
                    TestAuthenticationScheme,
                    _ => { });
            services.AddAuthorization(options =>
            {
                options.AddPolicy("sso", policy =>
                {
                    policy.AddAuthenticationSchemes(TestAuthenticationScheme);
                    policy.RequireAuthenticatedUser();
                });
            });
            services.RemoveAll<ICentralGitPreflight>();
            services.AddScoped<ICentralGitPreflight, IntegrationPreflight>();
        });
    }

    public HttpClient CreateAuthenticatedClient(string subject = "integration-subject")
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-Subject", subject);
        return client;
    }

    public void AddAntiforgeryHeaders(HttpRequestMessage request, string subject = "integration-subject")
    {
        using var scope = Services.CreateScope();
        var antiforgery = scope.ServiceProvider.GetRequiredService<IAntiforgery>();
        var context = new DefaultHttpContext
        {
            RequestServices = scope.ServiceProvider,
            User = CreateTestPrincipal(subject)
        };
        var tokens = antiforgery.GetAndStoreTokens(context);
        request.Headers.Add("X-CSRF-TOKEN", tokens.RequestToken!);
        var cookie = context.Response.Headers.SetCookie.ToString()
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)[0];
        if (string.IsNullOrWhiteSpace(cookie))
        {
            throw new InvalidOperationException($"Antiforgery cookie was not generated. Request={tokens.RequestToken?.Length}, Cookie={tokens.CookieToken?.Length}.");
        }

        request.Headers.Add("Cookie", cookie);
    }

    public async Task<SeededStack> SeedStackAsync()
    {
        await using var scope = Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<StackPivotDbContext>();
        var workspace = new Workspace
        {
            WorkspaceId = Guid.NewGuid(),
            Name = "workspace_" + Guid.NewGuid().ToString("N")[..8],
            DisplayName = "Workspace"
        };
        var stack = new Stack
        {
            StackId = Guid.NewGuid(),
            WorkspaceId = workspace.WorkspaceId,
            FolderName = "stack_web",
            DisplayName = "Web"
        };
        var agent = new AgentNode
        {
            AgentId = Guid.NewGuid(),
            Name = "agent",
            ApiKeyHash = Guid.NewGuid().ToString("N"),
            ApiKeyVersion = 1,
            CapabilitiesJson = "[\"fullDeploy\"]"
        };
        db.AddRange(
            workspace,
            stack,
            agent,
            new StackAgentBinding
            {
                Id = Guid.NewGuid(),
                StackId = stack.StackId,
                AgentId = agent.AgentId
            });
        await db.SaveChangesAsync();
        return new SeededStack(stack.StackId, agent.AgentId);
    }

    public async Task<SeededTargets> SeedStackWithAgentsAsync()
    {
        await using var scope = Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<StackPivotDbContext>();
        var workspace = new Workspace
        {
            WorkspaceId = Guid.NewGuid(),
            Name = "workspace_" + Guid.NewGuid().ToString("N")[..8],
            DisplayName = "Workspace"
        };
        var stack = new Stack
        {
            StackId = Guid.NewGuid(),
            WorkspaceId = workspace.WorkspaceId,
            FolderName = "stack_web",
            DisplayName = "Web"
        };
        var boundAgent = new AgentNode
        {
            AgentId = Guid.NewGuid(),
            Name = "agent_one",
            ApiKeyHash = Guid.NewGuid().ToString("N"),
            ApiKeyVersion = 1,
            CapabilitiesJson = "[\"fullDeploy\"]"
        };
        var secondBoundAgent = new AgentNode
        {
            AgentId = Guid.NewGuid(),
            Name = "agent_two",
            ApiKeyHash = Guid.NewGuid().ToString("N"),
            ApiKeyVersion = 1,
            CapabilitiesJson = "[\"fullDeploy\"]"
        };
        var unboundAgent = new AgentNode
        {
            AgentId = Guid.NewGuid(),
            Name = "agent_unbound",
            ApiKeyHash = Guid.NewGuid().ToString("N"),
            ApiKeyVersion = 1,
            CapabilitiesJson = "[\"fullDeploy\"]"
        };
        db.AddRange(
            workspace,
            stack,
            boundAgent,
            secondBoundAgent,
            unboundAgent,
            new StackAgentBinding
            {
                Id = Guid.NewGuid(),
                StackId = stack.StackId,
                AgentId = boundAgent.AgentId
            },
            new StackAgentBinding
            {
                Id = Guid.NewGuid(),
                StackId = stack.StackId,
                AgentId = secondBoundAgent.AgentId
            });
        await db.SaveChangesAsync();
        return new SeededTargets(stack.StackId, boundAgent.AgentId, secondBoundAgent.AgentId, unboundAgent.AgentId);
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing && File.Exists(databasePath))
        {
            File.Delete(databasePath);
        }
    }

    private sealed class TestAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        Microsoft.Extensions.Logging.ILoggerFactory logger,
        System.Text.Encodings.Web.UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var subject = Request.Headers.TryGetValue("X-Test-Subject", out var value)
                && !string.IsNullOrWhiteSpace(value)
                ? value.ToString()
                : "integration-subject";
            return Task.FromResult(AuthenticateResult.Success(
                new AuthenticationTicket(CreateTestPrincipal(subject), Scheme.Name)));
        }
    }

    private static ClaimsPrincipal CreateTestPrincipal(string subject)
    {
        var identity = new ClaimsIdentity(
            new[]
            {
                new Claim("sub", subject),
                new Claim(ClaimTypes.Name, subject),
                new Claim(ClaimTypes.Role, "platform-admin")
            },
            SsoAuthenticationDefaults.Scheme);
        return new ClaimsPrincipal(identity);
    }

    private sealed class IntegrationPreflight : ICentralGitPreflight
    {
        public Task<DeploymentPreflight> ValidateAsync(
            Guid stackId,
            string fullCommitHash,
            CancellationToken cancellationToken)
        {
            if (fullCommitHash.StartsWith("aaaaaaaa", StringComparison.Ordinal))
            {
                throw new DeploymentValidationException("invalid_path", "Stack path is invalid.");
            }

            if (fullCommitHash.StartsWith("bbbbbbbb", StringComparison.Ordinal))
            {
                throw new DeploymentValidationException("policy_violation", "Git remote is not allowed.");
            }

            return Task.FromResult(new DeploymentPreflight(
                "https://git.example/repository.git",
                "git-user",
                "workspace_one/stack_web",
                "/opt/agent-main/workspace_one/stack_web",
                "git-key-v1"));
        }
    }

    public sealed record SeededStack(Guid StackId, Guid AgentId);
    public sealed record SeededTargets(Guid StackId, Guid FirstBoundAgentId, Guid SecondBoundAgentId, Guid UnboundAgentId);
}
