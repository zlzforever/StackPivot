using StackPivot.Agent.Execution;
using Xunit;

namespace StackPivot.Agent.Tests;

public sealed class ComposeExecutorTests
{
    private static readonly string[] ExpectedVersionArguments = ["compose", "version"];
    private static readonly string[] ExpectedUpArguments = ["compose", "up", "-d"];

    [Fact]
    public async Task ComposeV2UsesOnlyFixedUpCommand()
    {
        var runner = new RecordingProcessRunner(
            new ProcessResult(0, "Docker Compose version v2.24.0", string.Empty),
            new ProcessResult(0, "started", string.Empty));
        var executor = new ComposeExecutor(runner, TimeSpan.FromSeconds(5));

        var result = await executor.ExecuteAsync("/opt/agent-main/workspace_one/stack_web", CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(ExpectedVersionArguments, runner.Requests[0].Arguments);
        Assert.Equal(ExpectedUpArguments, runner.Requests[1].Arguments);
    }

    [Fact]
    public async Task ComposeV1IsRejectedBeforeDeploy()
    {
        var runner = new RecordingProcessRunner(
            new ProcessResult(0, "docker-compose version 1.29.2", string.Empty));
        var executor = new ComposeExecutor(runner, TimeSpan.FromSeconds(5));

        var result = await executor.ExecuteAsync("/opt/agent-main/workspace_one/stack_web", CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("compose_v2_required", result.ErrorCode);
        Assert.Single(runner.Requests);
    }

    [Fact]
    public async Task ComposeVersionTimeoutReturnsProcessTimeout()
    {
        var runner = new RecordingProcessRunner(
            new ProcessResult(-1, string.Empty, string.Empty, TimedOut: true));
        var executor = new ComposeExecutor(runner, TimeSpan.FromSeconds(5));

        var result = await executor.CheckVersionAsync(
            "/opt/agent-main/workspace_one/stack_web",
            CancellationToken.None);

        Assert.False(result.IsSupported);
        Assert.True(result.ExitCode < 0);
        Assert.Equal("process_timeout", result.ErrorCode);
    }

    [Fact]
    public async Task ComposeOutputIsLimitedToOneMebibyteAfterCombiningStreams()
    {
        var line = new string('x', LogSanitizer.MaxLineBytes);
        var runner = new RecordingProcessRunner(
            new ProcessResult(0, "Docker Compose version v2.24.0", string.Empty),
            new ProcessResult(0, string.Join('\n', Enumerable.Repeat(line, 100)), string.Empty));
        var executor = new ComposeExecutor(runner, TimeSpan.FromSeconds(5));

        var result = await executor.ExecuteAsync("/opt/agent-main/workspace_one/stack_web", CancellationToken.None);

        Assert.True(result.Success);
        Assert.True(result.LogTruncated);
        Assert.True(System.Text.Encoding.UTF8.GetByteCount(result.OutputLog) <= LogSanitizer.MaxTaskBytes);
    }

    [Fact]
    public async Task ComposeUpCallbackReceivesSanitizedStdoutAndStderr()
    {
        var runner = new StreamingProcessRunner(
            new ProcessResult(0, "Authorization: Bearer hidden\nstdout-line", "password=secret-value"));
        var executor = new ComposeExecutor(runner, TimeSpan.FromSeconds(5));
        var lines = new List<ProcessOutputLine>();

        var result = await executor.ExecuteUpAsync(
            "/opt/agent-main/workspace_one/stack_web",
            CancellationToken.None,
            line =>
            {
                lines.Add(line);
                return ValueTask.CompletedTask;
            });

        Assert.True(result.Success);
        Assert.Equal(["stdout:Authorization=[REDACTED]", "stdout:stdout-line", "stderr:password=[REDACTED]"], lines.Select(line => line.Stream + ":" + line.Text));
        Assert.DoesNotContain("hidden", lines[0].Text);
        Assert.DoesNotContain("secret-value", lines[2].Text);
    }

    internal sealed class RecordingProcessRunner(params ProcessResult[] responses) : IProcessRunner
    {
        private int responseIndex;
        public List<ProcessRequest> Requests { get; } = new();

        public Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(responses[Math.Min(responseIndex++, responses.Length - 1)]);
        }
    }

    private sealed class StreamingProcessRunner(params ProcessResult[] responses) : IProcessRunner
    {
        private int responseIndex;

        public async Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken)
        {
            var response = responses[Math.Min(responseIndex++, responses.Length - 1)];
            if (request.OutputHandler is not null && response.StandardOutput.Length > 0)
            {
                foreach (var line in response.StandardOutput.Split('\n'))
                {
                    await request.OutputHandler(new ProcessOutputLine("stdout", line));
                }
            }

            if (request.OutputHandler is not null && response.StandardError.Length > 0)
            {
                foreach (var line in response.StandardError.Split('\n'))
                {
                    await request.OutputHandler(new ProcessOutputLine("stderr", line));
                }
            }

            return response;
        }
    }
}
