using StackPivot.Contracts.SignalR;

namespace StackPivot.Agent.Security;

public sealed record SafePath(string FullPath, string RelativePath);

public sealed class PathPolicy
{
    private readonly string configuredRoot;

    public PathPolicy(string configuredRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configuredRoot);
        if (!Path.IsPathFullyQualified(configuredRoot))
        {
            throw new PathPolicyException("Agent root must be an absolute path.");
        }

        this.configuredRoot = Path.GetFullPath(configuredRoot).TrimEnd(Path.DirectorySeparatorChar);
    }

    public Task<SafePath> ValidateStackPathAsync(
        string relativePath,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Validate(relativePath));
    }

    public Task<SafePath> ValidateStackPathAsync(
        string configuredRoot,
        string relativePath,
        CancellationToken cancellationToken)
    {
        return string.Equals(this.configuredRoot, Path.GetFullPath(configuredRoot).TrimEnd(Path.DirectorySeparatorChar), StringComparison.Ordinal)
            ? ValidateStackPathAsync(relativePath, cancellationToken)
            : new PathPolicy(configuredRoot).ValidateStackPathAsync(relativePath, cancellationToken);
    }

    private SafePath Validate(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath)
            || relativePath.Any(character => char.IsControl(character) || character == '\\' || character == ':')
            || Path.IsPathFullyQualified(relativePath))
        {
            throw new PathPolicyException("Stack path must be relative and contain no control characters.");
        }

        var segments = relativePath.Split('/', StringSplitOptions.None);
        if (segments.Length != 2
            || segments.Any(segment => string.IsNullOrEmpty(segment) || segment is "." or "..")
            || segments.Any(segment => !ProtocolValidation.IsSafeName(segment)))
        {
            throw new PathPolicyException("Stack path contains an unsafe segment.");
        }

        var fullPath = Path.GetFullPath(relativePath.Replace('/', Path.DirectorySeparatorChar), configuredRoot);
        var rootWithSeparator = configuredRoot + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(rootWithSeparator, StringComparison.Ordinal))
        {
            throw new PathPolicyException("Stack path escapes the agent root.");
        }

        EnsureNoSymlinkEscape(segments);
        return new SafePath(fullPath, relativePath);
    }

    private void EnsureNoSymlinkEscape(IReadOnlyList<string> segments)
    {
        var current = configuredRoot;
        foreach (var segment in segments)
        {
            current = Path.Combine(current, segment);
            var directory = new DirectoryInfo(current);
            if (directory.Exists && directory.LinkTarget is not null)
            {
                throw new PathPolicyException("Stack path contains a symbolic link.");
            }

            var file = new FileInfo(current);
            if (file.Exists && file.LinkTarget is not null)
            {
                throw new PathPolicyException("Stack path contains a symbolic link.");
            }
        }
    }
}

public sealed class PathPolicyException(string message) : Exception(message);
