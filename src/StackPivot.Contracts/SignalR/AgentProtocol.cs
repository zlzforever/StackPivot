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
            expiresAt = command.ExpiresAt
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
