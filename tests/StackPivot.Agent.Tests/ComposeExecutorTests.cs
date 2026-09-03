using StackPivot.Agent.Execution;
using StackPivot.Agent.Security;
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

    [SkippableFact]
    public async Task ComposeUpRunsFromAPrivateSnapshotOfTheOpenedStackDirectory()
    {
        TestPlatform.RequireLinux();
        var root = Path.Combine(Path.GetTempPath(), "stackpivot-compose-snapshot-" + Guid.NewGuid().ToString("N"));
        var stackPath = Path.Combine(root, "workspace_one", "stack_web");
        Directory.CreateDirectory(Path.Combine(stackPath, "build"));
        File.WriteAllText(
            Path.Combine(stackPath, "compose.yaml"),
            "services:\n  app:\n    env_file: app.env\n    build:\n      context: build\n      dockerfile: Dockerfile\n");
        File.WriteAllText(Path.Combine(stackPath, "app.env"), "PORT=8080\n");
        File.WriteAllText(Path.Combine(stackPath, "build", "Dockerfile"), "FROM scratch\n");
        var policy = new PathPolicy(root);
        await using var safePath = await policy.OpenStackPathAsync("workspace_one/stack_web", CancellationToken.None);
        var runner = new RecordingProcessRunner(new ProcessResult(0, "started", string.Empty));
        var executor = new ComposeExecutor(runner, TimeSpan.FromSeconds(5));

        try
        {
            var result = await executor.ExecuteUpAsync(
                stackPath,
                CancellationToken.None,
                workingDirectoryHandle: safePath.DirectoryHandle);

            Assert.True(result.Success);
            Assert.NotEqual(stackPath, runner.Requests[0].WorkingDirectory);
            Assert.True(runner.SawComposeFileDuringUp);
            Assert.True(runner.SawReferencedFilesDuringUp);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [SkippableFact]
    public async Task ComposeUpCleansSnapshotEntriesCreatedDuringProcess()
    {
        TestPlatform.RequireLinux();
        var root = Path.Combine(Path.GetTempPath(), "stackpivot-compose-cleanup-" + Guid.NewGuid().ToString("N"));
        var outside = Path.Combine(Path.GetTempPath(), "stackpivot-compose-cleanup-outside-" + Guid.NewGuid().ToString("N"));
        var stackPath = Path.Combine(root, "workspace_one", "stack_web");
        Directory.CreateDirectory(stackPath);
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(stackPath, "compose.yaml"), "services:\n  app:\n    image: alpine\n");
        var policy = new PathPolicy(root);
        await using var safePath = await policy.OpenStackPathAsync("workspace_one/stack_web", CancellationToken.None);
        var runner = new SnapshotMutatingProcessRunner(outside);
        var executor = new ComposeExecutor(runner, TimeSpan.FromSeconds(5));

        try
        {
            var result = await executor.ExecuteUpAsync(
                stackPath,
                CancellationToken.None,
                workingDirectoryHandle: safePath.DirectoryHandle);

            Assert.True(result.Success);
            Assert.NotNull(runner.WorkingDirectory);
            Assert.False(Directory.Exists(runner.WorkingDirectory));
            Assert.True(Directory.Exists(outside));
        }
        finally
        {
            if (runner.WorkingDirectory is not null && Directory.Exists(runner.WorkingDirectory))
            {
                var link = Path.Combine(runner.WorkingDirectory, "link");
                if (File.Exists(link) || Directory.Exists(link))
                {
                    File.Delete(link);
                }

                Directory.Delete(runner.WorkingDirectory, recursive: true);
            }

            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }

            if (Directory.Exists(outside))
            {
                Directory.Delete(outside, recursive: true);
            }
        }
    }

    [SkippableTheory]
    [InlineData("build: ../outside")]
    [InlineData("build: { context: ../outside }")]
    [InlineData("build: { context: /etc }")]
    [InlineData("build:\n  context: ../outside")]
    [InlineData("build:\n  context: ${OUTSIDE_CONTEXT}")]
    [InlineData("build:\n  context: |\n    ../outside")]
    [InlineData("env_file:\n  - path: ../outside.env")]
    [InlineData("volumes:\n  - ../outside:/var/lib/app")]
    [InlineData("volumes: { type: bind, source: ../outside }")]
    [InlineData("include:\n  - project_directory: ../outside")]
    public async Task ComposeUpRejectsReferencesOutsideThePrivateSnapshot(string serviceConfiguration)
    {
        TestPlatform.RequireLinux();
        var root = Path.Combine(Path.GetTempPath(), "stackpivot-compose-path-" + Guid.NewGuid().ToString("N"));
        var stackPath = Path.Combine(root, "workspace_one", "stack_web");
        Directory.CreateDirectory(stackPath);
        File.WriteAllText(
            Path.Combine(stackPath, "compose.yaml"),
            "services:\n  app:\n    " + serviceConfiguration.Replace("\n", "\n    ", StringComparison.Ordinal) + "\n");
        var policy = new PathPolicy(root);
        await using var safePath = await policy.OpenStackPathAsync("workspace_one/stack_web", CancellationToken.None);
        var runner = new RecordingProcessRunner(new ProcessResult(0, "started", string.Empty));
        var executor = new ComposeExecutor(runner, TimeSpan.FromSeconds(5));

        try
        {
            var result = await executor.ExecuteUpAsync(
                stackPath,
                CancellationToken.None,
                workingDirectoryHandle: safePath.DirectoryHandle);

            Assert.False(result.Success);
            Assert.Equal("compose_workspace_unavailable", result.ErrorCode);
            Assert.Empty(runner.Requests);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [SkippableTheory]
    [InlineData("extends:\n  file: ../../etc/passwd\n  service: base")]
    [InlineData("extends:\n  file: /etc/passwd\n  service: base")]
    [InlineData("extends: { file: ../../etc/passwd, service: base }")]
    [InlineData("extends: { file: /etc/passwd, service: base }")]
    [InlineData("extends:\n  file: base.yaml\n  service: base\n  unknown: ../outside")]
    [InlineData("extends: { file: base.yaml, service: base, unknown: ../outside }")]
    [InlineData("build: { context: build, unknown: ../outside }")]
    public async Task ComposeUpRejectsUnsafeOrUnknownComposeReferences(string serviceConfiguration)
    {
        TestPlatform.RequireLinux();
        var root = Path.Combine(Path.GetTempPath(), "stackpivot-compose-extends-" + Guid.NewGuid().ToString("N"));
        var stackPath = Path.Combine(root, "workspace_one", "stack_web");
        Directory.CreateDirectory(stackPath);
        File.WriteAllText(
            Path.Combine(stackPath, "compose.yaml"),
            "services:\n  app:\n    image: alpine\n    " + serviceConfiguration.Replace("\n", "\n    ", StringComparison.Ordinal) + "\n");
        var policy = new PathPolicy(root);
        await using var safePath = await policy.OpenStackPathAsync("workspace_one/stack_web", CancellationToken.None);
        var runner = new RecordingProcessRunner(new ProcessResult(0, "started", string.Empty));
        var executor = new ComposeExecutor(runner, TimeSpan.FromSeconds(5));

        try
        {
            var result = await executor.ExecuteUpAsync(
                stackPath,
                CancellationToken.None,
                workingDirectoryHandle: safePath.DirectoryHandle);

            Assert.False(result.Success);
            Assert.Equal("compose_workspace_unavailable", result.ErrorCode);
            Assert.Empty(runner.Requests);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [SkippableTheory]
    [InlineData("extends: base.yaml")]
    [InlineData("extends:\n  file: base.yaml")]
    [InlineData("extends: { file: base.yaml }")]
    [InlineData("extends: { service: base, file: base.yaml, extra: value }")]
    public async Task ComposeUpRejectsMalformedOrIncompleteExtendsConfiguration(string serviceConfiguration)
    {
        TestPlatform.RequireLinux();
        var root = Path.Combine(Path.GetTempPath(), "stackpivot-compose-extends-shape-" + Guid.NewGuid().ToString("N"));
        var stackPath = Path.Combine(root, "workspace_one", "stack_web");
        Directory.CreateDirectory(stackPath);
        File.WriteAllText(
            Path.Combine(stackPath, "compose.yaml"),
            "services:\n  app:\n    image: alpine\n    " + serviceConfiguration.Replace("\n", "\n    ", StringComparison.Ordinal) + "\n");
        File.WriteAllText(Path.Combine(stackPath, "base.yaml"), "services:\n  base:\n    image: alpine\n");
        var policy = new PathPolicy(root);
        await using var safePath = await policy.OpenStackPathAsync("workspace_one/stack_web", CancellationToken.None);
        var runner = new RecordingProcessRunner(new ProcessResult(0, "started", string.Empty));
        var executor = new ComposeExecutor(runner, TimeSpan.FromSeconds(5));

        try
        {
            var result = await executor.ExecuteUpAsync(
                stackPath,
                CancellationToken.None,
                workingDirectoryHandle: safePath.DirectoryHandle);

            Assert.False(result.Success);
            Assert.Equal("compose_workspace_unavailable", result.ErrorCode);
            Assert.Empty(runner.Requests);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [SkippableFact]
    public async Task ComposeUpPreservesSafeRelativeExtendsFileInPrivateSnapshot()
    {
        TestPlatform.RequireLinux();
        var root = Path.Combine(Path.GetTempPath(), "stackpivot-compose-extends-safe-" + Guid.NewGuid().ToString("N"));
        var stackPath = Path.Combine(root, "workspace_one", "stack_web");
        const string compose = "services:\n  app:\n    image: alpine\n    extends:\n      file: base.yaml\n      service: base\n";
        const string baseCompose = "services:\n  base:\n    image: alpine\n";
        Directory.CreateDirectory(stackPath);
        File.WriteAllText(Path.Combine(stackPath, "compose.yaml"), compose);
        File.WriteAllText(Path.Combine(stackPath, "base.yaml"), baseCompose);
        var policy = new PathPolicy(root);
        await using var safePath = await policy.OpenStackPathAsync("workspace_one/stack_web", CancellationToken.None);
        var runner = new RecordingProcessRunner(new ProcessResult(0, "started", string.Empty));
        var executor = new ComposeExecutor(runner, TimeSpan.FromSeconds(5));

        try
        {
            var result = await executor.ExecuteUpAsync(
                stackPath,
                CancellationToken.None,
                workingDirectoryHandle: safePath.DirectoryHandle);

            Assert.True(result.Success);
            Assert.Equal(compose, runner.ComposeContentsDuringUp);
            Assert.Equal(baseCompose, runner.ExtendsFileContentsDuringUp);
            Assert.Equal(ExpectedUpArguments, runner.Requests.Single().Arguments);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [SkippableFact]
    public async Task ComposeUpRejectsSymlinkedExtendsFileBeforeCompose()
    {
        TestPlatform.RequireLinux();
        var root = Path.Combine(Path.GetTempPath(), "stackpivot-compose-extends-link-" + Guid.NewGuid().ToString("N"));
        var outside = Path.Combine(Path.GetTempPath(), "stackpivot-compose-extends-link-outside-" + Guid.NewGuid().ToString("N"));
        var stackPath = Path.Combine(root, "workspace_one", "stack_web");
        Directory.CreateDirectory(stackPath);
        Directory.CreateDirectory(outside);
        File.WriteAllText(
            Path.Combine(stackPath, "compose.yaml"),
            "services:\n  app:\n    extends:\n      file: base.yaml\n      service: base\n");
        var outsideCompose = Path.Combine(outside, "base.yaml");
        File.WriteAllText(outsideCompose, "services:\n  base:\n    image: alpine\n");
        File.CreateSymbolicLink(Path.Combine(stackPath, "base.yaml"), outsideCompose);
        var policy = new PathPolicy(root);
        await using var safePath = await policy.OpenStackPathAsync("workspace_one/stack_web", CancellationToken.None);
        var runner = new RecordingProcessRunner(new ProcessResult(0, "started", string.Empty));
        var executor = new ComposeExecutor(runner, TimeSpan.FromSeconds(5));

        try
        {
            var result = await executor.ExecuteUpAsync(
                stackPath,
                CancellationToken.None,
                workingDirectoryHandle: safePath.DirectoryHandle);

            Assert.False(result.Success);
            Assert.Equal("compose_workspace_unavailable", result.ErrorCode);
            Assert.Empty(runner.Requests);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }

            if (Directory.Exists(outside))
            {
                Directory.Delete(outside, recursive: true);
            }
        }
    }

    internal sealed class RecordingProcessRunner(params ProcessResult[] responses) : IProcessRunner
    {
        private int responseIndex;
        public List<ProcessRequest> Requests { get; } = new();
        public bool SawComposeFileDuringUp { get; private set; }
        public bool SawReferencedFilesDuringUp { get; private set; }
        public string? ComposeContentsDuringUp { get; private set; }
        public string? ExtendsFileContentsDuringUp { get; private set; }

        public Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            if (request.Arguments.SequenceEqual(ExpectedUpArguments))
            {
                SawComposeFileDuringUp = File.Exists(Path.Combine(request.WorkingDirectory, "compose.yaml"));
                SawReferencedFilesDuringUp = File.Exists(Path.Combine(request.WorkingDirectory, "app.env"))
                    && File.Exists(Path.Combine(request.WorkingDirectory, "build", "Dockerfile"));
                var composeFile = Path.Combine(request.WorkingDirectory, "compose.yaml");
                if (File.Exists(composeFile))
                {
                    ComposeContentsDuringUp = File.ReadAllText(composeFile);
                }

                var extendsFile = Path.Combine(request.WorkingDirectory, "base.yaml");
                if (File.Exists(extendsFile))
                {
                    ExtendsFileContentsDuringUp = File.ReadAllText(extendsFile);
                }
            }
            return Task.FromResult(responses[Math.Min(responseIndex++, responses.Length - 1)]);
        }
    }

    private sealed class SnapshotMutatingProcessRunner(string outside) : IProcessRunner
    {
        public string? WorkingDirectory { get; private set; }

        public Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken)
        {
            if (request.Arguments.SequenceEqual(ExpectedUpArguments))
            {
                WorkingDirectory = request.WorkingDirectory;
                Directory.CreateSymbolicLink(Path.Combine(request.WorkingDirectory, "link"), outside);
            }

            return Task.FromResult(new ProcessResult(0, "started", string.Empty));
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
