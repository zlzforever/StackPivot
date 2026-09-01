using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using StackPivot.Control.Application.Audit;
using StackPivot.Control.Auth;
using StackPivot.Control.Domain.Entities;
using StackPivot.Control.Infrastructure.AgentTransport;
using StackPivot.Control.Infrastructure.Persistence;
using StackPivot.Contracts.Resources;

namespace StackPivot.Control.Api;

public static class AgentAdminEndpoints
{
    private static readonly JsonSerializerOptions RequestJsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public static IEndpointRouteBuilder MapAgentAdminEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet(
                "/api/agent-nodes",
                async (HttpContext context, StackPivotDbContext dbContext, IUserIdentityService users, ISsoIdentityAdapter adapter) =>
                {
                    var admin = await RequireAdminAsync(context, dbContext, users, adapter);
                    if (admin is not null)
                    {
                        return admin;
                    }

                    var agents = await dbContext.AgentNodes
                        .AsNoTracking()
                        .OrderBy(value => value.Name)
                        .Select(value => new
                        {
                            value.AgentId,
                            value.Name,
                            value.RevokedAt,
                            value.LastSeenAt,
                            value.ApiKeyLast4,
                            value.ApiKeyVersion,
                            value.CapabilitiesJson
                        })
                        .ToListAsync(context.RequestAborted);
                    return Results.Ok(agents.Select(value => new
                    {
                        agentId = value.AgentId,
                        name = value.Name,
                        online = value.RevokedAt is null && value.LastSeenAt is not null && value.LastSeenAt >= DateTimeOffset.UtcNow.AddMinutes(-2),
                        lastSeenAt = value.LastSeenAt,
                        apiKeyLast4 = value.ApiKeyLast4,
                        apiKeyVersion = value.ApiKeyVersion,
                        capabilities = ParseCapabilities(value.CapabilitiesJson)
                    }));
                })
            .RequireAuthorization("sso");

        endpoints.MapPost(
                "/api/agent-nodes",
                async (HttpContext context, StackPivotDbContext dbContext, AgentApiKeyService keyService, AuditWriter auditWriter, IUserIdentityService users, ISsoIdentityAdapter adapter) =>
                {
                    var admin = await RequireAdminAsync(context, dbContext, users, adapter);
                    if (admin is not null)
                    {
                        return admin;
                    }

                    AgentCreateRequest request;
                    try
                    {
                        using var reader = new StreamReader(context.Request.Body);
                        request = JsonSerializer.Deserialize<AgentCreateRequest>(await reader.ReadToEndAsync(context.RequestAborted), RequestJsonOptions)
                            ?? throw new JsonException();
                    }
                    catch (JsonException)
                    {
                        return ApiProblem.Create(context, "invalid_request", 422, "Agent request is invalid.");
                    }

                    var name = request.Name?.Trim();
                    if (string.IsNullOrWhiteSpace(name) || name.Length > 100 || name.Any(char.IsControl))
                    {
                        return ApiProblem.Create(context, "invalid_request", 422, "Agent name is invalid.");
                    }

                    if (request.Remark is { Length: > 500 } || request.Remark?.Any(char.IsControl) == true)
                    {
                        return ApiProblem.Create(context, "invalid_request", 422, "Agent remark is invalid.");
                    }

                    var identity = adapter.Require(context);
                    var user = await users.UpsertFromSsoAsync(identity, context.RequestAborted);
                    var agent = new AgentNode
                    {
                        AgentId = Guid.NewGuid(),
                        Name = name,
                        Remark = request.Remark?.Trim() ?? string.Empty,
                        CapabilitiesJson = "[]"
                    };
                    dbContext.AgentNodes.Add(agent);
                    await dbContext.SaveChangesAsync(context.RequestAborted);
                    var issue = await keyService.IssueAgentKeyAsync(agent.AgentId, context.RequestAborted);
                    auditWriter.Add(AuditActions.AgentKeyCreated, null, user.UserId, agent.AgentId, "agent", agent.AgentId.ToString(), "success");
                    await dbContext.SaveChangesAsync(context.RequestAborted);
                    return Results.Json(new AgentKeyIssueView(agent.AgentId, issue.ApiKey, issue.Version, issue.ApiKeyLast4), statusCode: 201);
                })
            .RequireAuthorization("sso")
            .WithMetadata(new RequireAntiforgeryTokenAttribute(true));

        endpoints.MapPost(
                "/api/agent-nodes/{agentId:guid}/rotate-key",
                async (Guid agentId, HttpContext context, StackPivotDbContext dbContext, AgentApiKeyService keyService, AgentConnectionRegistry registry, AuditWriter auditWriter, IUserIdentityService users, ISsoIdentityAdapter adapter) =>
                {
                    var admin = await RequireAdminAsync(context, dbContext, users, adapter);
                    if (admin is not null)
                    {
                        return admin;
                    }

                    var identity = adapter.Require(context);
                    var user = await users.UpsertFromSsoAsync(identity, context.RequestAborted);
                    try
                    {
                        var issue = await keyService.RotateKeyAsync(agentId, context.RequestAborted);
                        await registry.DisconnectAsync(agentId);
                        auditWriter.Add(AuditActions.AgentKeyRotated, null, user.UserId, agentId, "agent", agentId.ToString(), "success");
                        await dbContext.SaveChangesAsync(context.RequestAborted);
                        return Results.Ok(new AgentKeyIssueView(agentId, issue.ApiKey, issue.Version, issue.ApiKeyLast4));
                    }
                    catch (KeyNotFoundException)
                    {
                        return ApiProblem.Create(context, "resource_not_found", 404, "Agent was not found.");
                    }
                })
            .RequireAuthorization("sso")
            .WithMetadata(new RequireAntiforgeryTokenAttribute(true));

        endpoints.MapPost(
                "/api/agent-nodes/{agentId:guid}/revoke-key",
                async (Guid agentId, HttpContext context, StackPivotDbContext dbContext, AgentApiKeyService keyService, AgentConnectionRegistry registry, AuditWriter auditWriter, IUserIdentityService users, ISsoIdentityAdapter adapter) =>
                {
                    var admin = await RequireAdminAsync(context, dbContext, users, adapter);
                    if (admin is not null)
                    {
                        return admin;
                    }

                    var identity = adapter.Require(context);
                    var user = await users.UpsertFromSsoAsync(identity, context.RequestAborted);
                    try
                    {
                        await keyService.RevokeKeyAsync(agentId, context.RequestAborted);
                        await registry.DisconnectAsync(agentId);
                        auditWriter.Add(AuditActions.AgentKeyRevoked, null, user.UserId, agentId, "agent", agentId.ToString(), "success");
                        await dbContext.SaveChangesAsync(context.RequestAborted);
                        return Results.NoContent();
                    }
                    catch (KeyNotFoundException)
                    {
                        return ApiProblem.Create(context, "resource_not_found", 404, "Agent was not found.");
                    }
                })
            .RequireAuthorization("sso")
            .WithMetadata(new RequireAntiforgeryTokenAttribute(true));

        endpoints.MapPut(
                "/api/stacks/{stackId:guid}/agent-bindings",
                async (Guid stackId, HttpContext context, StackPivotDbContext dbContext, IUserIdentityService users, ISsoIdentityAdapter adapter) =>
                {
                    var admin = await RequireAdminAsync(context, dbContext, users, adapter);
                    if (admin is not null)
                    {
                        return admin;
                    }

                    UpdateStackAgentBindingsRequest request;
                    try
                    {
                        using var reader = new StreamReader(context.Request.Body);
                        request = JsonSerializer.Deserialize<UpdateStackAgentBindingsRequest>(await reader.ReadToEndAsync(context.RequestAborted), RequestJsonOptions)
                            ?? throw new JsonException();
                    }
                    catch (JsonException)
                    {
                        return ApiProblem.Create(context, "invalid_request", 422, "Binding request is invalid.");
                    }

                    var stack = await dbContext.Stacks.SingleOrDefaultAsync(value => value.StackId == stackId, context.RequestAborted);
                    if (stack is null
                        || request.AgentIds is null
                        || request.AgentIds.Distinct().Count() != request.AgentIds.Count)
                    {
                        return ApiProblem.Create(context, "resource_not_found", 404, "Stack was not found.");
                    }

                    var agents = await dbContext.AgentNodes
                        .Where(value => request.AgentIds.Contains(value.AgentId) && value.RevokedAt == null)
                        .ToListAsync(context.RequestAborted);
                    if (agents.Count != request.AgentIds.Count)
                    {
                        return ApiProblem.Create(context, "invalid_target", 422, "One or more agents are invalid.");
                    }

                    var identity = adapter.Require(context);
                    var user = await users.UpsertFromSsoAsync(identity, context.RequestAborted);
                    await using var transaction = await dbContext.Database.BeginTransactionAsync(context.RequestAborted);
                    var existing = await dbContext.StackAgentBindings.Where(value => value.StackId == stackId).ToListAsync(context.RequestAborted);
                    dbContext.StackAgentBindings.RemoveRange(existing);
                    dbContext.StackAgentBindings.AddRange(request.AgentIds.Select(agentId => new StackAgentBinding
                    {
                        Id = Guid.NewGuid(),
                        StackId = stackId,
                        AgentId = agentId,
                        CreatedAt = DateTimeOffset.UtcNow
                    }));
                    dbContext.AuditLogs.Add(new AuditLog
                    {
                        AuditId = Guid.NewGuid(),
                        ActorUserId = user.UserId,
                        Action = "agent_binding_updated",
                        ResourceType = "stack",
                        ResourceId = stackId.ToString(),
                        Result = "success",
                        CreatedAt = DateTimeOffset.UtcNow
                    });
                    await dbContext.SaveChangesAsync(context.RequestAborted);
                    await transaction.CommitAsync(context.RequestAborted);
                    return Results.NoContent();
                })
            .RequireAuthorization("sso")
            .WithMetadata(new RequireAntiforgeryTokenAttribute(true));

        return endpoints;
    }

    private static async Task<IResult?> RequireAdminAsync(
        HttpContext context,
        StackPivotDbContext dbContext,
        IUserIdentityService users,
        ISsoIdentityAdapter adapter)
    {
        try
        {
            var identity = adapter.Require(context);
            var user = await users.UpsertFromSsoAsync(identity, context.RequestAborted);
            return user.IsPlatformAdmin
                ? null
                : ApiProblem.Create(context, "insufficient_permission", 403, "Platform administrator permission is required.");
        }
        catch (UnauthorizedAccessException)
        {
            return ApiProblem.Create(context, "unauthenticated", 401, "Authentication is required.");
        }
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
