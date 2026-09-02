using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using StackPivot.Control.Auth;
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
}

public sealed class AcceptanceFlowFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("AgentApiKey:Pepper", Convert.ToBase64String(new byte[32]));
        builder.UseSetting("GitCredential:Key", Convert.ToBase64String(new byte[32]));
        builder.UseSetting("ConnectionStrings:StackPivot", "Data Source=acceptance-flow-test.db");
        builder.UseSetting("Sso:Authority", "https://sso.example.test");
        builder.UseSetting("Sso:ClientId", "stackpivot-integration-tests");
        builder.UseSetting("Sso:ClientSecret", "integration-test-secret");
    }
}
