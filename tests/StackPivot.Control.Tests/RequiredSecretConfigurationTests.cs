using StackPivot.Control.Infrastructure.Security;
using Xunit;

namespace StackPivot.Control.Tests;

public sealed class RequiredSecretConfigurationTests
{
    [Fact]
    public void MissingSecretIsRejectedInsteadOfGeneratingAnEphemeralValue()
    {
        var exception = Assert.Throws<InvalidOperationException>(() => RequiredSecretConfiguration.ReadBase64(null, "AgentApiKey:Pepper", 32));

        Assert.Contains("AgentApiKey:Pepper", exception.Message);
    }

    [Fact]
    public void SecretWithWrongLengthIsRejected()
    {
        var value = Convert.ToBase64String(new byte[31]);

        Assert.Throws<InvalidOperationException>(() => RequiredSecretConfiguration.ReadBase64(value, "GitCredential:Key", 32));
    }
}
