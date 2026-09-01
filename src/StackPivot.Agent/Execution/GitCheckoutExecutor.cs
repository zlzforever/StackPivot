using System.Text.Json;
using System.Security.Cryptography;
using StackPivot.Agent.Security;
using StackPivot.Contracts.Deployments;
using StackPivot.Contracts.SignalR;

namespace StackPivot.Agent.Execution;

public sealed record GitDeploymentInput(
    string GitRepo,
    string GitUserName,
    byte[] AccessToken,
    string TargetCommitHash,
    string StackGitRelativePath,
    string AgentStackLocalPath);

public sealed record GitCheckoutResult(
    bool Success,
    string? ErrorCode,
    IReadOnlyList<string> MaterializedFiles);

public interface IGitCheckoutExecutor
{
    Task<GitCheckoutResult> MaterializeAsync(
        GitDeploymentInput input,
        CancellationToken cancellationToken);
}

public sealed class GitTreePolicyException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

public static class GitTreePolicy
{
    public static IReadOnlyList<string> Validate(string treeOutput, string stackPath)
    {
        ArgumentNullException.ThrowIfNull(treeOutput);
        ArgumentException.ThrowIfNullOrWhiteSpace(stackPath);
        var prefix = stackPath.TrimEnd('/') + "/";
        var files = new List<string>();
        foreach (var rawLine in treeOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var line = rawLine.TrimEnd('\r');
            var separator = line.IndexOf('\t');
            string mode;
            string type;
            string fullPath;
            if (separator < 0)
            {
                mode = "100644";
                type = "blob";
                fullPath = line.Trim();
            }
            else
            {
                var header = line[..separator].Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (header.Length < 2)
                {
                    throw new GitTreePolicyException("invalid_path", "Git tree entry is invalid.");
                }

                mode = header[0];
                type = header[1];
                fullPath = line[(separator + 1)..];
            }

            if (!fullPath.StartsWith(prefix, StringComparison.Ordinal))
            {
                throw new GitTreePolicyException("invalid_path", "Git tree entry is outside the stack path.");
            }

            var relative = fullPath[prefix.Length..];
            ValidateRelativePath(relative);
            if (mode == "120000" || !string.Equals(type, "blob", StringComparison.Ordinal))
            {
                throw new GitTreePolicyException("policy_violation", "Symlinks and non-file Git tree entries are not deployable.");
            }

            var fileName = relative[(relative.LastIndexOf('/') + 1)..];
            if (IsSensitiveEnvironmentFile(fileName))
            {
                throw new GitTreePolicyException("policy_violation", "Sensitive environment files are not deployable.");
            }

            files.Add(relative);
        }

        if (!files.Contains("compose.yaml", StringComparer.Ordinal)
            && !files.Contains("compose.yml", StringComparer.Ordinal))
        {
            throw new GitTreePolicyException("invalid_path", "The stack does not contain a compose file.");
        }

        return files;
    }

    private static void ValidateRelativePath(string relative)
    {
        if (string.IsNullOrWhiteSpace(relative)
            || relative.Any(character => char.IsControl(character) || character == '\\' || character == '\0')
            || Path.IsPathFullyQualified(relative))
        {
            throw new GitTreePolicyException("invalid_path", "Git tree path is invalid.");
        }

        var segments = relative.Split('/', StringSplitOptions.None);
        if (segments.Any(segment => string.IsNullOrEmpty(segment) || segment is "." or ".." or ".git"))
        {
            throw new GitTreePolicyException("policy_violation", "Git tree path contains an unsafe segment.");
        }
    }

    private static bool IsSensitiveEnvironmentFile(string fileName)
    {
        return fileName.Equals(".env", StringComparison.Ordinal)
            || fileName.StartsWith(".env.", StringComparison.Ordinal)
            || fileName.Equals(".secret.env", StringComparison.Ordinal)
            || fileName.StartsWith(".secret.env.", StringComparison.Ordinal);
    }
}

public sealed class GitCheckoutExecutor : IGitCheckoutExecutor
{
    private static readonly string[] InitArguments = ["init"];
    private static readonly JsonSerializerOptions MetadataJsonOptions = new();
    private readonly IProcessRunner processRunner;
    private readonly PathPolicy pathPolicy;
    private readonly TimeSpan timeout;

    public GitCheckoutExecutor(
        IProcessRunner processRunner,
        PathPolicy pathPolicy,
        TimeSpan timeout)
    {
        this.processRunner = processRunner ?? throw new ArgumentNullException(nameof(processRunner));
        this.pathPolicy = pathPolicy ?? throw new ArgumentNullException(nameof(pathPolicy));
        this.timeout = timeout;
    }

    public async Task<GitCheckoutResult> MaterializeAsync(
        GitDeploymentInput input,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (input.AccessToken is null
            || !ProtocolValidation.IsFullCommitHash(input.TargetCommitHash)
            || !CentralRemotePolicy.IsAllowed(input.GitRepo)
            || input.AccessToken.Length == 0)
        {
            return Failure("invalid_git_input");
        }

        SafePath safePath;
        try
        {
            safePath = await pathPolicy.ValidateStackPathAsync(input.StackGitRelativePath, cancellationToken);
            try
            {
                if (!string.Equals(
                        Path.GetFullPath(input.AgentStackLocalPath),
                        safePath.FullPath,
                        StringComparison.Ordinal))
                {
                    return Failure("invalid_path");
                }
            }
            catch (ArgumentException)
            {
                return Failure("invalid_path");
            }
        }
        catch (PathPolicyException)
        {
            return Failure("invalid_path");
        }

        Directory.CreateDirectory(safePath.FullPath);
        var gitDirectory = Path.Combine(safePath.FullPath, ".git");
        if (File.Exists(gitDirectory) || Directory.Exists(gitDirectory) && new DirectoryInfo(gitDirectory).LinkTarget is not null)
        {
            return Failure("invalid_repository");
        }

        if (!Directory.Exists(gitDirectory))
        {
            var init = await RunAsync(safePath.FullPath, InitArguments, cancellationToken);
            if (init.ExitCode != 0)
            {
                return Failure("git_init_failed");
            }
        }

        var origin = await RunAsync(safePath.FullPath, ["remote", "get-url", "origin"], cancellationToken);
        if (origin.ExitCode != 0)
        {
            var addOrigin = await RunAsync(safePath.FullPath, ["remote", "add", "origin", input.GitRepo], cancellationToken);
            if (addOrigin.ExitCode != 0)
            {
                return Failure("git_remote_failed");
            }
        }
        else if (!string.Equals(origin.StandardOutput.Trim(), input.GitRepo, StringComparison.Ordinal))
        {
            return Failure("git_remote_changed");
        }

        InMemoryGitCredential? askpass = null;
        try
        {
            askpass = InMemoryGitCredential.Create(input.GitUserName, input.AccessToken);
        }
        catch (CredentialTransportUnavailableException)
        {
            return Failure("credential_transport_unavailable");
        }

        try
        {
            var environment = new Dictionary<string, string?>
            {
                ["GIT_ASKPASS"] = askpass.AskpassPath,
                ["GIT_TERMINAL_PROMPT"] = "0",
                ["GIT_CONFIG_NOSYSTEM"] = "1",
                ["GIT_CONFIG_COUNT"] = "1",
                ["GIT_CONFIG_KEY_0"] = "credential.helper",
                ["GIT_CONFIG_VALUE_0"] = string.Empty
            };
            var fetch = await RunAsync(
                safePath.FullPath,
                ["fetch", "--no-tags", "origin", input.TargetCommitHash],
                cancellationToken,
                environment);
            if (fetch.ExitCode != 0)
            {
                return Failure("git_fetch_failed");
            }

            var verify = await RunAsync(
                safePath.FullPath,
                ["cat-file", "-e", $"{input.TargetCommitHash}^{{commit}}"],
                cancellationToken,
                environment);
            if (verify.ExitCode != 0)
            {
                return Failure("invalid_commit");
            }

            var tree = await RunAsync(
                safePath.FullPath,
                ["ls-tree", "-r", input.TargetCommitHash, "--", input.StackGitRelativePath],
                cancellationToken,
                environment);
            IReadOnlyList<string> files;
            try
            {
                files = GitTreePolicy.Validate(tree.StandardOutput, input.StackGitRelativePath);
            }
            catch (GitTreePolicyException exception)
            {
                return Failure(exception.Code);
            }

            var oldFiles = ReadPreviousFiles(gitDirectory);
            RemoveManagedFiles(safePath.FullPath, oldFiles);
            var checkout = await RunAsync(
                safePath.FullPath,
                ["read-tree", "--reset", "-u", $"{input.TargetCommitHash}:{input.StackGitRelativePath}"],
                cancellationToken,
                environment);
            if (checkout.ExitCode != 0)
            {
                return Failure("git_materialize_failed");
            }

            WriteMetadata(gitDirectory, input.TargetCommitHash, input.StackGitRelativePath, files);
            return new GitCheckoutResult(true, null, files);
        }
        finally
        {
            askpass?.Dispose();
            CryptographicOperations.ZeroMemory(input.AccessToken);
        }
    }

    private async Task<ProcessResult> RunAsync(
        string workingDirectory,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken,
        IReadOnlyDictionary<string, string?>? environment = null)
    {
        return await processRunner.RunAsync(
            new ProcessRequest("git", arguments, workingDirectory, environment, timeout),
            cancellationToken);
    }

    private static IReadOnlyList<string> ReadPreviousFiles(string gitDirectory)
    {
        var metadataPath = Path.Combine(gitDirectory, "stackpivot-checkout.json");
        if (!File.Exists(metadataPath))
        {
            return Array.Empty<string>();
        }

        try
        {
            var metadata = JsonSerializer.Deserialize<CheckoutMetadata>(File.ReadAllText(metadataPath));
            return metadata?.Files ?? Array.Empty<string>();
        }
        catch (JsonException)
        {
            return Array.Empty<string>();
        }
    }

    private static void RemoveManagedFiles(string root, IReadOnlyList<string> files)
    {
        foreach (var relative in files)
        {
            if (relative.Contains("..", StringComparison.Ordinal)
                || Path.IsPathFullyQualified(relative))
            {
                continue;
            }

            var full = Path.GetFullPath(relative.Replace('/', Path.DirectorySeparatorChar), root);
            if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            {
                continue;
            }

            if (File.Exists(full))
            {
                File.Delete(full);
            }
        }
    }

    private static void WriteMetadata(
        string gitDirectory,
        string commit,
        string path,
        IReadOnlyList<string> files)
    {
        var metadata = new CheckoutMetadata(commit, path, files);
        var json = JsonSerializer.Serialize(metadata, MetadataJsonOptions);
        File.WriteAllText(Path.Combine(gitDirectory, "stackpivot-checkout.json"), json);
    }

    private static GitCheckoutResult Failure(string errorCode)
    {
        return new GitCheckoutResult(false, errorCode, Array.Empty<string>());
    }

    private sealed record CheckoutMetadata(string Commit, string Path, IReadOnlyList<string> Files);
}

public static class CentralRemotePolicy
{
    public static bool IsAllowed(string? remote)
    {
        return !string.IsNullOrWhiteSpace(remote)
            && !remote.Any(char.IsControl)
            && Uri.TryCreate(remote, UriKind.Absolute, out var uri)
            && string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            && string.IsNullOrEmpty(uri.UserInfo)
            && !string.IsNullOrWhiteSpace(uri.Host);
    }
}
