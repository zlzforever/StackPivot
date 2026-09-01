using System.Text;
using System.Text.RegularExpressions;

namespace StackPivot.Agent.Execution;

public sealed partial class LogSanitizer(params string[] secrets)
{
    public const int MaxLineBytes = 16 * 1024;
    public const int MaxTaskBytes = 1024 * 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    public string Sanitize(string? value)
    {
        var result = value ?? string.Empty;
        foreach (var secret in secrets.Where(secret => !string.IsNullOrEmpty(secret)))
        {
            result = result.Replace(secret, "[REDACTED]", StringComparison.Ordinal);
        }

        result = AuthorizationPattern().Replace(result, "$1=[REDACTED]");
        result = KeyValuePattern().Replace(result, "$1=[REDACTED]");
        return TruncateUtf8(result, MaxLineBytes);
    }

    public SanitizedOutput SanitizeOutput(string? value)
    {
        var output = value ?? string.Empty;
        var builder = new StringBuilder();
        var currentBytes = 0;
        var truncated = false;
        foreach (var line in output.Split('\n', StringSplitOptions.None))
        {
            var sanitized = Sanitize(line);
            var lineBytes = Encoding.UTF8.GetBytes(sanitized);
            var separatorBytes = builder.Length == 0 ? 0 : 1;
            var remaining = MaxTaskBytes - currentBytes - separatorBytes;
            if (remaining <= 0)
            {
                truncated = true;
                break;
            }

            if (lineBytes.Length > remaining)
            {
                if (separatorBytes != 0)
                {
                    builder.Append('\n');
                }

                builder.Append(TruncateUtf8(sanitized, remaining));
                truncated = true;
                break;
            }

            if (separatorBytes != 0)
            {
                builder.Append('\n');
                currentBytes++;
            }

            builder.Append(sanitized);
            currentBytes += lineBytes.Length;
        }

        return new SanitizedOutput(builder.ToString(), truncated);
    }

    public static string TruncateUtf8(string value, int maxBytes)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (maxBytes <= 0)
        {
            return string.Empty;
        }

        var bytes = Encoding.UTF8.GetBytes(value);
        if (bytes.Length <= maxBytes)
        {
            return value;
        }

        var length = maxBytes;
        while (length > 0)
        {
            try
            {
                return StrictUtf8.GetString(bytes, 0, length);
            }
            catch (DecoderFallbackException)
            {
                length--;
            }
        }

        return string.Empty;
    }

    public sealed record SanitizedOutput(string Text, bool Truncated);

    [GeneratedRegex("(?i)(authorization|x-agent-api-key)\\s*:\\s*(?:Bearer\\s+)?[^\\s,;]+(?:\\s+[^\\s,;]+)?", RegexOptions.CultureInvariant)]
    private static partial Regex AuthorizationPattern();

    [GeneratedRegex("(?i)(password|passwd|pwd|secret|token|api[-_]?key|access[-_]?token|client[-_]?secret)\\s*[:=]\\s*[^\\s,;]+", RegexOptions.CultureInvariant)]
    private static partial Regex KeyValuePattern();
}
