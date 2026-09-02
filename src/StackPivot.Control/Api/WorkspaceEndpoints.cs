using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using StackPivot.Control.Auth;
using StackPivot.Control.Authorization;
using StackPivot.Control.Domain.Entities;
using StackPivot.Control.Infrastructure.AgentTransport;
using StackPivot.Control.Infrastructure.Persistence;
using StackPivot.Contracts.Agents;
using StackPivot.Contracts.Resources;

namespace StackPivot.Control.Api;

public static class WorkspaceEndpoints
{
    public static IEndpointRouteBuilder MapWorkspaceEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet(
                "/api/me",
                async (HttpContext context, ISsoIdentityAdapter adapter, IUserIdentityService users) =>
                {
                    try
                    {
                        var identity = adapter.Require(context);
                        var user = await users.UpsertFromSsoAsync(identity, context.RequestAborted);
                        return Results.Ok(new CurrentUserView(user.UserId, user.UserName, identity.Roles.Order(StringComparer.OrdinalIgnoreCase).ToArray()));
                    }
                    catch (UnauthorizedAccessException)
                    {
                        return ApiProblem.Create(context, "unauthenticated", 401, "Authentication is required.");
                    }
                })
            .RequireAuthorization("sso");

        endpoints.MapGet(
                "/api/workspaces",
                async (HttpContext context, StackPivotDbContext dbContext, ISsoIdentityAdapter adapter, IUserIdentityService users) =>
                {
                    try
                    {
                        var identity = adapter.Require(context);
                        var user = await users.UpsertFromSsoAsync(identity, context.RequestAborted);
                        var workspaces = await dbContext.Workspaces
                            .AsNoTracking()
                            .Where(workspace => user.IsPlatformAdmin
                                || workspace.Members.Any(member => member.UserId == user.UserId))
                            .OrderBy(workspace => workspace.Name)
                            .Select(workspace => new WorkspaceView(workspace.WorkspaceId, workspace.Name, workspace.DisplayName))
                            .ToListAsync(context.RequestAborted);
                        return Results.Ok(workspaces);
                    }
                    catch (UnauthorizedAccessException)
                    {
                        return ApiProblem.Create(context, "unauthenticated", 401, "Authentication is required.");
                    }
                })
            .RequireAuthorization("sso");

        endpoints.MapGet(
                "/api/workspaces/{workspaceId:guid}/stacks",
                async (Guid workspaceId, HttpContext context, StackPivotDbContext dbContext, WorkspaceAuthorizationService authorization, ISsoIdentityAdapter adapter, IUserIdentityService users) =>
                {
                    try
                    {
                        var identity = adapter.Require(context);
                        var user = await users.UpsertFromSsoAsync(identity, context.RequestAborted);
                        var access = await authorization.RequireAsync(user.UserId, workspaceId, WorkspacePermission.ReadOnly, context.RequestAborted);
                        if (!access.IsAllowed)
                        {
                            return ApiProblem.Create(context, "resource_not_found", 404, "Workspace was not found.");
                        }

                        var stacks = await dbContext.Stacks
                            .AsNoTracking()
                            .Where(stack => stack.WorkspaceId == workspaceId)
                            .OrderBy(stack => stack.FolderName)
                            .Select(stack => new StackView(stack.StackId, stack.WorkspaceId, stack.FolderName, stack.DisplayName))
                            .ToListAsync(context.RequestAborted);
                        return Results.Ok(stacks);
                    }
                    catch (UnauthorizedAccessException)
                    {
                        return ApiProblem.Create(context, "unauthenticated", 401, "Authentication is required.");
                    }
                })
            .RequireAuthorization("sso");

        endpoints.MapGet(
                "/api/stacks/{stackId:guid}/deployment-targets",
                async (Guid stackId, HttpContext context, StackPivotDbContext dbContext, WorkspaceAuthorizationService authorization, AgentConnectionRegistry registry, ISsoIdentityAdapter adapter, IUserIdentityService users) =>
                {
                    try
                    {
                        var identity = adapter.Require(context);
                        var user = await users.UpsertFromSsoAsync(identity, context.RequestAborted);
                        var stack = await dbContext.Stacks.AsNoTracking().SingleOrDefaultAsync(value => value.StackId == stackId, context.RequestAborted);
                        if (stack is null)
                        {
                            return ApiProblem.Create(context, "resource_not_found", 404, "Stack was not found.");
                        }

                        var access = await authorization.RequireAsync(user.UserId, stack.WorkspaceId, WorkspacePermission.ReadOnly, context.RequestAborted);
                        if (!access.IsAllowed)
                        {
                            return ApiProblem.Create(context, "resource_not_found", 404, "Stack was not found.");
                        }

                        var bindings = await dbContext.StackAgentBindings
                            .AsNoTracking()
                            .Include(value => value.Agent)
                            .Where(value => value.StackId == stackId && value.Agent != null && value.Agent.RevokedAt == null)
                            .ToListAsync(context.RequestAborted);
                        var result = new List<DeploymentTargetView>(bindings.Count);
                        foreach (var binding in bindings)
                        {
                            var agent = binding.Agent!;
                            var online = await registry.FindAsync(agent.AgentId, context.RequestAborted) is not null;
                            result.Add(new DeploymentTargetView(
                                agent.AgentId,
                                agent.Name,
                                online,
                                agent.LastSeenAt,
                                ParseCapabilities(agent.CapabilitiesJson)));
                        }

                        return Results.Ok(result);
                    }
                    catch (UnauthorizedAccessException)
                    {
                        return ApiProblem.Create(context, "unauthenticated", 401, "Authentication is required.");
                    }
                })
            .RequireAuthorization("sso");

        return endpoints;
    }

    private static string[] ParseCapabilities(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<string[]>(json) ?? Array.Empty<string>();
        }
        catch (JsonException)
        {
            return Array.Empty<string>();
        }
    }
}
