using StackPivot.Agent.Execution;
using Xunit;

namespace StackPivot.Agent.Tests;

public sealed class LogSanitizerTests
{
    [Fact]
    public void SensitiveKeyValuesAreRedacted()
    {
        var sanitizer = new LogSanitizer();

        var line = sanitizer.Sanitize("Authorization: Bearer secret-token password=hunter2");

        Assert.DoesNotContain("secret-token", line);
        Assert.DoesNotContain("hunter2", line);
        Assert.Contains("[REDACTED]", line);
    }

    [Fact]
    public void ALineIsLimitedTo16Kibibytes()
    {
        var sanitizer = new LogSanitizer();

        var line = sanitizer.Sanitize(new string('x', 20 * 1024));

        Assert.True(System.Text.Encoding.UTF8.GetByteCount(line) <= LogSanitizer.MaxLineBytes);
    }

    [Fact]
    public void MultibyteLineIsTruncatedAtAValidUtf8Boundary()
    {
        var sanitizer = new LogSanitizer();

        var line = sanitizer.Sanitize(new string('\u754c', 20 * 1024));

        Assert.True(System.Text.Encoding.UTF8.GetByteCount(line) <= LogSanitizer.MaxLineBytes);
        Assert.DoesNotContain('\ufffd', line);
    }

    [Fact]
    public void CombinedOutputIsLimitedToOneMebibyte()
    {
        var sanitizer = new LogSanitizer();

        var output = sanitizer.SanitizeOutput(string.Join('\n', Enumerable.Repeat(new string('x', LogSanitizer.MaxLineBytes), 100)));

        Assert.True(output.Truncated);
        Assert.True(System.Text.Encoding.UTF8.GetByteCount(output.Text) <= LogSanitizer.MaxTaskBytes);
    }
}
