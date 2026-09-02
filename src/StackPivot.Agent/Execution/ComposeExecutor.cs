using System.Text.RegularExpressions;
using StackPivot.Agent.Security;

namespace StackPivot.Agent.Execution;

public sealed record ComposeExecutionResult(
    bool Success,
    int ExitCode,
    string OutputLog,
    bool LogTruncated,
    string? ErrorCode = null);

public sealed record ComposeVersionCheck(
    bool IsSupported,
    int ExitCode,
    string OutputLog,
    bool LogTruncated,
    string? ErrorCode = null);

public sealed class ComposeExecutor
{
    private static readonly string[] ComposeVersionArguments = ["compose", "version"];
    private static readonly string[] ComposeUpArguments = ["compose", "up", "-d"];
    private readonly IProcessRunner processRunner;
    private readonly TimeSpan timeout;
    private readonly LogSanitizer sanitizer;

    public ComposeExecutor(IProcessRunner processRunner, TimeSpan timeout, LogSanitizer? sanitizer = null)
    {
        this.processRunner = processRunner ?? throw new ArgumentNullException(nameof(processRunner));
        this.timeout = timeout;
        this.sanitizer = sanitizer ?? new LogSanitizer();
    }

    public async Task<ComposeExecutionResult> ExecuteAsync(
        string workingDirectory,
        CancellationToken cancellationToken,
        SafeDirectoryHandle? workingDirectoryHandle = null)
    {
        var version = await CheckVersionAsync(workingDirectory, cancellationToken, workingDirectoryHandle);
        if (!version.IsSupported)
        {
            return new ComposeExecutionResult(false, version.ExitCode, version.OutputLog, version.LogTruncated, version.ErrorCode);
        }

        return await ExecuteUpAsync(workingDirectory, cancellationToken, workingDirectoryHandle: workingDirectoryHandle);
    }

    public async Task<ComposeVersionCheck> CheckVersionAsync(
        string workingDirectory,
        CancellationToken cancellationToken,
        SafeDirectoryHandle? workingDirectoryHandle = null)
    {
        var version = await processRunner.RunAsync(
            new ProcessRequest(
                "docker",
                ComposeVersionArguments,
                workingDirectory,
                Timeout: timeout,
                WorkingDirectoryHandle: workingDirectoryHandle),
            cancellationToken);
        var sanitized = Sanitize(version);
        if (version.TimedOut)
        {
            return new ComposeVersionCheck(
                false,
                version.ExitCode,
                sanitized.Text,
                version.OutputTruncated || sanitized.Truncated,
                "process_timeout");
        }

        if (version.ExitCode != 0 || !TryGetMajorVersion(version.StandardOutput, out var major) || major != 2)
        {
            return new ComposeVersionCheck(false, version.ExitCode, sanitized.Text, version.OutputTruncated || sanitized.Truncated, "compose_v2_required");
        }

        return new ComposeVersionCheck(true, version.ExitCode, sanitized.Text, version.OutputTruncated || sanitized.Truncated);
    }

    public async Task<ComposeExecutionResult> ExecuteUpAsync(
        string workingDirectory,
        CancellationToken cancellationToken,
        Func<ProcessOutputLine, ValueTask>? outputHandler = null,
        SafeDirectoryHandle? workingDirectoryHandle = null)
    {
        try
        {
            using var snapshot = workingDirectoryHandle is null
                ? null
                : ComposeWorkspaceSnapshot.Create(workingDirectoryHandle, cancellationToken);
            var processWorkingDirectory = snapshot?.WorkingDirectory ?? workingDirectory;
            var processWorkingDirectoryHandle = snapshot?.WorkingDirectoryHandle ?? workingDirectoryHandle;
            var environment = snapshot is null
                ? null
                : new Dictionary<string, string?>
                {
                    ["COMPOSE_FILE"] = snapshot.ComposeFileName,
                    ["COMPOSE_PROJECT_NAME"] = Path.GetFileName(workingDirectory.TrimEnd(Path.DirectorySeparatorChar))
                };
            var execution = await processRunner.RunAsync(
                new ProcessRequest(
                    "docker",
                    ComposeUpArguments,
                    processWorkingDirectory,
                    EnvironmentVariables: environment,
                    Timeout: timeout,
                    OutputHandler: outputHandler is null
                        ? null
                        : line => outputHandler(new ProcessOutputLine(line.Stream, sanitizer.Sanitize(line.Text))),
                    WorkingDirectoryHandle: processWorkingDirectoryHandle),
                cancellationToken);
            var sanitized = Sanitize(execution);
            var errorCode = execution.TimedOut ? "process_timeout" : execution.ExitCode == 0 ? null : "compose_failed";
            return new ComposeExecutionResult(
                execution.ExitCode == 0 && !execution.TimedOut,
                execution.ExitCode,
                sanitized.Text,
                execution.OutputTruncated || sanitized.Truncated,
                errorCode);
        }
        catch (PathPolicyException)
        {
            return new ComposeExecutionResult(false, -1, string.Empty, false, "compose_workspace_unavailable");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return new ComposeExecutionResult(false, -1, string.Empty, false, "compose_workspace_unavailable");
        }
    }

    private LogSanitizer.SanitizedOutput Sanitize(ProcessResult result)
    {
        var output = result.StandardOutput;
        if (!string.IsNullOrEmpty(result.StandardError))
        {
            output = string.IsNullOrEmpty(output) ? result.StandardError : output + "\n" + result.StandardError;
        }

        return sanitizer.SanitizeOutput(output);
    }

    private static bool TryGetMajorVersion(string output, out int major)
    {
        var match = Regex.Match(output, "(?i)(?:version\\s+)?v?(\\d+)\\.", RegexOptions.CultureInvariant);
        major = 0;
        return match.Success && int.TryParse(match.Groups[1].Value, out major);
    }
}
