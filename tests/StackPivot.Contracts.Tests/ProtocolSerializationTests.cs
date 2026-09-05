using System.Text.Json;
using StackPivot.Contracts.Agents;
using StackPivot.Contracts.Deployments;
using StackPivot.Contracts.SignalR;
using Xunit;

namespace StackPivot.Contracts.Tests;

public sealed class ProtocolSerializationTests
{
    [Fact]
    public void DeployRequestUsesCamelCaseAndLowercaseMode()
    {
        var request = new DeployStackRequest(
            "0123456789abcdef0123456789abcdef01234567",
            DeploymentMode.BoundAgents,
            null);

        var json = ProtocolJson.Serialize(request);

        Assert.Contains("\"targetCommitHash\"", json);
        Assert.Contains("\"mode\":\"boundAgents\"", json);
        Assert.DoesNotContain("TargetCommitHash", json);
        Assert.DoesNotContain("agentId", json);
    }

    [Theory]
    [InlineData("0123456789abcdef0123456789abcdef01234567")]
    [InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef")]
    public void FullCommitHashIsAccepted(string commit)
    {
        Assert.True(ProtocolValidation.IsFullCommitHash(commit));
    }

    [Theory]
    [InlineData("0123456789abcdef0123456789abcdef0123456")]
    [InlineData("0123456789ABCDEF0123456789abcdef01234567")]
    [InlineData("refs/heads/main")]
    [InlineData("0123456789abcdef0123456789abcdef01234567^{commit}")]
    public void NonFullCommitHashIsRejected(string commit)
    {
        Assert.False(ProtocolValidation.IsFullCommitHash(commit));
    }

    [Fact]
    public void UnknownSchemaVersionIsRejected()
    {
        const string payload = "{\"schemaVersion\":2,\"agentId\":\"00000000-0000-0000-0000-000000000001\",\"agentVersion\":\"1.0.0\",\"os\":\"linux\",\"composeMajorVersion\":2,\"capabilities\":[\"fullDeploy\"],\"sentAt\":\"2026-09-01T00:00:00Z\"}";

        Assert.Throws<JsonException>(() => ProtocolJson.Deserialize<AgentHello>(payload));
    }

    [Fact]
    public void UnknownJsonMemberIsRejected()
    {
        const string payload = "{\"targetCommitHash\":\"0123456789abcdef0123456789abcdef01234567\",\"mode\":\"boundAgents\",\"unexpected\":true}";

        Assert.Throws<JsonException>(() => ProtocolJson.Deserialize<DeployStackRequest>(payload));
    }

    [Fact]
    public void ProtocolMessageWithUnknownSchemaCannotBeSerialized()
    {
        var hello = new AgentHello(
            2,
            Guid.NewGuid(),
            "1.0.0",
            "linux",
            2,
            new[] { "fullDeploy" },
            DateTimeOffset.UtcNow);

        Assert.Throws<JsonException>(() => ProtocolJson.Serialize(hello));
    }

    [Fact]
    public void JsonMemberCasingIsStrict()
    {
        const string payload = "{\"TargetCommitHash\":\"0123456789abcdef0123456789abcdef01234567\",\"mode\":\"boundAgents\",\"agentId\":null}";

        Assert.Throws<JsonException>(() => ProtocolJson.Deserialize<DeployStackRequest>(payload));
    }

    [Fact]
    public void SingleAgentCannotUseAnEmptyAgentId()
    {
        var request = new DeployStackRequest(
            "0123456789abcdef0123456789abcdef01234567",
            DeploymentMode.SingleAgent,
            Guid.Empty);

        var validation = request.Validate();

        Assert.False(validation.IsValid);
        Assert.Contains("agent_id_required", validation.Errors);
    }

    [Fact]
    public void CommandSafeSnapshotNeverContainsAccessToken()
    {
        var command = new DeployStackCommand(
            ProtocolVersion.Current,
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            "https://git.example/repository.git",
            "git-user",
            "secret-git-token"u8.ToArray(),
            "0123456789abcdef0123456789abcdef01234567",
            "workspace_prod/stack_web",
            "/opt/agent-main/workspace_prod/stack_web",
            DateTimeOffset.UtcNow.AddMinutes(5));

        var snapshot = ProtocolJson.SerializeSafeSnapshot(command);

        Assert.DoesNotContain("secret-git-token", snapshot);
        Assert.DoesNotContain("accessToken", snapshot);
        Assert.Contains("targetCommitHash", snapshot);
    }

    [Fact]
    public void DispatchFingerprintBindsTheNonSecretDispatchSnapshot()
    {
        var command = new DeployStackCommand(
            ProtocolVersion.Current,
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            "https://git.example/repository.git",
            "git-user",
            "secret-git-token"u8.ToArray(),
            "0123456789abcdef0123456789abcdef01234567",
            "workspace_prod/stack_web",
            "/opt/agent-main/workspace_prod/stack_web",
            DateTimeOffset.UtcNow.AddMinutes(5));

        var fingerprint = DispatchFingerprint.Compute(command);
        var changed = command with { TargetCommitHash = "abcdef0123456789abcdef0123456789abcdef01" };

        Assert.Matches("^[0-9a-f]{64}$", fingerprint);
        Assert.NotEqual(fingerprint, DispatchFingerprint.Compute(changed));
    }
}
