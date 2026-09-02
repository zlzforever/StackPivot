using System.Text;
using Microsoft.Extensions.Configuration;
using StackPivot.Agent;
using StackPivot.Agent.Execution;
using Xunit;

namespace StackPivot.Agent.Tests;

public sealed class AgentHardeningTests
{
    private static readonly Guid AgentId = Guid.Parse("00000000-0000-0000-0000-000000000001");

    [Fact]
    public void ConfigurationCredentialPathIsIgnoredWithoutRuntimeEnvironmentVariable()
    {
        var configuredPath = CreateCredentialFile("configured-secret");
        try
        {
            WithRuntimeCredentialPath(null, () =>
            {
                var configuration = BuildConfiguration(
                    "wss://control.example/hubs/agent",
                    new Dictionary<string, string?>
                    {
                        ["StackPivot:ApiKeyFile"] = configuredPath,
                        ["StackPivot:ApiKey"] = "inline-secret"
                    });

                var exception = Assert.Throws<InvalidOperationException>(() => AgentOptions.FromConfiguration(configuration));

                Assert.DoesNotContain("configured-secret", exception.ToString(), StringComparison.Ordinal);
                Assert.DoesNotContain("inline-secret", exception.ToString(), StringComparison.Ordinal);
                Assert.Contains(AgentOptions.ApiKeyFileEnvironmentVariable, exception.Message, StringComparison.Ordinal);
            });
        }
        finally
        {
            DeleteCredentialFile(configuredPath);
        }
    }

    [Fact]
    public void RuntimeCredentialPathTakesPrecedenceOverConfigurationFallbacks()
    {
        var runtimePath = CreateCredentialFile("runtime-secret\n");
        var configuredPath = CreateCredentialFile("configured-secret\n");
        try
        {
            WithRuntimeCredentialPath(runtimePath, () =>
            {
                var configuration = BuildConfiguration(
                    "wss://control.example/hubs/agent",
                    new Dictionary<string, string?>
                    {
                        ["StackPivot:ApiKeyFile"] = configuredPath,
                        ["StackPivot:ApiKey"] = "inline-secret"
                    });

                var options = AgentOptions.FromConfiguration(configuration);

                Assert.Equal("runtime-secret", options.ApiKey);
            });
        }
        finally
        {
            DeleteCredentialFile(runtimePath);
            DeleteCredentialFile(configuredPath);
        }
    }

    [Theory]
    [InlineData("https://control.example/hubs/agent")]
    [InlineData("wss://control.example:444/hubs/agent")]
    [InlineData("wss://control.example/wrong")]
    [InlineData("wss://control.example/hubs/agent/")]
    [InlineData("wss://control.example/hubs/agent?mode=unsafe")]
    [InlineData("wss://control.example/hubs/agent#fragment")]
    [InlineData("wss://user:password@control.example/hubs/agent")]
    [InlineData("wss:///hubs/agent")]
    public void ControlHubUrlMustUseTheFixedSecureEndpoint(string hubUrl)
    {
        var credentialPath = CreateCredentialFile("runtime-secret");
        try
        {
            WithRuntimeCredentialPath(credentialPath, () =>
            {
                var exception = Assert.Throws<InvalidOperationException>(() =>
                    AgentOptions.FromConfiguration(BuildConfiguration(hubUrl)));

                Assert.DoesNotContain("runtime-secret", exception.ToString(), StringComparison.Ordinal);
            });
        }
        finally
        {
            DeleteCredentialFile(credentialPath);
        }
    }

    [Fact]
    public void ControlHubUrlMayUseTheExplicitHttpsPort()
    {
        var credentialPath = CreateCredentialFile("runtime-secret");
        try
        {
            WithRuntimeCredentialPath(credentialPath, () =>
            {
                var options = AgentOptions.FromConfiguration(
                    BuildConfiguration("wss://control.example:443/hubs/agent"));

                Assert.Equal("wss://control.example:443/hubs/agent", options.ControlHubUrl);
            });
        }
        finally
        {
            DeleteCredentialFile(credentialPath);
        }
    }

    [Fact]
    public void RemoteAllowlistMustNotTreatMissingConfigurationAsAnyHttpsHost()
    {
        const string remote = "https://untrusted.example/repository.git";

        Assert.False(CentralRemotePolicy.IsAllowed(remote, null));
        Assert.False(CentralRemotePolicy.IsAllowed(remote, new HashSet<string>(StringComparer.OrdinalIgnoreCase)));
    }

    [Fact]
    public void RemoteAllowlistMatchesOnlyConfiguredHttpsHosts()
    {
        var allowedHosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "git.example" };

        Assert.True(CentralRemotePolicy.IsAllowed("https://git.example/repository.git", allowedHosts));
        Assert.False(CentralRemotePolicy.IsAllowed("https://other.example/repository.git", allowedHosts));
        Assert.False(CentralRemotePolicy.IsAllowed("http://git.example/repository.git", allowedHosts));
        Assert.False(CentralRemotePolicy.IsAllowed("https://user:password@git.example/repository.git", allowedHosts));
    }

    [Fact]
    public void AgentAllowlistUsesAnExplicitNonSensitiveEnvironmentSetting()
    {
        var credentialPath = CreateCredentialFile("runtime-secret");
        try
        {
            WithRuntimeCredentialPath(credentialPath, () =>
            {
                var configuration = BuildConfiguration(
                    "wss://control.example/hubs/agent",
                    new Dictionary<string, string?>
                    {
                        ["STACKPIVOT_AGENT_ALLOWED_REMOTE_HOSTS"] = "git.example, mirror.example"
                    });

                var options = AgentOptions.FromConfiguration(configuration);

                Assert.Equal(
                    new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "git.example", "mirror.example" },
                    options.AllowedRemoteHosts);
            });
        }
        finally
        {
            DeleteCredentialFile(credentialPath);
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("runtime secret")]
    [InlineData("runtime-secret\nsecond-line")]
    public void InvalidCredentialContentFailsClosedWithoutEchoingTheSecret(string content)
    {
        var credentialPath = CreateCredentialFile(content);
        try
        {
            WithRuntimeCredentialPath(credentialPath, () =>
            {
                var exception = Assert.Throws<InvalidOperationException>(() =>
                    AgentOptions.FromConfiguration(BuildConfiguration("wss://control.example/hubs/agent")));

                if (!string.IsNullOrWhiteSpace(content))
                {
                    Assert.DoesNotContain(content, exception.ToString(), StringComparison.Ordinal);
                }
            });
        }
        finally
        {
            DeleteCredentialFile(credentialPath);
        }
    }

    [Fact]
    public void CredentialDirectoryFailsClosedWithoutEchoingThePathContents()
    {
        var directoryPath = Path.Combine(Path.GetTempPath(), "stackpivot-agent-credential-directory-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directoryPath);
        try
        {
            WithRuntimeCredentialPath(directoryPath, () =>
            {
                var exception = Assert.Throws<InvalidOperationException>(() =>
                    AgentOptions.FromConfiguration(BuildConfiguration("wss://control.example/hubs/agent")));

                Assert.DoesNotContain("runtime-secret", exception.ToString(), StringComparison.Ordinal);
            });
        }
        finally
        {
            Directory.Delete(directoryPath, recursive: true);
        }
    }

    [Fact]
    public void OversizedCredentialFailsClosed()
    {
        var credentialPath = CreateCredentialFile(new string('a', 4097));
        try
        {
            WithRuntimeCredentialPath(credentialPath, () => Assert.Throws<InvalidOperationException>(() =>
                AgentOptions.FromConfiguration(BuildConfiguration("wss://control.example/hubs/agent"))));
        }
        finally
        {
            DeleteCredentialFile(credentialPath);
        }
    }

    [Fact]
    public void InvalidUtf8CredentialFailsClosed()
    {
        var credentialPath = CreateCredentialFile(new byte[] { 0xc3, 0x28 });
        try
        {
            WithRuntimeCredentialPath(credentialPath, () => Assert.Throws<InvalidOperationException>(() =>
                AgentOptions.FromConfiguration(BuildConfiguration("wss://control.example/hubs/agent"))));
        }
        finally
        {
            DeleteCredentialFile(credentialPath);
        }
    }

    private static IConfiguration BuildConfiguration(
        string hubUrl,
        IReadOnlyDictionary<string, string?>? extraValues = null)
    {
        var values = new Dictionary<string, string?>
        {
            ["STACKPIVOT_AGENT_ID"] = AgentId.ToString(),
            ["STACKPIVOT_CONTROL_HUB_URL"] = hubUrl,
            ["STACKPIVOT_AGENT_WORK_ROOT"] = "/opt/agent-main"
        };
        if (extraValues is not null)
        {
            foreach (var pair in extraValues)
            {
                values[pair.Key] = pair.Value;
            }
        }

        return new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();
    }

    private static void WithRuntimeCredentialPath(string? path, Action action)
    {
        AgentTestEnvironment.WithRuntimeCredentialPath(path, action);
    }

    private static string CreateCredentialFile(string content)
    {
        var path = Path.Combine(Path.GetTempPath(), "stackpivot-agent-credential-" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(path, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return path;
    }

    private static string CreateCredentialFile(byte[] content)
    {
        var path = Path.Combine(Path.GetTempPath(), "stackpivot-agent-credential-" + Guid.NewGuid().ToString("N"));
        File.WriteAllBytes(path, content);
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
