using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using StackPivot.Control.Auth;
using StackPivot.Control.Application.Deployments;
using StackPivot.Control.Infrastructure.Git;
using StackPivot.Contracts.Deployments;
using StackPivot.Contracts.SignalR;

namespace StackPivot.Control.Api;

public static class DeploymentEndpoints
{
    public static IEndpointRouteBuilder MapDeploymentEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost(
                "/api/stacks/{stackId:guid}/deployments",
                async (Guid stackId, HttpContext context, DeploymentService service, ISsoIdentityAdapter adapter, IUserIdentityService users) =>
                {
                    if (!ApiProblem.TryGetWriteRequestId(context, out var requestId)
                        || !ApiProblem.TryGetRequiredGuidHeader(context.Request, "Idempotency-Key", out var idempotencyKey))
                    {
                        return ApiProblem.Create(context, "invalid_request", 422, "Request UUID headers are required.");
                    }

                    DeployStackRequest request;
                    try
                    {
                        using var reader = new StreamReader(context.Request.Body);
                        request = ProtocolJson.Deserialize<DeployStackRequest>(await reader.ReadToEndAsync(context.RequestAborted));
                    }
                    catch (JsonException)
                    {
                        return ApiProblem.Create(context, "invalid_request", 422, "Request body is invalid.", requestId);
                    }

                    try
                    {
                        var identity = adapter.Require(context);
                        var user = await users.UpsertFromSsoAsync(identity, context.RequestAborted);
                        var result = await service.RequestAsync(
                            user.UserId,
                            stackId,
                            request,
                            requestId,
                            idempotencyKey,
                            context.RequestAborted);
                        return Results.Json(result, statusCode: StatusCodes.Status202Accepted);
                    }
                    catch (UnauthorizedAccessException)
                    {
                        return ApiProblem.Create(context, "unauthenticated", 401, "Authentication is required.", requestId);
                    }
                    catch (DeploymentRequestException exception)
                    {
                        return ApiProblem.Create(context, exception.Code, exception.StatusCode, exception.Message, requestId);
                    }
                    catch (DeploymentValidationException exception)
                    {
                        return ApiProblem.Create(context, exception.Code, exception.StatusCode, exception.Message, requestId);
                    }
                })
            .RequireAuthorization("sso")
            .RequireCookieAntiforgery();

        endpoints.MapGet(
                "/api/deployments/{requestId:guid}",
                async (Guid requestId, HttpContext context, IDeploymentService service, ISsoIdentityAdapter adapter, IUserIdentityService users) =>
                {
                    try
                    {
                        var identity = adapter.Require(context);
                        var user = await users.UpsertFromSsoAsync(identity, context.RequestAborted);
                        var result = await service.GetRequestAsync(user.UserId, requestId, context.RequestAborted);
                        return result is null
                            ? ApiProblem.Create(context, "resource_not_found", 404, "Deployment was not found.", requestId)
                            : Results.Ok(result);
                    }
                    catch (UnauthorizedAccessException)
                    {
                        return ApiProblem.Create(context, "unauthenticated", 401, "Authentication is required.", requestId);
                    }
                })
            .RequireAuthorization("sso");

        endpoints.MapGet(
                "/api/stacks/{stackId:guid}/operations",
                async (Guid stackId, HttpContext context, DeploymentService service, ISsoIdentityAdapter adapter, IUserIdentityService users, int? limit, string? cursor) =>
                {
                    try
                    {
                        var identity = adapter.Require(context);
                        var user = await users.UpsertFromSsoAsync(identity, context.RequestAborted);
                        var result = await service.GetOperationsPageAsync(user.UserId, stackId, limit ?? 50, cursor, context.RequestAborted);
                        return result is null
                            ? ApiProblem.Create(context, "resource_not_found", 404, "Stack was not found.")
                            : Results.Ok(result);
                    }
                    catch (DeploymentRequestException exception)
                    {
                        return ApiProblem.Create(context, exception.Code, exception.StatusCode, exception.Message);
                    }
                    catch (UnauthorizedAccessException)
                    {
                        return ApiProblem.Create(context, "unauthenticated", 401, "Authentication is required.");
                    }
                })
            .RequireAuthorization("sso");

        return endpoints;
    }
}
