using Microsoft.Extensions.Configuration;
using StackPivot.Agent;
using Xunit;

namespace StackPivot.Agent.Tests;

public sealed class AgentConfigurationTests
{
    private static readonly Guid AgentId = Guid.Parse("00000000-0000-0000-0000-000000000001");

    [Fact]
    public void UnitEnvironmentReadsCredentialFileBeforeInlineConfiguration()
    {
        var credentialPath = CreateCredentialFile("credential-api-key\n");
        try
        {
            AgentTestEnvironment.WithRuntimeCredentialPath(credentialPath, () =>
            {
                var configuration = BuildConfiguration(
                    new Dictionary<string, string?>
                    {
                        ["STACKPIVOT_AGENT_ID"] = AgentId.ToString(),
                        ["STACKPIVOT_CONTROL_HUB_URL"] = "wss://control.example/hubs/agent",
                        ["STACKPIVOT_AGENT_WORK_ROOT"] = "/opt/agent-main",
                        ["StackPivot:ApiKey"] = "inline-value-must-not-win"
                    });

                var options = AgentOptions.FromConfiguration(configuration);

                Assert.Equal(AgentId, options.AgentId);
                Assert.Equal("wss://control.example/hubs/agent", options.ControlHubUrl);
                Assert.Equal("/opt/agent-main", options.AgentRoot);
                Assert.Equal("credential-api-key", options.ApiKey);
                Assert.DoesNotContain("inline-value-must-not-win", options.ApiKey, StringComparison.Ordinal);
            });
        }
        finally
        {
            DeleteCredentialFile(credentialPath);
        }
    }

    [Fact]
    public void MissingCredentialFileFailsClosedWithoutEchoingTheConfiguredSecret()
    {
        const string secret = "inline-secret-must-not-appear";
        var missingPath = Path.Combine(Path.GetTempPath(), "stackpivot-agent-missing-" + Guid.NewGuid().ToString("N"));
        AgentTestEnvironment.WithRuntimeCredentialPath(null, () =>
        {
            var configuration = BuildConfiguration(
                new Dictionary<string, string?>
                {
                    ["STACKPIVOT_AGENT_ID"] = AgentId.ToString(),
                    ["STACKPIVOT_CONTROL_HUB_URL"] = "wss://control.example/hubs/agent",
                    ["StackPivot:ApiKeyFile"] = missingPath,
                    ["StackPivot:ApiKey"] = secret
                });

            var exception = Assert.Throws<InvalidOperationException>(() => AgentOptions.FromConfiguration(configuration));

            Assert.DoesNotContain(secret, exception.ToString(), StringComparison.Ordinal);
            Assert.Contains("STACKPIVOT_AGENT_API_KEY_FILE", exception.Message, StringComparison.Ordinal);
        });
    }

    [Theory]
    [InlineData("")]
    [InlineData("credential api key")]
    [InlineData("credential-api-key\nsecond-line")]
    public void EmptyOrMalformedCredentialFailsClosed(string contents)
    {
        var credentialPath = CreateCredentialFile(contents);
        try
        {
            AgentTestEnvironment.WithRuntimeCredentialPath(credentialPath, () =>
            {
                var configuration = BuildConfiguration(
                    new Dictionary<string, string?>
                    {
                        ["STACKPIVOT_AGENT_ID"] = AgentId.ToString(),
                        ["STACKPIVOT_CONTROL_HUB_URL"] = "wss://control.example/hubs/agent"
                    });

                var exception = Assert.Throws<InvalidOperationException>(() => AgentOptions.FromConfiguration(configuration));

                if (!string.IsNullOrEmpty(contents))
                {
                    Assert.DoesNotContain(contents, exception.ToString(), StringComparison.Ordinal);
                }
            });
        }
        finally
        {
            DeleteCredentialFile(credentialPath);
        }
    }

    [Fact]
    public void InlineApiKeyNeverEnablesAgent()
    {
        AgentTestEnvironment.WithRuntimeCredentialPath(null, () =>
        {
            var configuration = BuildConfiguration(
                new Dictionary<string, string?>
                {
                    ["STACKPIVOT_AGENT_ID"] = AgentId.ToString(),
                    ["STACKPIVOT_CONTROL_HUB_URL"] = "wss://control.example/hubs/agent",
                    ["StackPivot:ApiKey"] = "development-api-key"
                });

            var exception = Assert.Throws<InvalidOperationException>(() => AgentOptions.FromConfiguration(configuration));

            Assert.DoesNotContain("development-api-key", exception.ToString(), StringComparison.Ordinal);
            Assert.Contains("STACKPIVOT_AGENT_API_KEY_FILE", exception.Message, StringComparison.Ordinal);
        });
    }

    [Theory]
    [InlineData("wss://user:password@control.example/hubs/agent")]
    [InlineData("wss://@control.example/hubs/agent")]
    [InlineData("wss://control.example:8443/hubs/agent")]
    [InlineData("wss://control.example/hubs/agent?token=secret")]
    [InlineData("wss://control.example/hubs/agent#fragment")]
    [InlineData("wss://control.example/hubs/agent\n--header=secret")]
    public void ControlHubUrlRejectsAuthoritySuffixesAndUnsafeCharacters(string controlHubUrl)
    {
        var credentialPath = CreateCredentialFile("credential-api-key");
        try
        {
            AgentTestEnvironment.WithRuntimeCredentialPath(credentialPath, () =>
            {
                var configuration = BuildConfiguration(
                    new Dictionary<string, string?>
                    {
                        ["STACKPIVOT_AGENT_ID"] = AgentId.ToString(),
                        ["STACKPIVOT_CONTROL_HUB_URL"] = controlHubUrl,
                        ["STACKPIVOT_AGENT_WORK_ROOT"] = "/opt/agent-main"
                    });

                var exception = Assert.Throws<InvalidOperationException>(() => AgentOptions.FromConfiguration(configuration));

                Assert.DoesNotContain("credential-api-key", exception.ToString(), StringComparison.Ordinal);
                Assert.Contains("STACKPIVOT_CONTROL_HUB_URL", exception.Message, StringComparison.Ordinal);
            });
        }
        finally
        {
            DeleteCredentialFile(credentialPath);
        }
    }

    private static IConfiguration BuildConfiguration(IReadOnlyDictionary<string, string?> values) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();

    private static string CreateCredentialFile(string contents)
    {
        var path = Path.Combine(Path.GetTempPath(), "stackpivot-agent-credential-" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(path, contents);
        return path;
    }

    private static void DeleteCredentialFile(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }
}
