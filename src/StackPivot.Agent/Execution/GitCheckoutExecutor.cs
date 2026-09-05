using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text;
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
    string AgentStackLocalPath,
    SafeDirectoryHandle? WorkingDirectoryHandle = null);

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
    private static readonly string[] InitArguments = ["init", "--bare"];
    private const int MaxCheckoutMetadataBytes = 1024 * 1024;
    private const int MaxCheckoutMetadataEntries = 4096;
    private const int MaxCheckoutMetadataPathBytes = 4096;
    private static readonly JsonSerializerOptions MetadataJsonOptions = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };
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
        try
        {
            if (!OperatingSystem.IsLinux())
            {
                return Failure("platform_unsupported");
            }

            if (input.AccessToken is null
                || !ProtocolValidation.IsFullCommitHash(input.TargetCommitHash)
                || !CentralRemotePolicy.IsAllowed(input.GitRepo, allowedRemoteHosts)
                || input.AccessToken.Length == 0)
            {
                return Failure("invalid_git_input");
            }

        SafePath? ownedSafePath = null;
        SafePath safePath;
        try
        {
            var validatedPath = await pathPolicy.ValidateStackPathAsync(input.StackGitRelativePath, cancellationToken);
            try
            {
                if (!string.Equals(
                        Path.GetFullPath(input.AgentStackLocalPath),
                        validatedPath.FullPath,
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
                if (input.WorkingDirectoryHandle is null)
                {
                    ownedSafePath = await pathPolicy.OpenStackPathAsync(input.StackGitRelativePath, cancellationToken);
                    safePath = ownedSafePath;
                }
                else
                {
                    if (!string.Equals(
                            input.WorkingDirectoryHandle.CanonicalPath,
                            Path.GetFullPath(input.AgentStackLocalPath),
                            StringComparison.Ordinal))
                    {
                        return Failure("invalid_path");
                    }

                    safePath = new SafePath(
                        Path.GetFullPath(input.AgentStackLocalPath),
                        input.StackGitRelativePath,
                        input.WorkingDirectoryHandle);
                }
            }
            catch (PathPolicyException)
            {
                return Failure("invalid_path");
            }

            try
            {
                return await MaterializeInDirectoryAsync(input, safePath, cancellationToken);
            }
            catch (PathPolicyException)
            {
                return Failure("invalid_path");
            }
            finally
            {
                if (ownedSafePath is not null)
                {
                    await ownedSafePath.DisposeAsync();
                }
            }
        }
        finally
        {
            if (input.AccessToken is not null)
            {
                CryptographicOperations.ZeroMemory(input.AccessToken);
            }
        }
    }

    private async Task<GitCheckoutResult> MaterializeInDirectoryAsync(
        GitDeploymentInput input,
        SafePath safePath,
        CancellationToken cancellationToken)
    {
        var directoryHandle = safePath.DirectoryHandle;
        if (directoryHandle is null)
        {
            return Failure("path_handle_unavailable");
        }

        SafeDirectoryHandle? gitDirectoryHandle = null;
        try
        {
            gitDirectoryHandle = directoryHandle.TryOpenChildDirectory(".git");
            if (gitDirectoryHandle is null)
            {
                gitDirectoryHandle = directoryHandle.OpenChildDirectory(".git", create: true);
                var init = await RunAsync(
                    safePath.FullPath,
                    InitArguments,
                    cancellationToken,
                    handle: gitDirectoryHandle);
                if (init.TimedOut)
                {
                    return Failure("git_init_timeout");
                }

                if (init.ExitCode != 0)
                {
                    return Failure("git_init_failed");
                }
            }

            if (gitDirectoryHandle is null)
            {
                return Failure("invalid_repository");
            }

            var repositoryHandle = gitDirectoryHandle!;
            using var materializationDirectory = SafeDirectoryHandle.CreateTemporaryDirectory("git-", inheritFinalHandle: true);
            var origin = await RunAsync(
                safePath.FullPath,
                RepositoryArguments(materializationDirectory.Directory.ProcessWorkingDirectory, "remote", "get-url", "origin"),
                cancellationToken,
                handle: repositoryHandle);
            if (origin.TimedOut)
            {
                return Failure("git_remote_timeout");
            }

            if (origin.ExitCode != 0)
            {
                var addOrigin = await RunAsync(
                    safePath.FullPath,
                    RepositoryArguments(materializationDirectory.Directory.ProcessWorkingDirectory, "remote", "add", "origin", input.GitRepo),
                    cancellationToken,
                    handle: repositoryHandle);
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
                    RepositoryArguments(materializationDirectory.Directory.ProcessWorkingDirectory, "fetch", "--no-tags", input.GitRepo, input.TargetCommitHash),
                    cancellationToken,
                    environment,
                    repositoryHandle);
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
                    RepositoryArguments(materializationDirectory.Directory.ProcessWorkingDirectory, "cat-file", "-e", $"{input.TargetCommitHash}^{{commit}}"),
                    cancellationToken,
                    environment,
                    repositoryHandle);
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
                    RepositoryArguments(materializationDirectory.Directory.ProcessWorkingDirectory, "ls-tree", "-r", input.TargetCommitHash, "--", input.StackGitRelativePath),
                    cancellationToken,
                    environment,
                    repositoryHandle);
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
                    oldFiles = ReadPreviousFiles(repositoryHandle, input.StackGitRelativePath);
                    foreach (var file in files)
                    {
                        directoryHandle.ValidateManagedFilePath(file);
                    }
                }
                catch (PathPolicyException)
                {
                    return Failure("invalid_path");
                }

                var checkout = await RunAsync(
                    safePath.FullPath,
                    RepositoryArguments(materializationDirectory.Directory.ProcessWorkingDirectory, "read-tree", "--reset", "-u", $"{input.TargetCommitHash}:{input.StackGitRelativePath}"),
                    cancellationToken,
                    environment,
                    repositoryHandle);
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
                    await RemoveManagedFilesAsync(directoryHandle, oldFiles, cancellationToken);
                    await CopyStagedFilesAsync(materializationDirectory.Directory, directoryHandle, files, cancellationToken);
                    WriteMetadata(
                        repositoryHandle,
                        input.TargetCommitHash,
                        input.StackGitRelativePath,
                        files);
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
            }
        }
        finally
        {
            gitDirectoryHandle?.Dispose();
        }
    }

    private static string[] RepositoryArguments(
        string workTree,
        params string[] arguments)
    {
        return ["--git-dir=.", $"--work-tree={workTree}", .. arguments];
    }

    private async Task<ProcessResult> RunAsync(
        string workingDirectory,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken,
        IReadOnlyDictionary<string, string?>? environment = null,
        SafeDirectoryHandle? handle = null)
    {
        return await processRunner.RunAsync(
            new ProcessRequest(
                "git",
                arguments,
                workingDirectory,
                EnvironmentVariables: environment,
                Timeout: timeout,
                WorkingDirectoryHandle: handle),
            cancellationToken);
    }

    private static IReadOnlyList<string> ReadPreviousFiles(
        SafeDirectoryHandle gitDirectory,
        string expectedStackPath)
    {
        try
        {
            using var file = gitDirectory.OpenFile("stackpivot-checkout.json", FileMode.Open, FileAccess.Read);
            var json = ReadMetadataJson(file);
            try
            {
                var metadata = JsonSerializer.Deserialize<CheckoutMetadata>(json, MetadataJsonOptions);
                if (metadata is null
                    || string.IsNullOrWhiteSpace(metadata.Commit)
                    || metadata.Commit.Length > 64
                    || string.IsNullOrWhiteSpace(metadata.Path)
                    || metadata.Path.Length > 200
                    || !string.Equals(metadata.Path, expectedStackPath, StringComparison.Ordinal)
                    || metadata.Files is null
                    || metadata.Files.Count > MaxCheckoutMetadataEntries)
                {
                    throw new PathPolicyException("Checkout metadata is incomplete or exceeds its limits.");
                }

                var files = new List<string>(metadata.Files.Count);
                var uniqueFiles = new HashSet<string>(StringComparer.Ordinal);
                foreach (var path in metadata.Files)
                {
                    if (string.IsNullOrWhiteSpace(path)
                        || Encoding.UTF8.GetByteCount(path) > MaxCheckoutMetadataPathBytes
                        || !uniqueFiles.Add(path))
                    {
                        throw new PathPolicyException("Checkout metadata contains an invalid file entry.");
                    }

                    files.Add(path);
                }

                return files;
            }
            catch (JsonException exception)
            {
                throw new PathPolicyException("Checkout metadata is incomplete or invalid.", exception);
            }
        }
        catch (PathPolicyException exception) when (exception.ErrorNumber == LinuxPathOperations.EntryNotFound)
        {
            return Array.Empty<string>();
        }
    }

    private static string ReadMetadataJson(FileStream file)
    {
        if (file.Length < 0 || file.Length > MaxCheckoutMetadataBytes)
        {
            throw new PathPolicyException("Checkout metadata exceeds its size limit.");
        }

        var buffer = new byte[MaxCheckoutMetadataBytes + 1];
        var total = 0;
        while (true)
        {
            var read = file.Read(buffer, total, buffer.Length - total);
            if (read == 0)
            {
                break;
            }

            total += read;
            if (total > MaxCheckoutMetadataBytes)
            {
                throw new PathPolicyException("Checkout metadata exceeds its size limit.");
            }
        }

        try
        {
            return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true)
                .GetString(buffer, 0, total);
        }
        catch (DecoderFallbackException exception)
        {
            throw new PathPolicyException("Checkout metadata is not valid UTF-8.", exception);
        }
    }

    private static Task RemoveManagedFilesAsync(
        SafeDirectoryHandle root,
        IReadOnlyList<string> files,
        CancellationToken cancellationToken)
    {
        foreach (var relative in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            root.DeleteFile(relative);
        }

        return Task.CompletedTask;
    }

    private static async Task CopyStagedFilesAsync(
        SafeDirectoryHandle source,
        SafeDirectoryHandle destination,
        IReadOnlyList<string> files,
        CancellationToken cancellationToken)
    {
        foreach (var relative in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var sourceFile = source.OpenFile(relative, FileMode.Open, FileAccess.Read);
            using var destinationFile = destination.OpenFile(relative, FileMode.Create, FileAccess.Write);
            await sourceFile.CopyToAsync(destinationFile, cancellationToken);
        }
    }

    private static void WriteMetadata(
        SafeDirectoryHandle gitDirectory,
        string commit,
        string path,
        IReadOnlyList<string> files)
    {
        var metadata = new CheckoutMetadata(commit, path, files);
        var json = JsonSerializer.Serialize(metadata, MetadataJsonOptions);
        using var file = gitDirectory.OpenFile("stackpivot-checkout.json", FileMode.Create, FileAccess.Write);
        using var writer = new StreamWriter(
            file,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            bufferSize: 4096);
        writer.Write(json);
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
            && !HasExplicitUserInfo(remote, uri)
            && string.IsNullOrEmpty(uri.Query)
            && string.IsNullOrEmpty(uri.Fragment)
            && uri.IsDefaultPort
            && !string.IsNullOrWhiteSpace(uri.Host)
            && (allowedHosts is null || allowedHosts.Contains(uri.Host));
    }

    private static bool HasExplicitUserInfo(string remote, Uri uri)
    {
        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            return true;
        }

        var schemeSeparator = remote.IndexOf("://", StringComparison.Ordinal);
        if (schemeSeparator < 0)
        {
            return false;
        }

        var authorityStart = schemeSeparator + 3;
        var authorityEnd = remote.IndexOfAny(['/','?','#'], authorityStart);
        if (authorityEnd < 0)
        {
            authorityEnd = remote.Length;
        }

        return remote[authorityStart..authorityEnd].Contains('@', StringComparison.Ordinal);
    }
}
