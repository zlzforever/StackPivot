using System.Security.Claims;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Http;
using StackPivot.Control.Auth;
using StackPivot.Control.Authorization;
using StackPivot.Control.Domain.Entities;
using StackPivot.Control.Infrastructure.Persistence;
using Xunit;

namespace StackPivot.Control.Tests;

public sealed class AuthAndPermissionTests
{
    [Fact]
    public void AgentApiKeyManagerGeneratesAndRotatesNonReversibleKeys()
    {
        var manager = new AgentApiKeyManager(Encoding.UTF8.GetBytes("pepper-that-is-long-enough-for-tests"));
        var agentId = Guid.NewGuid();

        var first = manager.Issue(agentId);
        var rotated = manager.Issue(agentId, first.Version + 1);

        Assert.True(first.ApiKey.Length >= 43);
        Assert.NotEqual(first.ApiKey, rotated.ApiKey);
        Assert.NotEqual(first.ApiKey, first.ApiKeyHash);
        Assert.False(manager.Verify(agentId, first.ApiKey, rotated.ApiKeyHash, rotated.Version, null));
        Assert.True(manager.Verify(agentId, rotated.ApiKey, rotated.ApiKeyHash, rotated.Version, null));
        Assert.False(manager.Verify(agentId, rotated.ApiKey, rotated.ApiKeyHash, rotated.Version, DateTimeOffset.UtcNow));
    }

    [Fact]
    public void SsoAdapterRequiresSubAndMapsNameAndRoles()
    {
        var context = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                new[]
                {
                    new Claim("sub", "sso-subject"),
                    new Claim(ClaimTypes.Name, "alice"),
                    new Claim(ClaimTypes.Role, "platform-admin")
                },
                "sso"))
        };

        var identity = new HttpContextSsoIdentityAdapter().Require(context);

        Assert.Equal("sso-subject", identity.Subject);
        Assert.Equal("alice", identity.UserName);
        Assert.Contains("platform-admin", identity.Roles);
    }

    [Fact]
    public async Task WorkspaceAuthorizationAllowsEditorAndHidesUnauthorizedResources()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<StackPivotDbContext>()
            .UseSqlite(connection)
            .Options;

        await using (var seed = new StackPivotDbContext(options))
        {
            await seed.Database.EnsureCreatedAsync();
            var editor = new UserAccount
            {
                UserId = Guid.NewGuid(),
                UserName = "editor",
                SsoSubject = "subject-editor"
            };
            var readOnly = new UserAccount
            {
                UserId = Guid.NewGuid(),
                UserName = "readonly",
                SsoSubject = "subject-readonly"
            };
            var workspace = new Workspace
            {
                WorkspaceId = Guid.NewGuid(),
                Name = "workspace_one",
                DisplayName = "Workspace One"
            };
            seed.AddRange(editor, readOnly, workspace);
            seed.AddRange(
                new WorkspaceMember
                {
                    Id = Guid.NewGuid(),
                    WorkspaceId = workspace.WorkspaceId,
                    UserId = editor.UserId,
                    Permission = WorkspacePermission.Editor
                },
                new WorkspaceMember
                {
                    Id = Guid.NewGuid(),
                    WorkspaceId = workspace.WorkspaceId,
                    UserId = readOnly.UserId,
                    Permission = WorkspacePermission.ReadOnly
                });
            await seed.SaveChangesAsync();

            var service = new WorkspaceAuthorizationService(seed);
            var editorResult = await service.RequireAsync(
                editor.UserId,
                workspace.WorkspaceId,
                WorkspacePermission.Editor,
                CancellationToken.None);
            var readOnlyResult = await service.RequireAsync(
                readOnly.UserId,
                workspace.WorkspaceId,
                WorkspacePermission.Editor,
                CancellationToken.None);
            var unknownResult = await service.RequireAsync(
                Guid.NewGuid(),
                workspace.WorkspaceId,
                WorkspacePermission.ReadOnly,
                CancellationToken.None);

            Assert.True(editorResult.IsAllowed);
            Assert.False(readOnlyResult.IsAllowed);
            Assert.True(readOnlyResult.ResourceNotFound);
            Assert.False(unknownResult.IsAllowed);
            Assert.True(unknownResult.ResourceNotFound);
        }
    }
}
