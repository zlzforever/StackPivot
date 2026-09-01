using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using StackPivot.Control.Domain.Entities;
using StackPivot.Control.Infrastructure.Persistence;

namespace StackPivot.Control.Auth;

public static class AgentApiKeyDefaults
{
    public const string AuthenticationScheme = "AgentApiKey";
    public const string HeaderName = "X-Agent-Api-Key";
}

public sealed record AgentApiKeyIssue(
    string ApiKey,
    string ApiKeyHash,
    int Version,
    string ApiKeyLast4);

public sealed class AgentApiKeyManager
{
    private readonly byte[] pepper;

    public AgentApiKeyManager(byte[] pepper)
    {
        ArgumentNullException.ThrowIfNull(pepper);
        if (pepper.Length < 16)
        {
            throw new ArgumentException("API key pepper must contain at least 16 bytes.", nameof(pepper));
        }

        this.pepper = pepper.ToArray();
    }

    public AgentApiKeyIssue Issue(Guid agentId, int version = 1)
    {
        if (agentId == Guid.Empty)
        {
            throw new ArgumentException("Agent id is required.", nameof(agentId));
        }

        var keyBytes = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
        var key = Microsoft.AspNetCore.WebUtilities.WebEncoders.Base64UrlEncode(keyBytes);
        var hash = ComputeHash(agentId, key);
        var last4 = key[^4..];
        CryptographicOperations.ZeroMemory(keyBytes);
        return new AgentApiKeyIssue(key, hash, version, last4);
    }

    public bool Verify(
        Guid agentId,
        string candidate,
        string expectedHash,
        int expectedVersion,
        DateTimeOffset? revokedAt)
    {
        if (agentId == Guid.Empty
            || expectedVersion < 1
            || revokedAt is not null
            || string.IsNullOrWhiteSpace(candidate)
            || string.IsNullOrWhiteSpace(expectedHash))
        {
            return false;
        }

        var actual = Convert.FromBase64String(ComputeHashBytes(agentId, candidate));
        byte[] expected;
        try
        {
            expected = Convert.FromBase64String(expectedHash);
        }
        catch (FormatException)
        {
            CryptographicOperations.ZeroMemory(actual);
            return false;
        }

        var equal = CryptographicOperations.FixedTimeEquals(actual, expected);
        CryptographicOperations.ZeroMemory(actual);
        CryptographicOperations.ZeroMemory(expected);
        return equal;
    }

    public string ComputeHash(Guid agentId, string apiKey)
    {
        return Convert.ToBase64String(Convert.FromBase64String(ComputeHashBytes(agentId, apiKey)));
    }

    private string ComputeHashBytes(Guid agentId, string apiKey)
    {
        using var hmac = new System.Security.Cryptography.HMACSHA256(pepper);
        var keyBytes = System.Text.Encoding.UTF8.GetBytes(apiKey);
        var input = new byte[16 + keyBytes.Length];
        agentId.TryWriteBytes(input);
        keyBytes.CopyTo(input, 16);
        try
        {
            var digest = hmac.ComputeHash(input);
            try
            {
                return Convert.ToBase64String(digest);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(digest);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(keyBytes);
            CryptographicOperations.ZeroMemory(input);
        }
    }
}

public sealed class AgentApiKeyService(StackPivotDbContext dbContext, AgentApiKeyManager manager)
{
    public async Task<AgentApiKeyIssue> IssueAgentKeyAsync(
        Guid agentId,
        CancellationToken cancellationToken)
    {
        var agent = await RequireAgentAsync(agentId, cancellationToken);
        var issue = manager.Issue(agentId, Math.Max(1, agent.ApiKeyVersion + 1));
        agent.ApiKeyHash = issue.ApiKeyHash;
        agent.ApiKeyVersion = issue.Version;
        agent.ApiKeyLast4 = issue.ApiKeyLast4;
        agent.RevokedAt = null;
        await dbContext.SaveChangesAsync(cancellationToken);
        return issue;
    }

    public async Task<AgentApiKeyIssue> RotateKeyAsync(Guid agentId, CancellationToken cancellationToken)
    {
        return await IssueAgentKeyAsync(agentId, cancellationToken);
    }

    public async Task RevokeKeyAsync(Guid agentId, CancellationToken cancellationToken)
    {
        var agent = await RequireAgentAsync(agentId, cancellationToken);
        agent.RevokedAt = DateTimeOffset.UtcNow;
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public bool Verify(AgentNode agent, string candidate)
    {
        ArgumentNullException.ThrowIfNull(agent);
        return manager.Verify(agent.AgentId, candidate, agent.ApiKeyHash, agent.ApiKeyVersion, agent.RevokedAt);
    }

    private async Task<AgentNode> RequireAgentAsync(Guid agentId, CancellationToken cancellationToken)
    {
        return await dbContext.AgentNodes.SingleOrDefaultAsync(value => value.AgentId == agentId, cancellationToken)
            ?? throw new KeyNotFoundException("Agent was not found.");
    }
}

public sealed class AgentApiKeyAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    AgentApiKeyAuthenticationService authenticationService)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(AgentApiKeyDefaults.HeaderName, out var header)
            || string.IsNullOrWhiteSpace(header.ToString()))
        {
            return AuthenticateResult.NoResult();
        }

        var identity = await authenticationService.AuthenticateAsync(header.ToString(), Context.RequestAborted);
        if (identity is null)
        {
            return AuthenticateResult.Fail("Invalid agent credentials.");
        }

        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            new[]
            {
                new Claim(ClaimTypes.NameIdentifier, identity.AgentId.ToString()),
                new Claim("agent_id", identity.AgentId.ToString())
            },
            AgentApiKeyDefaults.AuthenticationScheme));
        return AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme.Name));
    }
}

public sealed record AgentApiKeyIdentity(Guid AgentId);

public sealed class AgentApiKeyAuthenticationService(
    StackPivotDbContext dbContext,
    AgentApiKeyService keyService)
{
    public async Task<AgentApiKeyIdentity?> AuthenticateAsync(
        string candidate,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return null;
        }

        var agents = await dbContext.AgentNodes
            .Where(value => value.RevokedAt == null)
            .ToListAsync(cancellationToken);
        foreach (var agent in agents)
        {
            if (keyService.Verify(agent, candidate))
            {
                agent.LastSeenAt = DateTimeOffset.UtcNow;
                await dbContext.SaveChangesAsync(cancellationToken);
                return new AgentApiKeyIdentity(agent.AgentId);
            }
        }

        return null;
    }
}
