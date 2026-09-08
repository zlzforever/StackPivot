using System.Buffers;
using System.Text;
using Microsoft.AspNetCore.Http;

namespace StackPivot.Control.Api;

public sealed record RequestBodyReadResult(
    string? Text,
    bool TooLarge,
    bool InvalidUtf8);

public static class RequestBodyReader
{
    public const int MaxJsonBodyBytes = 256 * 1024;
    public const int MaxAgentBindingIds = 128;

    public static async Task<RequestBodyReadResult> ReadUtf8Async(
        HttpContext context,
        int maxBytes,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxBytes);

        if (context.Request.ContentLength is long contentLength && contentLength > maxBytes)
        {
            return new RequestBodyReadResult(null, true, false);
        }

        var buffer = ArrayPool<byte>.Shared.Rent(Math.Min(16 * 1024, maxBytes));
        try
        {
            var initialCapacity = context.Request.ContentLength is long declaredLength
                && declaredLength > 0
                && declaredLength <= maxBytes
                ? (int)declaredLength
                : 0;
            await using var body = new MemoryStream(capacity: initialCapacity);
            while (true)
            {
                var read = await context.Request.Body.ReadAsync(buffer.AsMemory(), cancellationToken);
                if (read == 0)
                {
                    break;
                }

                if (body.Length > maxBytes - read)
                {
                    return new RequestBodyReadResult(null, true, false);
                }

                await body.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            }

            try
            {
                return new RequestBodyReadResult(
                    new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true)
                        .GetString(body.GetBuffer(), 0, checked((int)body.Length)),
                    false,
                    false);
            }
            catch (DecoderFallbackException)
            {
                return new RequestBodyReadResult(null, false, true);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}
