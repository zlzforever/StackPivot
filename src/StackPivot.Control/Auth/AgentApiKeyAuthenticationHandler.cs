using System.Globalization;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using StackPivot.Control.Application.Audit;
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

public sealed class AgentApiKeyService(
    StackPivotDbContext dbContext,
    AgentApiKeyManager manager,
    AuditWriter? auditWriter = null)
{
    public sealed record AgentCreationResult(AgentNode Agent, AgentApiKeyIssue Issue);

    public async Task<AgentCreationResult> CreateAgentWithKeyAsync(
        string name,
        string remark,
        Guid actorUserId,
        Guid requestId,
        CancellationToken cancellationToken)
    {
        await using var transaction = await BeginTransactionAsync(cancellationToken);
        var agent = new AgentNode
        {
            AgentId = Guid.NewGuid(),
            Name = name,
            Remark = remark,
            CapabilitiesJson = "[]"
        };
        dbContext.AgentNodes.Add(agent);
        await dbContext.SaveChangesAsync(cancellationToken);
        var issue = manager.Issue(agent.AgentId, 1);
        agent.ApiKeyHash = issue.ApiKeyHash;
        agent.ApiKeyVersion = issue.Version;
        agent.ApiKeyLast4 = issue.ApiKeyLast4;
        agent.RevokedAt = null;
        AddAudit(AuditActions.AgentKeyCreated, requestId, actorUserId, agent.AgentId, "success");
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new AgentCreationResult(agent, issue);
    }

    public async Task<AgentApiKeyIssue> IssueAgentKeyAsync(
        Guid agentId,
        CancellationToken cancellationToken)
    {
        return await IssueAgentKeyAsync(agentId, null, null, null, cancellationToken);
    }

    public async Task<AgentApiKeyIssue> RotateKeyAsync(Guid agentId, CancellationToken cancellationToken)
    {
        return await IssueAgentKeyAsync(agentId, cancellationToken);
    }

    public Task<AgentApiKeyIssue> RotateKeyAsync(
        Guid agentId,
        Guid actorUserId,
        Guid requestId,
        CancellationToken cancellationToken)
    {
        return IssueAgentKeyAsync(agentId, actorUserId, requestId, AuditActions.AgentKeyRotated, cancellationToken);
    }

    public async Task<AgentApiKeyIssue> IssueAgentKeyAsync(
        Guid agentId,
        Guid? actorUserId,
        Guid? requestId,
        string? auditAction,
        CancellationToken cancellationToken)
    {
        if (agentId == Guid.Empty)
        {
            throw new KeyNotFoundException("Agent was not found.");
        }

        for (var attempt = 0; attempt < 5; attempt++)
        {
            dbContext.ChangeTracker.Clear();
            var agent = await RequireAgentAsync(agentId, cancellationToken);
            var issue = manager.Issue(agentId, Math.Max(1, agent.ApiKeyVersion + 1));
            agent.ApiKeyHash = issue.ApiKeyHash;
            agent.ApiKeyVersion = issue.Version;
            agent.ApiKeyLast4 = issue.ApiKeyLast4;
            agent.RevokedAt = null;
            await using var transaction = await BeginTransactionAsync(cancellationToken);
            try
            {
                AddAudit(auditAction, requestId, actorUserId, agentId, "success");
                await dbContext.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return issue;
            }
            catch (DbUpdateConcurrencyException) when (attempt < 4)
            {
                await transaction.RollbackAsync(CancellationToken.None);
            }
            catch (Microsoft.Data.Sqlite.SqliteException exception) when (IsBusy(exception) && attempt < 4)
            {
                await transaction.RollbackAsync(CancellationToken.None);
            }
        }

        throw new InvalidOperationException("Agent key operation could not be completed safely.");
    }

    public async Task RevokeKeyAsync(Guid agentId, CancellationToken cancellationToken)
    {
        await RevokeKeyAsync(agentId, null, null, cancellationToken);
    }

    public async Task RevokeKeyAsync(
        Guid agentId,
        Guid? actorUserId,
        Guid? requestId,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            dbContext.ChangeTracker.Clear();
            var agent = await RequireAgentAsync(agentId, cancellationToken);
            agent.RevokedAt = DateTimeOffset.UtcNow;
            await using var transaction = await BeginTransactionAsync(cancellationToken);
            try
            {
                AddAudit(AuditActions.AgentKeyRevoked, requestId, actorUserId, agentId, "success");
                await dbContext.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return;
            }
            catch (DbUpdateConcurrencyException) when (attempt < 4)
            {
                await transaction.RollbackAsync(CancellationToken.None);
            }
            catch (Microsoft.Data.Sqlite.SqliteException exception) when (IsBusy(exception) && attempt < 4)
            {
                await transaction.RollbackAsync(CancellationToken.None);
            }
        }

        throw new InvalidOperationException("Agent key operation could not be completed safely.");
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

    private void AddAudit(
        string? action,
        Guid? requestId,
        Guid? actorUserId,
        Guid agentId,
        string result)
    {
        if (action is null || auditWriter is null)
        {
            return;
        }

        auditWriter.Add(action, requestId, actorUserId, agentId, "agent", agentId.ToString(), result);
    }

    private Task<IDbContextTransaction> BeginTransactionAsync(CancellationToken cancellationToken)
    {
        return BeginTransactionWithRetryAsync(cancellationToken);
    }

    private async Task<IDbContextTransaction> BeginTransactionWithRetryAsync(CancellationToken cancellationToken)
    {
        Microsoft.Data.Sqlite.SqliteException? lastBusy = null;
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                return await dbContext.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable, cancellationToken);
            }
            catch (Microsoft.Data.Sqlite.SqliteException exception) when (IsBusy(exception))
            {
                lastBusy = exception;
                if (attempt == 4)
                {
                    break;
                }

                await Task.Delay(TimeSpan.FromMilliseconds(25 * (attempt + 1)), cancellationToken);
            }
        }

        throw new InvalidOperationException("Agent key transaction could not be started safely.", lastBusy);
    }

    private static bool IsBusy(Microsoft.Data.Sqlite.SqliteException exception) =>
        exception.SqliteErrorCode is 5 or 6;
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
                new Claim("agent_id", identity.AgentId.ToString()),
                new Claim("agent_key_version", identity.ApiKeyVersion.ToString(CultureInfo.InvariantCulture))
            },
            AgentApiKeyDefaults.AuthenticationScheme));
        return AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme.Name));
    }
}

public sealed record AgentApiKeyIdentity(Guid AgentId, int ApiKeyVersion = 0);

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
            .AsNoTracking()
            .ToListAsync(cancellationToken);
        foreach (var agent in agents)
        {
            if (keyService.Verify(agent, candidate))
            {
                var lastSeenAt = DateTimeOffset.UtcNow;
                await dbContext.AgentNodes
                    .Where(value => value.AgentId == agent.AgentId
                        && value.ApiKeyVersion == agent.ApiKeyVersion
                        && value.ApiKeyHash == agent.ApiKeyHash
                        && value.RevokedAt == null)
                    .ExecuteUpdateAsync(
                        setters => setters.SetProperty(value => value.LastSeenAt, lastSeenAt),
                        cancellationToken);
                return new AgentApiKeyIdentity(agent.AgentId, agent.ApiKeyVersion);
            }
        }

        return null;
    }
}
