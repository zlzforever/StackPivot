using StackPivot.Agent.Security;
using StackPivot.Contracts.Deployments;
using StackPivot.Contracts.SignalR;

namespace StackPivot.Agent.Execution;

public sealed record AgentExecutionResult(
    bool Success,
    int ExitCode,
    string OutputLog,
    bool LogTruncated,
    string? ErrorCode = null);

public interface IStackExecutor
{
    Task<AgentExecutionResult> ExecuteAsync(
        DeployStackCommand command,
        CancellationToken cancellationToken);
}

public sealed record AgentLogEntry(string Stream, string Line);

public interface IStreamingStackExecutor
{
    Task<AgentExecutionResult> ExecuteAsync(
        DeployStackCommand command,
        Func<AgentLogEntry, ValueTask> logHandler,
        CancellationToken cancellationToken);
}

public sealed class StackExecutor(
    AgentOptions agentOptions,
    PathPolicy pathPolicy,
    ComposeExecutor composeExecutor,
    IGitCheckoutExecutor gitCheckoutExecutor) : IStackExecutor, IStreamingStackExecutor
{
    public async Task<AgentExecutionResult> ExecuteAsync(
        DeployStackCommand command,
        CancellationToken cancellationToken)
    {
        return await ExecuteCoreAsync(command, null, cancellationToken);
    }

    public async Task<AgentExecutionResult> ExecuteAsync(
        DeployStackCommand command,
        Func<AgentLogEntry, ValueTask> logHandler,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(logHandler);
        return await ExecuteCoreAsync(command, logHandler, cancellationToken);
    }

    private async Task<AgentExecutionResult> ExecuteCoreAsync(
        DeployStackCommand command,
        Func<AgentLogEntry, ValueTask>? logHandler,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        try
        {
            ProtocolValidation.EnsureSchemaVersion(command.SchemaVersion);
            if (command.AgentId != agentOptions.AgentId)
            {
                return Failure("agent_mismatch");
            }

            if (command.TaskId == Guid.Empty || command.RequestId == Guid.Empty || command.StackId == Guid.Empty)
            {
                return Failure("invalid_task");
            }

            if (command.AccessToken is null or { Length: 0 })
            {
                return Failure("invalid_credential");
            }

            if (command.ExpiresAt <= DateTimeOffset.UtcNow)
            {
                return Failure("command_expired");
            }

            if (!ProtocolValidation.IsFullCommitHash(command.TargetCommitHash))
            {
                return Failure("invalid_commit");
            }

            SafePath? safePath = null;
            try
            {
                safePath = await pathPolicy.OpenStackPathAsync(command.StackGitRelativePath, cancellationToken);
                if (!string.Equals(
                        Path.GetFullPath(command.AgentStackLocalPath),
                        safePath.FullPath,
                        StringComparison.Ordinal))
                {
                    return Failure("invalid_path");
                }

                var composeVersion = await composeExecutor.CheckVersionAsync(
                    safePath.FullPath,
                    cancellationToken,
                    safePath.DirectoryHandle);
                if (!composeVersion.IsSupported)
                {
                    return new AgentExecutionResult(
                        false,
                        composeVersion.ExitCode,
                        composeVersion.OutputLog,
                        composeVersion.LogTruncated,
                        composeVersion.ErrorCode);
                }

                var gitResult = await gitCheckoutExecutor.MaterializeAsync(
                    new GitDeploymentInput(
                        command.GitRepo,
                        command.GitUserName,
                        command.AccessToken,
                        command.TargetCommitHash,
                        command.StackGitRelativePath,
                        command.AgentStackLocalPath,
                        safePath.DirectoryHandle),
                    cancellationToken);
                if (!gitResult.Success)
                {
                    return Failure(gitResult.ErrorCode ?? "git_failed");
                }

                var composeResult = await composeExecutor.ExecuteUpAsync(
                    safePath.FullPath,
                    cancellationToken,
                    logHandler is null
                        ? null
                        : output => logHandler(new AgentLogEntry(output.Stream, output.Text)),
                    safePath.DirectoryHandle);
                return new AgentExecutionResult(
                    composeResult.Success,
                    composeResult.ExitCode,
                    composeResult.OutputLog,
                    composeResult.LogTruncated,
                    composeResult.ErrorCode);
            }
            catch (Exception exception) when (exception is PathPolicyException or ArgumentException or PlatformNotSupportedException)
            {
                return Failure("invalid_path");
            }
            finally
            {
                if (safePath is not null)
                {
                    await safePath.DisposeAsync();
                }
            }
        }
        catch (System.Text.Json.JsonException)
        {
            return Failure("unsupported_schema");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Failure("process_timeout");
        }
        finally
        {
            command.ClearAccessToken();
        }
    }

    private static AgentExecutionResult Failure(string errorCode)
    {
        return new AgentExecutionResult(false, -1, string.Empty, false, errorCode);
    }
}
