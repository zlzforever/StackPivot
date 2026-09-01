using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Hosting;
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
}

public sealed class AcceptanceFlowFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("AgentApiKey:Pepper", Convert.ToBase64String(new byte[32]));
        builder.UseSetting("GitCredential:Key", Convert.ToBase64String(new byte[32]));
        builder.UseSetting("ConnectionStrings:StackPivot", "Data Source=acceptance-flow-test.db");
    }
}
