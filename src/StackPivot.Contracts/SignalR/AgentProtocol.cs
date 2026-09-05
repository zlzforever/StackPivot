using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using StackPivot.Contracts.Deployments;

namespace StackPivot.Contracts.SignalR;

public interface IProtocolMessage
{
    int SchemaVersion { get; }
}

public static class ProtocolVersion
{
    public const int Current = 1;
}

public static class AgentHubMethods
{
    public const string RegisterAgent = "RegisterAgent";
    public const string RegisterAgentAck = "RegisterAgentAck";
    public const string DeployStack = "DeployStack";
    public const string ReportTaskAccepted = "ReportTaskAccepted";
    public const string ReportTaskLog = "ReportTaskLog";
    public const string ReportTaskCompleted = "ReportTaskCompleted";
    public const string Heartbeat = "Heartbeat";
}

public static class ProtocolJson
{
    private static readonly JsonSerializerOptions Options = CreateOptions();

    public static string Serialize<T>(T value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value is IProtocolMessage message)
        {
            ProtocolValidation.EnsureSchemaVersion(message.SchemaVersion);
        }

        return JsonSerializer.Serialize(value, Options);
    }

    public static T Deserialize<T>(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);

        using var document = JsonDocument.Parse(json);
        if (typeof(IProtocolMessage).IsAssignableFrom(typeof(T)))
        {
            if (!document.RootElement.TryGetProperty("schemaVersion", out var schemaVersion)
                || schemaVersion.ValueKind != JsonValueKind.Number
                || schemaVersion.GetInt32() != ProtocolVersion.Current)
            {
                throw new JsonException($"Unsupported schemaVersion; expected {ProtocolVersion.Current}.");
            }
        }

        return JsonSerializer.Deserialize<T>(json, Options)
            ?? throw new JsonException("Payload was null.");
    }

    public static string SerializeSafeSnapshot(DeployStackCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        var safe = new
        {
            schemaVersion = command.SchemaVersion,
            taskId = command.TaskId,
            requestId = command.RequestId,
            stackId = command.StackId,
            agentId = command.AgentId,
            gitRepo = command.GitRepo,
            gitUserName = command.GitUserName,
            targetCommitHash = command.TargetCommitHash,
            stackGitRelativePath = command.StackGitRelativePath,
            agentStackLocalPath = command.AgentStackLocalPath,
            expiresAt = command.ExpiresAt,
            dispatchFingerprint = command.DispatchFingerprint
        };

        return JsonSerializer.Serialize(safe, Options);
    }

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            PropertyNameCaseInsensitive = false
        };
        options.Converters.Add(new DeploymentModeJsonConverter());
        return options;
    }
}

public static class DispatchFingerprint
{
    public static string Compute(DeployStackCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        return Compute(
            command.TaskId,
            command.RequestId,
            command.StackId,
            command.AgentId,
            command.GitRepo,
            command.GitUserName,
            command.TargetCommitHash,
            command.StackGitRelativePath,
            command.AgentStackLocalPath,
            command.ExpiresAt);
    }

    public static string Compute(
        Guid taskId,
        Guid requestId,
        Guid stackId,
        Guid agentId,
        string gitRepo,
        string gitUserName,
        string targetCommitHash,
        string stackGitRelativePath,
        string agentStackLocalPath,
        DateTimeOffset expiresAt)
    {
        var values = new[]
        {
            ProtocolVersion.Current.ToString(CultureInfo.InvariantCulture),
            taskId.ToString("D"),
            requestId.ToString("D"),
            stackId.ToString("D"),
            agentId.ToString("D"),
            gitRepo,
            gitUserName,
            targetCommitHash,
            stackGitRelativePath,
            agentStackLocalPath,
            expiresAt.UtcDateTime.Ticks.ToString(CultureInfo.InvariantCulture)
        };

        using var payload = new MemoryStream();
        Span<byte> length = stackalloc byte[sizeof(int)];
        foreach (var value in values)
        {
            var bytes = Encoding.UTF8.GetBytes(value ?? string.Empty);
            BinaryPrimitives.WriteInt32LittleEndian(length, bytes.Length);
            payload.Write(length);
            payload.Write(bytes);
        }

        return Convert.ToHexString(SHA256.HashData(payload.ToArray())).ToLowerInvariant();
    }

    public static bool Matches(DeployStackCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        return Matches(
            command.DispatchFingerprint,
            Compute(command));
    }

    public static bool Matches(string? actual, string expected)
    {
        if (string.IsNullOrWhiteSpace(actual)
            || actual.Length != expected.Length)
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(actual),
            Encoding.ASCII.GetBytes(expected));
    }
}

public sealed class DeploymentModeJsonConverter : JsonConverter<DeploymentMode>
{
    public override DeploymentMode Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String)
        {
            throw new JsonException("Deployment mode must be a string.");
        }

        return reader.GetString() switch
        {
            "boundAgents" => DeploymentMode.BoundAgents,
            "singleAgent" => DeploymentMode.SingleAgent,
            _ => throw new JsonException("Unknown deployment mode.")
        };
    }

    public override void Write(Utf8JsonWriter writer, DeploymentMode value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(value switch
        {
            DeploymentMode.BoundAgents => "boundAgents",
            DeploymentMode.SingleAgent => "singleAgent",
            _ => throw new JsonException("Unknown deployment mode.")
        });
    }
}

public sealed record ValidationResult(IReadOnlyList<string> Errors)
{
    public bool IsValid => Errors.Count == 0;
}

public static class ProtocolValidation
{
    public static bool IsFullCommitHash(string? value)
    {
        if (value is not ( { Length: 40 } or { Length: 64 }))
        {
            return false;
        }

        foreach (var character in value)
        {
            if (!((character is >= '0' and <= '9') || (character is >= 'a' and <= 'f')))
            {
                return false;
            }
        }

        return true;
    }

    public static bool IsSafeName(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > 50)
        {
            return false;
        }

        foreach (var character in value)
        {
            if (!((character is >= 'a' and <= 'z')
                || (character is >= '0' and <= '9')
                || character == '_'))
            {
                return false;
            }
        }

        return true;
    }

    public static ValidationResult Validate(DeployStackRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var errors = new List<string>();

        if (!IsFullCommitHash(request.TargetCommitHash))
        {
            errors.Add("invalid_commit");
        }

        switch (request.Mode)
        {
            case DeploymentMode.BoundAgents when request.AgentId is not null:
                errors.Add("agent_id_not_allowed");
                break;
            case DeploymentMode.SingleAgent when request.AgentId is null:
                errors.Add("agent_id_required");
                break;
            case DeploymentMode.SingleAgent when request.AgentId == Guid.Empty:
                errors.Add("agent_id_required");
                break;
            case DeploymentMode.BoundAgents:
            case DeploymentMode.SingleAgent:
                break;
            default:
                errors.Add("invalid_mode");
                break;
        }

        return new ValidationResult(errors);
    }

    public static void EnsureSchemaVersion(int schemaVersion)
    {
        if (schemaVersion != ProtocolVersion.Current)
        {
            throw new JsonException($"Unsupported schemaVersion; expected {ProtocolVersion.Current}.");
        }
    }
}
