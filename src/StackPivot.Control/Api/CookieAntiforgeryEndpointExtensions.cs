using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace StackPivot.Control.Api;

public static class CookieAntiforgeryEndpointExtensions
{
    public static RouteHandlerBuilder RequireCookieAntiforgery(this RouteHandlerBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.AddEndpointFilter(async (context, next) =>
        {
            try
            {
                var antiforgery = context.HttpContext.RequestServices.GetRequiredService<IAntiforgery>();
                await antiforgery.ValidateRequestAsync(context.HttpContext);
                return await next(context);
            }
            catch (AntiforgeryValidationException)
            {
                return Results.Problem(
                    statusCode: StatusCodes.Status400BadRequest,
                    title: "Antiforgery validation failed.",
                    extensions: new Dictionary<string, object?> { ["code"] = "antiforgery_failed" });
            }
        });
    }
}
