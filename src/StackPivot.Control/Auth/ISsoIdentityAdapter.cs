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

public static class SsoAuthenticationDefaults
{
    public const string Scheme = "sso";
    public const string CookieScheme = "sso-session";
}

public interface ISsoIdentityAdapter
{
    SsoUserIdentity Require(HttpContext httpContext);
}

public static class SsoIdentityMapper
{
    public static SsoUserIdentity Require(ClaimsPrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(principal);
        var identity = principal.Identities.FirstOrDefault(value =>
            value.IsAuthenticated
            && value.AuthenticationType is not null
            && (string.Equals(value.AuthenticationType, SsoAuthenticationDefaults.Scheme, StringComparison.Ordinal)
                || string.Equals(value.AuthenticationType, SsoAuthenticationDefaults.CookieScheme, StringComparison.Ordinal)));
        if (identity is null)
        {
            throw new UnauthorizedAccessException("An authenticated SSO principal is required.");
        }

        var subject = identity.FindFirst("sub")?.Value;
        if (string.IsNullOrWhiteSpace(subject))
        {
            throw new UnauthorizedAccessException("SSO subject is required.");
        }

        var userName = identity.FindFirst(ClaimTypes.Name)?.Value
            ?? identity.Name
            ?? subject;
        var roles = identity.FindAll(ClaimTypes.Role)
            .Concat(identity.FindAll("role"))
            .Select(claim => claim.Value)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return new SsoUserIdentity(subject, userName, roles);
    }
}

public sealed class HttpContextSsoIdentityAdapter : ISsoIdentityAdapter
{
    public SsoUserIdentity Require(HttpContext httpContext)
    {
        ArgumentNullException.ThrowIfNull(httpContext);

        return SsoIdentityMapper.Require(httpContext.User);
    }
}

public interface IUserIdentityService
{
    Task<UserAccount> UpsertFromSsoAsync(SsoUserIdentity identity, CancellationToken cancellationToken);
    Task<UserAccount> UpsertFromClaimsAsync(ClaimsPrincipal principal, CancellationToken cancellationToken);
}

public sealed class UserIdentityService(StackPivotDbContext dbContext) : IUserIdentityService
{
    public Task<UserAccount> UpsertFromClaimsAsync(
        ClaimsPrincipal principal,
        CancellationToken cancellationToken)
    {
        return UpsertFromSsoAsync(SsoIdentityMapper.Require(principal), cancellationToken);
    }

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
