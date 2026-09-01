using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using StackPivot.Control.Domain.Entities;
using StackPivot.Control.Infrastructure.Persistence;
using StackPivot.Contracts.SignalR;

namespace StackPivot.Control.Infrastructure.Git;

public sealed record DeploymentPreflight(
    string GitRepo,
    string GitUserName,
    string StackGitRelativePath,
    string AgentStackLocalPath,
    string TokenKeyId);

public sealed record GitCommandResult(int ExitCode, string StandardOutput, string StandardError);

public interface IGitCommandRunner
{
    Task<GitCommandResult> RunAsync(
        string workingDirectory,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken);
}

public sealed class GitCommandRunner : IGitCommandRunner
{
    public async Task<GitCommandResult> RunAsync(
        string workingDirectory,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
        ArgumentNullException.ThrowIfNull(arguments);
        using var process = new Process
        {
            StartInfo = CreateStartInfo(workingDirectory, arguments)
        };
        process.Start();
        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        return new GitCommandResult(
            process.ExitCode,
            await stdoutTask,
            await stderrTask);
    }

    private static ProcessStartInfo CreateStartInfo(string workingDirectory, IReadOnlyList<string> arguments)
    {
        var info = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        return info;
    }
}

public sealed class CentralGitOptions
{
    public string MainRoot { get; init; } = "/opt/main";
    public IReadOnlySet<string> AllowedRemoteHosts { get; init; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    public bool RejectSensitiveEnv { get; init; } = true;
}

public interface ICentralGitPreflight
{
    Task<DeploymentPreflight> ValidateAsync(
        Guid stackId,
        string fullCommitHash,
        CancellationToken cancellationToken);
}

public sealed class CentralGitPreflight(
    StackPivotDbContext dbContext,
    IGitCommandRunner gitCommandRunner,
    CentralGitOptions options) : ICentralGitPreflight
{
    public async Task<DeploymentPreflight> ValidateAsync(
        Guid stackId,
        string fullCommitHash,
        CancellationToken cancellationToken)
    {
        if (!ProtocolValidation.IsFullCommitHash(fullCommitHash))
        {
            throw new DeploymentValidationException("invalid_commit", "Commit hash must be a complete lowercase hash.");
        }

        var stack = await dbContext.Stacks
            .Include(value => value.Workspace)
            .SingleOrDefaultAsync(value => value.StackId == stackId, cancellationToken);
        if (stack?.Workspace is null
            || !ProtocolValidation.IsSafeName(stack.Workspace.Name)
            || !ProtocolValidation.IsSafeName(stack.FolderName))
        {
            throw new DeploymentValidationException("invalid_path", "Stack path is invalid.");
        }

        var setting = await dbContext.GlobalGitSettings
            .SingleOrDefaultAsync(value => value.Id == 1, cancellationToken);
        if (setting is null || !IsAllowedRemote(setting.GitRepo, options.AllowedRemoteHosts))
        {
            throw new DeploymentValidationException("policy_violation", "Git remote is not allowed.");
        }

        var relativePath = $"{stack.Workspace.Name}/{stack.FolderName}";
        var commit = await RunGitAsync(
            new[] { "cat-file", "-e", $"{fullCommitHash}^{{commit}}" },
            cancellationToken);
        if (commit.ExitCode != 0)
        {
            throw new DeploymentValidationException("invalid_commit", "Commit is not available in the central repository.");
        }

        var tree = await RunGitAsync(
            new[] { "ls-tree", "-r", "--name-only", fullCommitHash, "--", relativePath },
            cancellationToken);
        if (tree.ExitCode != 0 || !ContainsComposeFile(tree.StandardOutput, relativePath))
        {
            throw new DeploymentValidationException("invalid_path", "Commit does not contain the requested stack.");
        }

        if (options.RejectSensitiveEnv && ContainsSensitiveEnv(tree.StandardOutput, relativePath))
        {
            throw new DeploymentValidationException("policy_violation", "Sensitive .env files are not deployable.");
        }

        return new DeploymentPreflight(
            setting.GitRepo,
            setting.GitUserName,
            relativePath,
            $"/opt/agent-main/{relativePath}",
            setting.TokenKeyId);
    }

    public static bool IsAllowedRemote(string? remote, IReadOnlySet<string> allowedHosts)
    {
        ArgumentNullException.ThrowIfNull(allowedHosts);
        if (string.IsNullOrWhiteSpace(remote)
            || remote.Any(character => char.IsControl(character) || char.IsWhiteSpace(character)))
        {
            return false;
        }

        if (!Uri.TryCreate(remote, UriKind.Absolute, out var uri)
            || !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || !string.IsNullOrEmpty(uri.UserInfo)
            || string.IsNullOrWhiteSpace(uri.Host))
        {
            return false;
        }

        return allowedHosts.Contains(uri.Host);
    }

    private async Task<GitCommandResult> RunGitAsync(
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        try
        {
            return await gitCommandRunner.RunAsync(options.MainRoot, arguments, cancellationToken);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            throw new DeploymentValidationException("git_preflight_failed", "Central Git preflight failed.", exception);
        }
    }

    private static bool ContainsComposeFile(string output, string relativePath)
    {
        var prefix = relativePath + "/";
        return output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(path => path is var value
                && (value.Equals(prefix + "compose.yaml", StringComparison.Ordinal)
                    || value.Equals(prefix + "compose.yml", StringComparison.Ordinal)));
    }

    private static bool ContainsSensitiveEnv(string output, string relativePath)
    {
        var prefix = relativePath + "/";
        return output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(path => path.StartsWith(prefix, StringComparison.Ordinal))
            .Select(path => path[prefix.Length..])
            .Select(path => path[(path.LastIndexOf('/') + 1)..])
            .Any(fileName => fileName.Equals(".env", StringComparison.Ordinal)
                || fileName.StartsWith(".env.", StringComparison.Ordinal)
                || fileName.Equals(".secret.env", StringComparison.Ordinal)
                || fileName.StartsWith(".secret.env.", StringComparison.Ordinal));
    }
}

public sealed class DeploymentValidationException : Exception
{
    public DeploymentValidationException(string code, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        Code = code;
        StatusCode = 422;
    }

    public string Code { get; }
    public int StatusCode { get; }
}
