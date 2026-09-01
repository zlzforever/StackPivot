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
}
