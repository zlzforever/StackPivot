using Microsoft.AspNetCore.Http;

namespace StackPivot.Control.Api;

public static class ApiProblem
{
    public static IResult Create(
        HttpContext context,
        string code,
        int statusCode,
        string title,
        Guid? requestId = null)
    {
        var id = requestId ?? TryGetRequestId(context);
        var extensions = new Dictionary<string, object?>
        {
            ["code"] = code
        };
        if (id is not null)
        {
            extensions["requestId"] = id;
        }

        return Results.Problem(
            statusCode: statusCode,
            type: $"https://stackpivot/errors/{code}",
            title: title,
            extensions: extensions);
    }

    public static bool TryGetRequiredGuidHeader(HttpRequest request, string name, out Guid value)
    {
        value = Guid.Empty;
        return request.Headers.TryGetValue(name, out var header)
            && Guid.TryParse(header.ToString(), out value)
            && value != Guid.Empty;
    }

    public static Guid? TryGetRequestId(HttpContext context)
    {
        return TryGetRequiredGuidHeader(context.Request, "X-Request-Id", out var requestId)
            ? requestId
            : null;
    }
}
