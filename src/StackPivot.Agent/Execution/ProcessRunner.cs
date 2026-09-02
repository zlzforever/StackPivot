using System.Diagnostics;
using System.ComponentModel;
using System.Text;
using StackPivot.Agent.Security;

namespace StackPivot.Agent.Execution;

public sealed record ProcessRequest(
    string FileName,
    IReadOnlyList<string> Arguments,
    string WorkingDirectory,
    IReadOnlyDictionary<string, string?>? EnvironmentVariables = null,
    TimeSpan? Timeout = null,
    Func<ProcessOutputLine, ValueTask>? OutputHandler = null,
    SafeDirectoryHandle? WorkingDirectoryHandle = null);

public sealed record ProcessOutputLine(string Stream, string Text);

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
    private const int MaxLineBytes = 16 * 1024;

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
        var budget = new OutputBudget(MaxOutputBytes);
        var stdoutTask = ReadBoundedAsync(process.StandardOutput, "stdout", budget, request.OutputHandler, waitToken);
        var stderrTask = ReadBoundedAsync(process.StandardError, "stderr", budget, request.OutputHandler, waitToken);
        try
        {
            await process.WaitForExitAsync(waitToken);
            var stdout = await stdoutTask;
            var stderr = await stderrTask;
            return new ProcessResult(
                process.ExitCode,
                stdout.Text,
                stderr.Text,
                false,
                stdout.Truncated || stderr.Truncated || budget.Truncated);
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
            WorkingDirectory = request.WorkingDirectoryHandle?.ProcessWorkingDirectory ?? request.WorkingDirectory,
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

    private static async Task<BoundedOutput> ReadBoundedAsync(
        StreamReader reader,
        string stream,
        OutputBudget budget,
        Func<ProcessOutputLine, ValueTask>? outputHandler,
        CancellationToken cancellationToken)
    {
        var output = new StringBuilder();
        var line = new StringBuilder();
        var lineBytes = 0;
        var truncated = false;
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
                    if (!budget.TryReserve(separatorBytes))
                    {
                        truncated = true;
                    }
                    else
                    {
                        if (separatorBytes != 0)
                        {
                            output.Append('\n');
                        }

                        output.Append(line);
                        if (outputHandler is not null && line.Length > 0)
                        {
                            await outputHandler(new ProcessOutputLine(stream, line.ToString()));
                        }
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
                    truncated = true;
                    continue;
                }

                line.Append(character);
                lineBytes += characterBytes;
            }
        }

        if (line.Length > 0)
        {
            var separatorBytes = output.Length == 0 ? 0 : 1;
            if (budget.TryReserve(separatorBytes))
            {
                if (separatorBytes != 0)
                {
                    output.Append('\n');
                }

                output.Append(line);
                if (outputHandler is not null)
                {
                    await outputHandler(new ProcessOutputLine(stream, line.ToString()));
                }
            }
            else
            {
                truncated = true;
            }
        }

        return new BoundedOutput(output.ToString(), truncated);
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
        catch (ArgumentException)
        {
        }
        catch (Win32Exception)
        {
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

    private sealed class OutputBudget(int limit)
    {
        private int remaining = limit;
        private int truncated;

        public bool Truncated => Volatile.Read(ref truncated) != 0;

        private void MarkTruncated()
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
}
