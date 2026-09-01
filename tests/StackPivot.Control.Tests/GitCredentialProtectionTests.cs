using System.Security.Cryptography;
using System.Text;
using StackPivot.Control.Infrastructure.Security;
using Xunit;

namespace StackPivot.Control.Tests;

public sealed class GitCredentialProtectionTests
{
    [Fact]
    public void AesGcmRoundTripDoesNotStorePlaintext()
    {
        var key = Enumerable.Range(1, 32).Select(value => (byte)value).ToArray();
        var protector = new AesGcmGitCredentialProtector(key, "git-key-v1");

        var encrypted = protector.Protect("super-secret-token");
        var decrypted = protector.Unprotect(encrypted, "git-key-v1");

        Assert.NotEqual("super-secret-token", encrypted);
        Assert.DoesNotContain("super-secret-token", encrypted);
        Assert.Equal("super-secret-token", Encoding.UTF8.GetString(decrypted));
        CryptographicOperations.ZeroMemory(decrypted);
    }
}
