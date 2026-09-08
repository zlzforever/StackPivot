using System.Diagnostics;
using System.ComponentModel;
using System.Runtime.InteropServices;
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
    SafeDirectoryHandle? WorkingDirectoryHandle = null,
    bool ClearInheritedEnvironment = false);

public sealed record ProcessOutputLine(string Stream, string Text);

public sealed record ProcessResult(
    int ExitCode,
    string StandardOutput,
    string StandardError,
    bool TimedOut = false,
    bool OutputTruncated = false,
    string? ErrorCode = null);

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
        if (request.Timeout is { } requestedTimeout)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(requestedTimeout, TimeSpan.Zero);
        }

        var isolateProcessGroup = ShouldIsolateProcessGroup();
        using var process = new Process
        {
            StartInfo = CreateStartInfo(request)
        };
        process.Start();
        var processId = process.Id;
        var processGroupId = isolateProcessGroup
            ? TryGetIsolatedProcessGroupId(processId)
            : null;
        var budget = new OutputBudget(MaxOutputBytes);
        var stdoutTask = ReadBoundedAsync(process.StandardOutput, "stdout", budget, request.OutputHandler, CancellationToken.None);
        var stderrTask = ReadBoundedAsync(process.StandardError, "stderr", budget, request.OutputHandler, CancellationToken.None);
        var waitForExitTask = process.WaitForExitAsync(CancellationToken.None);
        var callerCancellationTask = Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        var timeoutTask = request.Timeout is { } timeout
            ? Task.Delay(timeout, CancellationToken.None)
            : null;
        var processExited = false;
        var stdoutObserved = false;
        var stderrObserved = false;
        try
        {
            while (!processExited || !stdoutObserved || !stderrObserved)
            {
                var pending = new List<Task>(capacity: 5);
                if (!processExited)
                {
                    pending.Add(waitForExitTask);
                }

                if (!stdoutObserved)
                {
                    pending.Add(stdoutTask);
                }

                if (!stderrObserved)
                {
                    pending.Add(stderrTask);
                }

                pending.Add(callerCancellationTask);
                if (timeoutTask is not null)
                {
                    pending.Add(timeoutTask);
                }

                var completed = await Task.WhenAny(pending);
                if (completed == callerCancellationTask)
                {
                    KillProcessTree(process, processGroupId);
                    await DrainAfterKillAsync(stdoutTask, stderrTask);
                    throw new OperationCanceledException(cancellationToken);
                }

                if (completed == timeoutTask)
                {
                    KillProcessTree(process, processGroupId);
                    await DrainAfterKillAsync(stdoutTask, stderrTask);
                    return new ProcessResult(-1, string.Empty, string.Empty, true, false);
                }

                if (completed == stdoutTask)
                {
                    await stdoutTask;
                    stdoutObserved = true;
                }
                else if (completed == stderrTask)
                {
                    await stderrTask;
                    stderrObserved = true;
                }
                else
                {
                    await waitForExitTask;
                    processExited = true;
                }
            }

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
            KillProcessTree(process, processGroupId);
            await DrainAfterKillAsync(stdoutTask, stderrTask);
            return new ProcessResult(-1, string.Empty, string.Empty, false, false, "output_handler_failed");
        }
        catch (OperationCanceledException)
        {
            KillProcessTree(process, processGroupId);
            await DrainAfterKillAsync(stdoutTask, stderrTask);
            throw;
        }
        catch (Exception)
        {
            KillProcessTree(process, processGroupId);
            await DrainAfterKillAsync(stdoutTask, stderrTask);
            return new ProcessResult(-1, string.Empty, string.Empty, false, false, "output_handler_failed");
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
            if (request.ClearInheritedEnvironment)
            {
                info.Environment.Clear();
            }

            foreach (var pair in request.EnvironmentVariables)
            {
                if (pair.Value is null)
                {
                    info.Environment.Remove(pair.Key);
                }
                else
                {
                    info.Environment[pair.Key] = pair.Value;
                }
            }
        }
        else if (request.ClearInheritedEnvironment)
        {
            info.Environment.Clear();
        }

        if (OperatingSystem.IsLinux()
            && TryGetSetsidPath() is { } setsidPath)
        {
            var fileName = info.FileName;
            var arguments = info.ArgumentList.ToArray();
            info.FileName = setsidPath;
            info.ArgumentList.Clear();
            info.ArgumentList.Add("--wait");
            info.ArgumentList.Add(fileName);
            foreach (var argument in arguments)
            {
                info.ArgumentList.Add(argument);
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

    private static void KillProcessTree(Process process, int? processGroupId)
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

        if (processGroupId is { } groupId && OperatingSystem.IsLinux())
        {
            try
            {
                _ = kill(-groupId, SigKill);
            }
            catch (DllNotFoundException)
            {
            }
            catch (EntryPointNotFoundException)
            {
            }
        }
    }

    private static async Task DrainAfterKillAsync(
        Task<BoundedOutput> stdoutTask,
        Task<BoundedOutput> stderrTask)
    {
        try
        {
            await Task.WhenAll(stdoutTask, stderrTask).WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (Exception)
        {
        }
    }

    private static bool ShouldIsolateProcessGroup() =>
        OperatingSystem.IsLinux() && TryGetSetsidPath() is not null;

    private static string? TryGetSetsidPath()
    {
        if (File.Exists("/usr/bin/setsid"))
        {
            return "/usr/bin/setsid";
        }

        return File.Exists("/bin/setsid") ? "/bin/setsid" : null;
    }

    private static bool TryGetProcessGroupId(int processId, out int processGroupId)
    {
        processGroupId = 0;
        if (!OperatingSystem.IsLinux())
        {
            return false;
        }

        try
        {
            processGroupId = getpgid(processId);
            return processGroupId > 0;
        }
        catch (DllNotFoundException)
        {
            return false;
        }
        catch (EntryPointNotFoundException)
        {
            return false;
        }
    }

    private static int? TryGetIsolatedProcessGroupId(int processId)
    {
        for (var attempt = 0; attempt < 50; attempt++)
        {
            if (TryGetProcessGroupId(processId, out var processGroupId)
                && processGroupId == processId)
            {
                return processGroupId;
            }

            Thread.Sleep(1);
        }

        // setsid is started directly by Process.Start. Its child cannot already be
        // a process-group leader, so its PID is the deterministic PGID even after
        // the --wait wrapper exits.
        return processId;
    }

    private const int SigKill = 9;

    #pragma warning disable CA2101
    [DllImport("libc", EntryPoint = "getpgid", SetLastError = true)]
    private static extern int getpgid(int processId);

    [DllImport("libc", EntryPoint = "kill", SetLastError = true)]
    private static extern int kill(int processId, int signal);
    #pragma warning restore CA2101

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
