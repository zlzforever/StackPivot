using Microsoft.AspNetCore.Http;

namespace StackPivot.Control.Auth;

public sealed class MixedCredentialGuardMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (!AgentApiKeyDefaults.HasMixedCredentials(context.Request))
        {
            await next(context);
            return;
        }

        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        await context.Response.WriteAsJsonAsync(new { code = "mixed_credentials" });
    }
}
