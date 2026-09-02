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
    private readonly IReadOnlySet<string>? allowedRemoteHosts;

    public GitCheckoutExecutor(
        IProcessRunner processRunner,
        PathPolicy pathPolicy,
        TimeSpan timeout,
        IReadOnlySet<string>? allowedRemoteHosts = null)
    {
        this.processRunner = processRunner ?? throw new ArgumentNullException(nameof(processRunner));
        this.pathPolicy = pathPolicy ?? throw new ArgumentNullException(nameof(pathPolicy));
        this.timeout = timeout;
        this.allowedRemoteHosts = allowedRemoteHosts;
    }

    public async Task<GitCheckoutResult> MaterializeAsync(
        GitDeploymentInput input,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (input.AccessToken is null
            || !ProtocolValidation.IsFullCommitHash(input.TargetCommitHash)
            || !CentralRemotePolicy.IsAllowed(input.GitRepo, allowedRemoteHosts)
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

        try
        {
            safePath = await pathPolicy.ValidateStackPathAsync(input.StackGitRelativePath, cancellationToken);
        }
        catch (PathPolicyException)
        {
            return Failure("invalid_path");
        }

        Directory.CreateDirectory(safePath.FullPath);
        var gitDirectory = Path.Combine(safePath.FullPath, ".git");
        if (File.Exists(gitDirectory)
            || new FileInfo(gitDirectory).LinkTarget is not null
            || new DirectoryInfo(gitDirectory).LinkTarget is not null)
        {
            return Failure("invalid_repository");
        }

        if (!Directory.Exists(gitDirectory))
        {
            var init = await RunAsync(safePath.FullPath, InitArguments, cancellationToken);
            if (init.TimedOut)
            {
                return Failure("git_init_timeout");
            }

            if (init.ExitCode != 0)
            {
                return Failure("git_init_failed");
            }
        }

        var origin = await RunAsync(safePath.FullPath, ["remote", "get-url", "origin"], cancellationToken);
        if (origin.TimedOut)
        {
            return Failure("git_remote_timeout");
        }

        if (origin.ExitCode != 0)
        {
            var addOrigin = await RunAsync(safePath.FullPath, ["remote", "add", "origin", input.GitRepo], cancellationToken);
            if (addOrigin.TimedOut)
            {
                return Failure("git_remote_timeout");
            }

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
                ["GIT_CONFIG_NOGLOBAL"] = "1",
                ["GIT_CONFIG_COUNT"] = "2",
                ["GIT_CONFIG_KEY_0"] = "credential.helper",
                ["GIT_CONFIG_VALUE_0"] = string.Empty,
                ["GIT_CONFIG_KEY_1"] = "http.followRedirects",
                ["GIT_CONFIG_VALUE_1"] = "false"
            };
            var fetch = await RunAsync(
                safePath.FullPath,
                ["fetch", "--no-tags", "origin", input.TargetCommitHash],
                cancellationToken,
                environment);
            if (fetch.TimedOut)
            {
                return Failure("git_fetch_timeout");
            }

            if (fetch.ExitCode != 0)
            {
                return Failure("git_fetch_failed");
            }

            var verify = await RunAsync(
                safePath.FullPath,
                ["cat-file", "-e", $"{input.TargetCommitHash}^{{commit}}"],
                cancellationToken,
                environment);
            if (verify.TimedOut)
            {
                return Failure("git_verify_timeout");
            }

            if (verify.ExitCode != 0)
            {
                return Failure("invalid_commit");
            }

            var tree = await RunAsync(
                safePath.FullPath,
                ["ls-tree", "-r", input.TargetCommitHash, "--", input.StackGitRelativePath],
                cancellationToken,
                environment);
            if (tree.TimedOut)
            {
                return Failure("git_tree_timeout");
            }

            if (tree.OutputTruncated)
            {
                return Failure("git_tree_output_truncated");
            }

            if (tree.ExitCode != 0)
            {
                return Failure("git_tree_failed");
            }

            IReadOnlyList<string> files;
            try
            {
                files = GitTreePolicy.Validate(tree.StandardOutput, input.StackGitRelativePath);
            }
            catch (GitTreePolicyException exception)
            {
                return Failure(exception.Code);
            }

            IReadOnlyList<string> oldFiles;
            try
            {
                oldFiles = ReadPreviousFiles(gitDirectory);
                await RemoveManagedFilesAsync(safePath.FullPath, oldFiles, cancellationToken);
                var stackPathPolicy = new PathPolicy(safePath.FullPath);
                foreach (var file in files)
                {
                    await stackPathPolicy.ValidateManagedFilePathAsync(file, cancellationToken);
                }
            }
            catch (PathPolicyException)
            {
                return Failure("invalid_path");
            }
            var checkout = await RunAsync(
                safePath.FullPath,
                ["read-tree", "--reset", "-u", $"{input.TargetCommitHash}:{input.StackGitRelativePath}"],
                cancellationToken,
                environment);
            if (checkout.TimedOut)
            {
                return Failure("git_materialize_timeout");
            }

            if (checkout.ExitCode != 0)
            {
                return Failure("git_materialize_failed");
            }

            try
            {
                WriteMetadata(gitDirectory, input.TargetCommitHash, input.StackGitRelativePath, files);
            }
            catch (PathPolicyException)
            {
                return Failure("invalid_path");
            }
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
        if (new FileInfo(metadataPath).LinkTarget is not null
            || new DirectoryInfo(metadataPath).LinkTarget is not null)
        {
            throw new PathPolicyException("Checkout metadata must not be a symbolic link.");
        }

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

    private static async Task RemoveManagedFilesAsync(
        string root,
        IReadOnlyList<string> files,
        CancellationToken cancellationToken)
    {
        var pathPolicy = new PathPolicy(root);
        foreach (var relative in files)
        {
            var full = await pathPolicy.ValidateManagedFilePathAsync(relative, cancellationToken);

            if (Directory.Exists(full))
            {
                throw new PathPolicyException("Managed path must be a file.");
            }

            if (File.Exists(full))
            {
                var file = new FileInfo(full);
                if (file.LinkTarget is not null)
                {
                    throw new PathPolicyException("Managed file is a symbolic link.");
                }

                await pathPolicy.ValidateManagedFilePathAsync(relative, cancellationToken);
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
        var metadataPath = Path.Combine(gitDirectory, "stackpivot-checkout.json");
        if (new FileInfo(metadataPath).LinkTarget is not null
            || new DirectoryInfo(metadataPath).LinkTarget is not null)
        {
            throw new PathPolicyException("Checkout metadata must not be a symbolic link.");
        }

        var metadata = new CheckoutMetadata(commit, path, files);
        var json = JsonSerializer.Serialize(metadata, MetadataJsonOptions);
        File.WriteAllText(metadataPath, json);
    }

    private static GitCheckoutResult Failure(string errorCode)
    {
        return new GitCheckoutResult(false, errorCode, Array.Empty<string>());
    }

    private sealed record CheckoutMetadata(string Commit, string Path, IReadOnlyList<string> Files);
}

public static class CentralRemotePolicy
{
    public static bool IsAllowed(string? remote, IReadOnlySet<string>? allowedHosts)
    {
        return allowedHosts is { Count: > 0 }
            && !string.IsNullOrWhiteSpace(remote)
            && !remote.Any(character => char.IsControl(character) || char.IsWhiteSpace(character))
            && Uri.TryCreate(remote, UriKind.Absolute, out var uri)
            && string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            && string.IsNullOrEmpty(uri.UserInfo)
            && !string.IsNullOrWhiteSpace(uri.Host)
            && (allowedHosts is null || allowedHosts.Contains(uri.Host));
    }
}
