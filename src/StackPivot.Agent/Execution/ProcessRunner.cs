using System.Diagnostics;
using System.Text;

namespace StackPivot.Agent.Execution;

public sealed record ProcessRequest(
    string FileName,
    IReadOnlyList<string> Arguments,
    string WorkingDirectory,
    IReadOnlyDictionary<string, string?>? EnvironmentVariables = null,
    TimeSpan? Timeout = null);

public sealed record ProcessResult(
    int ExitCode,
    string StandardOutput,
    string StandardError,
    bool TimedOut = false,
    bool OutputTruncated = false);

public interface IProcessRunner
{
    Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken);
}

public sealed class ProcessRunner : IProcessRunner
{
    private const int MaxOutputBytes = 1024 * 1024;

    public async Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.FileName);
        ArgumentNullException.ThrowIfNull(request.Arguments);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.WorkingDirectory);

        using var process = new Process
        {
            StartInfo = CreateStartInfo(request)
        };
        process.Start();
        using var timeoutCancellation = request.Timeout is { } timeout
            ? new CancellationTokenSource(timeout)
            : null;
        using var linkedCancellation = timeoutCancellation is null
            ? null
            : CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCancellation.Token);
        var waitToken = linkedCancellation?.Token ?? cancellationToken;
        var stdoutTask = ReadBoundedAsync(process.StandardOutput, waitToken);
        var stderrTask = ReadBoundedAsync(process.StandardError, waitToken);
        try
        {
            await process.WaitForExitAsync(waitToken);
            var stdout = await stdoutTask;
            var stderr = await stderrTask;
            return new ProcessResult(process.ExitCode, stdout.Text, stderr.Text, false, stdout.Truncated || stderr.Truncated);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            KillProcessTree(process);
            await DrainAfterKillAsync(stdoutTask, stderrTask);
            return new ProcessResult(-1, string.Empty, string.Empty, true, false);
        }
        catch (OperationCanceledException)
        {
            KillProcessTree(process);
            await DrainAfterKillAsync(stdoutTask, stderrTask);
            throw;
        }
    }

    public static ProcessStartInfo CreateStartInfo(ProcessRequest request)
    {
        var info = new ProcessStartInfo
        {
            FileName = request.FileName,
            WorkingDirectory = request.WorkingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var argument in request.Arguments)
        {
            info.ArgumentList.Add(argument);
        }

        if (request.EnvironmentVariables is not null)
        {
            foreach (var pair in request.EnvironmentVariables)
            {
                info.Environment[pair.Key] = pair.Value;
            }
        }

        return info;
    }

    private static async Task<BoundedOutput> ReadBoundedAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        var builder = new StringBuilder();
        var truncated = false;
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            var bytes = Encoding.UTF8.GetBytes(line);
            var existingBytes = Encoding.UTF8.GetByteCount(builder.ToString());
            var remaining = MaxOutputBytes - existingBytes;
            if (remaining <= 0)
            {
                truncated = true;
                continue;
            }

            if (bytes.Length > remaining)
            {
                builder.Append(LogSanitizer.TruncateUtf8(line, remaining));
                truncated = true;
            }
            else
            {
                if (builder.Length > 0)
                {
                    builder.Append('\n');
                }

                builder.Append(line);
            }
        }

        return new BoundedOutput(builder.ToString(), truncated);
    }

    private static void KillProcessTree(Process process)
    {
        if (!process.HasExited)
        {
            process.Kill(entireProcessTree: true);
        }
    }

    private static async Task DrainAfterKillAsync(
        Task<BoundedOutput> stdoutTask,
        Task<BoundedOutput> stderrTask)
    {
        try
        {
            await Task.WhenAll(stdoutTask, stderrTask);
        }
        catch (Exception)
        {
        }
    }

    private sealed record BoundedOutput(string Text, bool Truncated);
}
