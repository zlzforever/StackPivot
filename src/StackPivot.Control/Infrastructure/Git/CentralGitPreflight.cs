using System.Diagnostics;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
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

public sealed record GitCommandResult(
    int ExitCode,
    string StandardOutput,
    string StandardError,
    bool TimedOut = false,
    bool OutputTruncated = false);

public interface IGitCommandRunner
{
    Task<GitCommandResult> RunAsync(
        string workingDirectory,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken);
}

public sealed class GitCommandRunner : IGitCommandRunner
{
    private const int MaxLineBytes = 16 * 1024;
    private const int MaxOutputBytes = 1024 * 1024;
    private readonly CentralGitOptions options;

    public GitCommandRunner(CentralGitOptions? options = null)
    {
        this.options = options ?? new CentralGitOptions();
    }

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
        using var timeoutCancellation = new CancellationTokenSource(options.CommandTimeout);
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCancellation.Token);
        var outputBudget = new OutputBudget(Math.Clamp(options.MaxOutputBytes, 1, MaxOutputBytes));
        var stdoutTask = ReadBoundedAsync(process.StandardOutput, outputBudget, linkedCancellation.Token);
        var stderrTask = ReadBoundedAsync(process.StandardError, outputBudget, linkedCancellation.Token);
        try
        {
            await process.WaitForExitAsync(linkedCancellation.Token);
            var stdout = await stdoutTask;
            var stderr = await stderrTask;
            return new GitCommandResult(
                process.ExitCode,
                stdout,
                stderr,
                false,
                outputBudget.Truncated);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            KillProcessTree(process);
            await DrainAfterKillAsync(stdoutTask, stderrTask);
            return new GitCommandResult(-1, string.Empty, string.Empty, true, false);
        }
        catch (OperationCanceledException)
        {
            KillProcessTree(process);
            await DrainAfterKillAsync(stdoutTask, stderrTask);
            throw;
        }
    }

    private static async Task<string> ReadBoundedAsync(
        StreamReader reader,
        OutputBudget budget,
        CancellationToken cancellationToken)
    {
        var output = new StringBuilder();
        var line = new StringBuilder();
        var lineBytes = 0;
        var buffer = new char[4096];
        int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory(), cancellationToken)) > 0)
        {
            for (var index = 0; index < count; index++)
            {
                var character = buffer[index];
                if (character == '\n')
                {
                    var separatorBytes = output.Length == 0 ? 0 : 1;
                    if (budget.TryReserve(separatorBytes))
                    {
                        if (separatorBytes != 0)
                        {
                            output.Append('\n');
                        }

                        output.Append(line);
                    }

                    line.Clear();
                    lineBytes = 0;
                    continue;
                }

                if (character == '\r')
                {
                    continue;
                }

                var characterBytes = Encoding.UTF8.GetByteCount(buffer.AsSpan(index, 1));
                if (lineBytes >= MaxLineBytes || !budget.TryReserve(characterBytes))
                {
                    budget.MarkTruncated();
                    continue;
                }

                line.Append(character);
                lineBytes += characterBytes;
            }
        }

        if (line.Length > 0 && budget.TryReserve(output.Length == 0 ? 0 : 1))
        {
            if (output.Length != 0)
            {
                output.Append('\n');
            }

            output.Append(line);
        }

        return output.ToString();
    }

    private static void KillProcessTree(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
        }
    }

    private static async Task DrainAfterKillAsync(Task<string> stdoutTask, Task<string> stderrTask)
    {
        try
        {
            await Task.WhenAll(stdoutTask, stderrTask);
        }
        catch (Exception)
        {
        }
    }

    private sealed class OutputBudget(int limit)
    {
        private int remaining = limit;
        private int truncated;

        public bool Truncated => Volatile.Read(ref truncated) != 0;

        public void MarkTruncated()
        {
            Interlocked.Exchange(ref truncated, 1);
        }

        public bool TryReserve(int bytes)
        {
            if (bytes == 0)
            {
                return true;
            }

            while (true)
            {
                var current = Volatile.Read(ref remaining);
                if (current < bytes)
                {
                    MarkTruncated();
                    return false;
                }

                if (Interlocked.CompareExchange(ref remaining, current - bytes, current) == current)
                {
                    return true;
                }
            }
        }
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
    public const string FixedMainRoot = "/opt/main";
    public const string AllowedRemoteHostsEnvironmentVariable = "STACKPIVOT_ALLOWED_REMOTE_HOSTS";
    public const string ControlAllowedRemoteHostsEnvironmentVariable = "STACKPIVOT_CONTROL_ALLOWED_REMOTE_HOSTS";

    // Kept for source compatibility; the preflight runner deliberately ignores this value.
    public string MainRoot { get; init; } = FixedMainRoot;
    public IReadOnlySet<string> AllowedRemoteHosts { get; init; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    public bool RejectSensitiveEnv { get; init; } = true;
    public TimeSpan CommandTimeout { get; init; } = TimeSpan.FromMinutes(2);
    public int MaxOutputBytes { get; init; } = 1024 * 1024;

    public static HashSet<string> ReadAllowedRemoteHosts(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var value = Environment.GetEnvironmentVariable(AllowedRemoteHostsEnvironmentVariable)
            ?? configuration[AllowedRemoteHostsEnvironmentVariable]
            ?? Environment.GetEnvironmentVariable(ControlAllowedRemoteHostsEnvironmentVariable)
            ?? configuration[ControlAllowedRemoteHostsEnvironmentVariable]
            ?? configuration["CentralGit:AllowedRemoteHosts"];
        return (value ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }
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
        if (commit.TimedOut)
        {
            throw new DeploymentValidationException("git_preflight_timeout", "Central Git preflight timed out.");
        }

        if (commit.OutputTruncated)
        {
            throw new DeploymentValidationException("git_output_truncated", "Central Git output exceeded its limit.");
        }

        if (commit.ExitCode != 0)
        {
            throw new DeploymentValidationException("invalid_commit", "Commit is not available in the central repository.");
        }

        var tree = await RunGitAsync(
            new[] { "ls-tree", "-r", "--name-only", fullCommitHash, "--", relativePath },
            cancellationToken);
        if (tree.TimedOut)
        {
            throw new DeploymentValidationException("git_preflight_timeout", "Central Git preflight timed out.");
        }

        if (tree.OutputTruncated)
        {
            throw new DeploymentValidationException("git_output_truncated", "Central Git output exceeded its limit.");
        }

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

    public static bool IsAllowedRemote(string? remote, IReadOnlySet<string>? allowedHosts)
    {
        if (allowedHosts is null || !TryGetHttpsRemoteHost(remote, out var host))
        {
            return false;
        }

        return allowedHosts.Contains(host);
    }

    private static bool TryGetHttpsRemoteHost(string? remote, out string host)
    {
        host = string.Empty;
        if (string.IsNullOrWhiteSpace(remote)
            || remote.Any(character => char.IsControl(character) || char.IsWhiteSpace(character))
            || !Uri.TryCreate(remote, UriKind.Absolute, out var uri)
            || !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || !string.IsNullOrEmpty(uri.UserInfo)
            || string.IsNullOrWhiteSpace(uri.Host))
        {
            return false;
        }

        host = uri.Host;
        return true;
    }

    private async Task<GitCommandResult> RunGitAsync(
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        try
        {
            return await gitCommandRunner.RunAsync(CentralGitOptions.FixedMainRoot, arguments, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
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
