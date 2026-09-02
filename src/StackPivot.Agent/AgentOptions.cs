using System.Security;
using System.Text;
using Microsoft.Extensions.Configuration;

namespace StackPivot.Agent;

public sealed record AgentOptions(
    Guid AgentId,
    string ControlHubUrl,
    string ApiKey,
    string AgentRoot)
{
    public const string DefaultAgentRoot = "/opt/agent-main";
    public const string ApiKeyFileEnvironmentVariable = "STACKPIVOT_AGENT_API_KEY_FILE";

    public IReadOnlySet<string>? AllowedRemoteHosts { get; init; }

    public static AgentOptions FromConfiguration(
        IConfiguration configuration,
        bool allowInlineApiKey = false)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var agentIdText = ReadValue(configuration, "STACKPIVOT_AGENT_ID", "StackPivot:AgentId");
        if (!Guid.TryParse(agentIdText, out var agentId) || agentId == Guid.Empty)
        {
            throw new InvalidOperationException("STACKPIVOT_AGENT_ID is required and must be a UUID.");
        }

        var controlHubUrl = ReadValue(configuration, "STACKPIVOT_CONTROL_HUB_URL", "StackPivot:ControlHubUrl");
        if (!Uri.TryCreate(controlHubUrl, UriKind.Absolute, out var hubUri)
            || !string.Equals(hubUri.Scheme, Uri.UriSchemeWss, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("STACKPIVOT_CONTROL_HUB_URL is required and must use wss.");
        }

        var agentRoot = ReadValue(configuration, "STACKPIVOT_AGENT_WORK_ROOT", "StackPivot:AgentRoot")
            ?? DefaultAgentRoot;
        if (!string.Equals(agentRoot, DefaultAgentRoot, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("STACKPIVOT_AGENT_WORK_ROOT must be /opt/agent-main.");
        }

        var apiKey = ReadApiKey(configuration, allowInlineApiKey);
        var allowedRemoteHosts = (configuration["StackPivot:AllowedRemoteHosts"] ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return new AgentOptions(agentId, controlHubUrl!, apiKey, agentRoot)
        {
            AllowedRemoteHosts = allowedRemoteHosts
        };
    }

    private static string? ReadValue(
        IConfiguration configuration,
        string environmentVariable,
        string configurationKey)
    {
        return Environment.GetEnvironmentVariable(environmentVariable)
            ?? configuration[environmentVariable]
            ?? configuration[configurationKey];
    }

    private static string ReadApiKey(IConfiguration configuration, bool allowInlineApiKey)
    {
        var credentialPath = ReadValue(configuration, ApiKeyFileEnvironmentVariable, "StackPivot:ApiKeyFile");
        if (credentialPath is not null)
        {
            return ReadCredentialFile(credentialPath);
        }

        if (!allowInlineApiKey)
        {
            throw new InvalidOperationException($"{ApiKeyFileEnvironmentVariable} is required.");
        }

        return ValidateApiKey(configuration["StackPivot:ApiKey"]);
    }

    private static string ReadCredentialFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
        {
            throw new InvalidOperationException($"{ApiKeyFileEnvironmentVariable} must be an absolute path.");
        }

        byte[]? contents = null;
        try
        {
            var fileInfo = new FileInfo(path);
            if (fileInfo.Attributes.HasFlag(FileAttributes.Directory)
                || fileInfo.LinkTarget is not null
                || fileInfo.Length is <= 0 or > 4096)
            {
                throw new InvalidOperationException($"{ApiKeyFileEnvironmentVariable} credential file is invalid.");
            }

            contents = File.ReadAllBytes(path);
            var text = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true)
                .GetString(contents)
                .TrimEnd('\r', '\n');
            return ValidateApiKey(text);
        }
        catch (InvalidOperationException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or ArgumentException
            or NotSupportedException
            or SecurityException
            or DecoderFallbackException)
        {
            throw new InvalidOperationException("Agent API key credential file could not be read.");
        }
        finally
        {
            if (contents is not null)
            {
                System.Security.Cryptography.CryptographicOperations.ZeroMemory(contents);
            }
        }
    }

    private static string ValidateApiKey(string? value)
    {
        if (string.IsNullOrEmpty(value)
            || value.Length > 4096
            || value.Any(character => !IsApiKeyCharacter(character)))
        {
            throw new InvalidOperationException("Agent API key credential is invalid.");
        }

        return value;
    }

    private static bool IsApiKeyCharacter(char character) =>
        character is (>= 'a' and <= 'z')
            or (>= 'A' and <= 'Z')
            or (>= '0' and <= '9')
            or '-'
            or '_';
}
