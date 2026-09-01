using Microsoft.EntityFrameworkCore;
using StackPivot.Control.Domain.Entities;
using StackPivot.Control.Infrastructure.Persistence;

namespace StackPivot.Control.Authorization;

public sealed record AuthorizationResult(bool IsAllowed, bool ResourceNotFound, string? Reason = null);

public sealed class WorkspaceAuthorizationService(StackPivotDbContext dbContext)
{
    public async Task<AuthorizationResult> RequireAsync(
        Guid userId,
        Guid workspaceId,
        WorkspacePermission permission,
        CancellationToken cancellationToken)
    {
        var workspaceExists = await dbContext.Workspaces
            .AsNoTracking()
            .AnyAsync(value => value.WorkspaceId == workspaceId, cancellationToken);
        if (!workspaceExists)
        {
            return new AuthorizationResult(false, true, "resource_not_found");
        }

        var user = await dbContext.Users
            .AsNoTracking()
            .SingleOrDefaultAsync(value => value.UserId == userId, cancellationToken);
        if (user is null)
        {
            return new AuthorizationResult(false, true, "resource_not_found");
        }

        if (permission == WorkspacePermission.PlatformAdmin)
        {
            return user.IsPlatformAdmin
                ? new AuthorizationResult(true, false)
                : new AuthorizationResult(false, true, "resource_not_found");
        }

        if (user.IsPlatformAdmin)
        {
            return new AuthorizationResult(true, false);
        }

        var memberPermission = await dbContext.WorkspaceMembers
            .AsNoTracking()
            .Where(value => value.WorkspaceId == workspaceId && value.UserId == userId)
            .Select(value => (WorkspacePermission?)value.Permission)
            .SingleOrDefaultAsync(cancellationToken);

        var allowed = permission switch
        {
            WorkspacePermission.ReadOnly => memberPermission is not null,
            WorkspacePermission.Editor => memberPermission == WorkspacePermission.Editor,
            _ => false
        };
        return allowed
            ? new AuthorizationResult(true, false)
            : new AuthorizationResult(false, true, "resource_not_found");
    }

    public async Task<(Stack Stack, AuthorizationResult Authorization)?> FindAuthorizedStackAsync(
        Guid userId,
        Guid stackId,
        WorkspacePermission permission,
        CancellationToken cancellationToken)
    {
        var stack = await dbContext.Stacks
            .AsNoTracking()
            .SingleOrDefaultAsync(value => value.StackId == stackId, cancellationToken);
        if (stack is null)
        {
            return null;
        }

        var authorization = await RequireAsync(userId, stack.WorkspaceId, permission, cancellationToken);
        return (stack, authorization);
    }
}
