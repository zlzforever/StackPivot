using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using StackPivot.Control.Domain.Entities;
using StackPivot.Control.Infrastructure.Persistence;

namespace StackPivot.Control.Auth;

public sealed record SsoUserIdentity(
    string Subject,
    string UserName,
    IReadOnlySet<string> Roles);

public interface ISsoIdentityAdapter
{
    SsoUserIdentity Require(HttpContext httpContext);
}

public sealed class HttpContextSsoIdentityAdapter : ISsoIdentityAdapter
{
    public SsoUserIdentity Require(HttpContext httpContext)
    {
        ArgumentNullException.ThrowIfNull(httpContext);

        var principal = httpContext.User;
        var subject = principal.FindFirstValue("sub")
            ?? throw new UnauthorizedAccessException("SSO subject is required.");
        var userName = principal.FindFirstValue(ClaimTypes.Name)
            ?? principal.Identity?.Name
            ?? subject;
        var roles = principal.FindAll(ClaimTypes.Role)
            .Concat(principal.FindAll("role"))
            .Select(claim => claim.Value)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return new SsoUserIdentity(subject, userName, roles);
    }
}

public interface IUserIdentityService
{
    Task<UserAccount> UpsertFromSsoAsync(SsoUserIdentity identity, CancellationToken cancellationToken);
}

public sealed class UserIdentityService(StackPivotDbContext dbContext) : IUserIdentityService
{
    public async Task<UserAccount> UpsertFromSsoAsync(
        SsoUserIdentity identity,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(identity);
        if (string.IsNullOrWhiteSpace(identity.Subject))
        {
            throw new UnauthorizedAccessException("SSO subject is required.");
        }

        var account = await dbContext.Users
            .SingleOrDefaultAsync(value => value.SsoSubject == identity.Subject, cancellationToken);
        var now = DateTimeOffset.UtcNow;
        if (account is null)
        {
            account = new UserAccount
            {
                UserId = Guid.NewGuid(),
                SsoSubject = identity.Subject,
                CreatedAt = now
            };
            dbContext.Users.Add(account);
        }

        account.UserName = identity.UserName;
        account.IsPlatformAdmin = identity.Roles.Contains("platform-admin", StringComparer.OrdinalIgnoreCase);
        account.UpdatedAt = now;
        await dbContext.SaveChangesAsync(cancellationToken);
        return account;
    }
}
