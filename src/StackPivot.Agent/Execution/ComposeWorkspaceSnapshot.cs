using System.Text;
using System.Text.RegularExpressions;
using StackPivot.Agent.Security;

namespace StackPivot.Agent.Execution;

internal sealed class ComposeWorkspaceSnapshot : IDisposable
{
    private static readonly Regex PathProperty = new(
        @"^(?<key>[A-Za-z0-9_.-]+)\s*:\s*(?<value>.*)$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly HashSet<string> PathKeys = new(StringComparer.Ordinal)
    {
        "build",
        "cache_from",
        "cache_to",
        "env_file",
        "file",
        "context",
        "device",
        "dest",
        "dockerfile",
        "additional_contexts",
        "include",
        "path",
        "project_directory",
        "ssh",
        "source",
        "volumes",
        "devices"
    };

    private readonly TemporaryDirectory temporaryDirectory;
    private bool disposed;

    private ComposeWorkspaceSnapshot(TemporaryDirectory temporaryDirectory, string composeFileName)
    {
        this.temporaryDirectory = temporaryDirectory;
        WorkingDirectory = temporaryDirectory.FullPath;
        WorkingDirectoryHandle = temporaryDirectory.Directory;
        ComposeFileName = composeFileName;
    }

    public string WorkingDirectory { get; }

    public SafeDirectoryHandle WorkingDirectoryHandle { get; }

    public string ComposeFileName { get; }

    public static ComposeWorkspaceSnapshot Create(
        SafeDirectoryHandle source,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        cancellationToken.ThrowIfCancellationRequested();

        var temporaryDirectory = SafeDirectoryHandle.CreateTemporaryDirectory("compose-");
        try
        {
            CopyDirectory(source, temporaryDirectory.Directory, isRoot: true, cancellationToken);
            ValidateComposeReferences(temporaryDirectory.Directory);
            return new ComposeWorkspaceSnapshot(temporaryDirectory, FindComposeFileName(temporaryDirectory.Directory));
        }
        catch
        {
            temporaryDirectory.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        temporaryDirectory.Dispose();
    }

    private static void CopyDirectory(
        SafeDirectoryHandle source,
        SafeDirectoryHandle destination,
        bool isRoot,
        CancellationToken cancellationToken)
    {
        foreach (var name in source.EnumerateEntryNames())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (name is "." or "..")
            {
                throw new PathPolicyException("Directory enumeration returned an unsafe entry.");
            }

            if (isRoot && name == ".git")
            {
                continue;
            }

            if (name == ".git" || IsSensitiveEnvironmentFile(name))
            {
                throw new PathPolicyException("Compose workspace contains a prohibited file.");
            }

            var sourceDirectory = source.TryOpenChildDirectory(name);
            if (sourceDirectory is not null)
            {
                using (sourceDirectory)
                using (var destinationDirectory = destination.OpenChildDirectory(name, create: true))
                {
                    CopyDirectory(sourceDirectory, destinationDirectory, isRoot: false, cancellationToken);
                }

                continue;
            }

            using var sourceFile = source.OpenFile(name, FileMode.Open, FileAccess.Read);
            using var destinationFile = destination.OpenFile(name, FileMode.Create, FileAccess.Write);
            sourceFile.CopyTo(destinationFile);
        }
    }

    private static void ValidateComposeReferences(SafeDirectoryHandle workspace)
    {
        string compose;
        using (var file = OpenComposeFile(workspace))
        using (var reader = new StreamReader(file, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
        {
            compose = reader.ReadToEnd();
        }

        var activeListKey = string.Empty;
        foreach (var rawLine in compose.Split('\n'))
        {
            var line = RemoveComment(rawLine.TrimEnd('\r'));
            var trimmed = line.Trim();
            if (trimmed.Length == 0)
            {
                continue;
            }

            if (trimmed.Contains('&') || trimmed.Contains('*'))
            {
                throw new PathPolicyException("Compose file aliases and anchors are not supported safely.");
            }

            var property = PathProperty.Match(trimmed);
            if (property.Success)
            {
                var key = property.Groups["key"].Value;
                var value = property.Groups["value"].Value;
                if (activeListKey == "additional_contexts" && !PathKeys.Contains(key))
                {
                    ValidateAdditionalContextValue(value);
                }

                activeListKey = PathKeys.Contains(key) ? key : string.Empty;
                if (PathKeys.Contains(key))
                {
                    switch (key)
                    {
                        case "build":
                            ValidateBuildValue(value);
                            break;
                        case "volumes":
                        case "devices":
                            ValidateMountValue(value);
                            break;
                        case "additional_contexts":
                            ValidateAdditionalContextValue(value);
                            break;
                        default:
                            ValidatePathValue(value);
                            break;
                    }
                }

                continue;
            }

            if (activeListKey.Length > 0 && trimmed.StartsWith('-'))
            {
                var value = trimmed[1..];
                switch (activeListKey)
                {
                    case "volumes":
                    case "devices":
                        ValidateMountValue(value);
                        break;
                    case "additional_contexts":
                        ValidateAdditionalContextValue(value);
                        break;
                    default:
                        ValidatePathValue(value);
                        break;
                }

                continue;
            }

            activeListKey = string.Empty;
        }
    }

    private static FileStream OpenComposeFile(SafeDirectoryHandle workspace)
    {
        try
        {
            return workspace.OpenFile("compose.yaml", FileMode.Open, FileAccess.Read);
        }
        catch (PathPolicyException exception) when (exception.ErrorNumber == LinuxPathOperations.EntryNotFound)
        {
            return workspace.OpenFile("compose.yml", FileMode.Open, FileAccess.Read);
        }
    }

    private static string FindComposeFileName(SafeDirectoryHandle workspace)
    {
        try
        {
            using var compose = workspace.OpenFile("compose.yaml", FileMode.Open, FileAccess.Read);
            return "compose.yaml";
        }
        catch (PathPolicyException exception) when (exception.ErrorNumber == LinuxPathOperations.EntryNotFound)
        {
            using var compose = workspace.OpenFile("compose.yml", FileMode.Open, FileAccess.Read);
            return "compose.yml";
        }
    }

    private static void ValidatePathValue(string value)
    {
        var trimmedValue = value.TrimStart();
        if (trimmedValue.StartsWith('|') || trimmedValue.StartsWith('>'))
        {
            throw new PathPolicyException("Compose file contains an unresolved path expression.");
        }

        if (value.Contains('$')
            || value.Contains('\\')
            || value.Contains('*'))
        {
            throw new PathPolicyException("Compose file contains an unresolved or unsafe path expression.");
        }

        foreach (var candidate in value
            .Split([',', '[', ']'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var path = candidate.Trim().Trim('"', '\'');
            if (path.Length == 0 || path is "{}" or "[]")
            {
                continue;
            }

            if (path.StartsWith('-'))
            {
                path = path[1..].Trim();
            }

            if (ContainsUnsafePathToken(path))
            {
                throw new PathPolicyException("Compose file references a path outside the private workspace.");
            }

            if (path.Length == 0
                || Path.IsPathFullyQualified(path)
                || path.StartsWith('~')
                || path.Split('/', StringSplitOptions.None).Any(segment => segment is ".." or ""))
            {
                throw new PathPolicyException("Compose file references a path outside the private workspace.");
            }

            var fileName = path[(path.LastIndexOf('/') + 1)..];
            if (IsSensitiveEnvironmentFile(fileName))
            {
                throw new PathPolicyException("Compose file references a sensitive environment file.");
            }
        }
    }

    private static void ValidateBuildValue(string value)
    {
        ValidatePathValue(value);
    }

    private static void ValidateMountValue(string value)
    {
        ValidatePathValue(value);

        foreach (var candidate in value
            .Split([',', '[', ']'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var mount = candidate.Trim().Trim('"', '\'');
            if (mount.Length == 0 || mount is "{}" or "[]")
            {
                continue;
            }

            if (mount.StartsWith('{'))
            {
                ValidatePathValue(mount);
                continue;
            }

            var separator = mount.IndexOf(':');
            if (separator < 0)
            {
                continue;
            }

            var source = mount[..separator].Trim().Trim('"', '\'');
            if (source.Length > 0)
            {
                ValidatePathValue(source);
            }
        }
    }

    private static void ValidateAdditionalContextValue(string value)
    {
        var trimmedValue = value.TrimStart();
        if (trimmedValue.StartsWith('|') || trimmedValue.StartsWith('>'))
        {
            throw new PathPolicyException("Compose file contains an unresolved build context expression.");
        }

        if (value.Contains('$')
            || value.Contains('\\')
            || value.Contains('*'))
        {
            throw new PathPolicyException("Compose file contains an unresolved or unsafe build context expression.");
        }

        ValidatePathValue(value);
        foreach (var candidate in value
            .Split([',', '[', ']'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var context = candidate.Trim().Trim('"', '\'');
            if (context.Length == 0 || context is "{}" or "[]")
            {
                continue;
            }

            var separator = context.IndexOf('=');
            if (separator >= 0)
            {
                context = context[(separator + 1)..].Trim();
            }

            ValidatePathValue(context);
        }
    }

    private static bool ContainsUnsafePathToken(string path)
    {
        var segments = path.Split('/', StringSplitOptions.None);
        return segments.Any(segment => segment == "..")
            || path.StartsWith('/')
            || path.StartsWith("~/", StringComparison.Ordinal)
            || path.StartsWith("~\\", StringComparison.Ordinal)
            || Regex.IsMatch(path, @"(^|[\s:=\[,])(?:[A-Za-z]:[\\/]|/|\.\.(?:[/\\]|$))", RegexOptions.CultureInvariant);
    }

    private static string RemoveComment(string value)
    {
        var quote = '\0';
        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            if (quote != '\0')
            {
                if (character == quote && (index == 0 || value[index - 1] != '\\'))
                {
                    quote = '\0';
                }

                continue;
            }

            if (character is '\'' or '"')
            {
                quote = character;
            }
            else if (character == '#')
            {
                return value[..index];
            }
        }

        return value;
    }

    private static bool IsSensitiveEnvironmentFile(string fileName)
    {
        return fileName.Equals(".env", StringComparison.Ordinal)
            || fileName.StartsWith(".env.", StringComparison.Ordinal)
            || fileName.Equals(".secret.env", StringComparison.Ordinal)
            || fileName.StartsWith(".secret.env.", StringComparison.Ordinal);
    }
}
