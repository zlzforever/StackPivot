using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using StackPivot.Control.Application.Audit;
using StackPivot.Control.Application.Deployments;
using StackPivot.Control.Authorization;
using StackPivot.Control.Domain.Entities;
using StackPivot.Control.Infrastructure.Git;
using StackPivot.Control.Infrastructure.Persistence;
using StackPivot.Control.Infrastructure.Security;
using StackPivot.Contracts.Agents;
using StackPivot.Contracts.Deployments;
using StackPivot.Contracts.SignalR;
using Xunit;

namespace StackPivot.Control.Tests;

public sealed class DeploymentEventTests
{
    [Fact]
    public async Task TaskLogsAcceptOnlyTheNextSequenceAndAreRedacted()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<StackPivotDbContext>().UseSqlite(connection).Options;
        await using var db = new StackPivotDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var fixture = await SeedAsync(db);
        var dispatcher = new DeploymentDispatcher(
            db,
            new OfflineTransport(),
            new AesGcmGitCredentialProtector(Enumerable.Range(1, 32).Select(value => (byte)value).ToArray()),
            new AuditWriter(db));

        var fixtureTask = await db.ServiceOperationHistories.SingleAsync();
        fixtureTask.DispatchedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();
        await dispatcher.HandleAcceptedAsync(CreateAccepted(fixtureTask, DateTimeOffset.UtcNow), CancellationToken.None);
        await dispatcher.HandleLogAsync(CreateLog(fixtureTask, 0, "stdout", "Authorization: Bearer hidden-token", DateTimeOffset.UtcNow), CancellationToken.None);
        await dispatcher.HandleLogAsync(CreateLog(fixtureTask, 0, "stdout", "duplicate", DateTimeOffset.UtcNow), CancellationToken.None);
        await dispatcher.HandleLogAsync(CreateLog(fixtureTask, 2, "stdout", "out-of-order", DateTimeOffset.UtcNow), CancellationToken.None);
        await dispatcher.HandleLogAsync(CreateLog(fixtureTask, 1, "stdout", "password=hunter2", DateTimeOffset.UtcNow), CancellationToken.None);

        var history = await db.ServiceOperationHistories.AsNoTracking().SingleAsync();
        Assert.Equal(1, history.LastSequence);
        Assert.DoesNotContain("hidden-token", history.OutputLog);
        Assert.DoesNotContain("hunter2", history.OutputLog);
        Assert.Contains("[REDACTED]", history.OutputLog);
        Assert.DoesNotContain("duplicate", history.OutputLog);
        Assert.DoesNotContain("out-of-order", history.OutputLog);
    }

    [Fact]
    public async Task AcceptedStreamsAndSuccessfulCompletionPersistLogsAndAuditFields()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<StackPivotDbContext>().UseSqlite(connection).Options;
        await using var db = new StackPivotDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var fixture = await SeedAsync(db);
        var history = await db.ServiceOperationHistories.SingleAsync();
        history.DispatchAttemptAt = DateTimeOffset.UtcNow;
        history.DispatchedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();
        var dispatcher = new DeploymentDispatcher(
            db,
            new OfflineTransport(),
            new AesGcmGitCredentialProtector(Enumerable.Range(1, 32).Select(value => (byte)value).ToArray()),
            new AuditWriter(db));

        await dispatcher.HandleAcceptedAsync(
            CreateAccepted(history, DateTimeOffset.UtcNow),
            CancellationToken.None);
        await dispatcher.HandleLogAsync(
            CreateLog(history, 0, "stdout", "pull complete", DateTimeOffset.UtcNow),
            CancellationToken.None);
        await dispatcher.HandleLogAsync(
            CreateLog(history, 1, "stderr", "password=\"hunter 2\"", DateTimeOffset.UtcNow),
            CancellationToken.None);
        await dispatcher.HandleCompletedAsync(
            CreateCompleted(history, DateTimeOffset.UtcNow, success: true, exitCode: 0),
            CancellationToken.None);

        history = await db.ServiceOperationHistories.AsNoTracking().SingleAsync();
        Assert.Equal("success", history.TaskStatus);
        Assert.Equal(0, history.ExitCode);
        Assert.NotNull(history.StartTime);
        Assert.NotNull(history.FinishTime);
        Assert.Contains("pull complete", history.OutputLog, StringComparison.Ordinal);
        Assert.Contains("[REDACTED]", history.OutputLog, StringComparison.Ordinal);
        Assert.DoesNotContain("hunter 2", history.OutputLog, StringComparison.Ordinal);
        var entries = JsonSerializer.Deserialize<List<DeploymentLogEntryView>>(history.OutputLogEntriesJson)
            ?? throw new InvalidOperationException("Expected structured deployment logs.");
        Assert.Equal(
            ["stdout:pull complete", "stderr:password=[REDACTED]"],
            entries.Select(entry => entry.Stream + ":" + entry.Line));

        var audits = await db.AuditLogs
            .Where(value => value.RequestId == history.RequestId)
            .OrderBy(value => value.CreatedAt)
            .ToListAsync();
        Assert.Contains(audits, audit => audit.Action == AuditActions.TaskAccepted
            && audit.AgentId == history.AgentId
            && audit.ResourceType == "task"
            && audit.ResourceId == history.TaskId.ToString()
            && audit.Result == "accepted");
        Assert.Contains(audits, audit => audit.Action == AuditActions.TaskSucceeded
            && audit.AgentId == history.AgentId
            && audit.ResourceType == "task"
            && audit.ResourceId == history.TaskId.ToString()
            && audit.Result == "success"
            && audit.ErrorCode is null);
    }

    [Fact]
    public async Task SuccessfulCompletionWithNonZeroExitCodeIsRecordedAsFailed()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<StackPivotDbContext>().UseSqlite(connection).Options;
        await using var db = new StackPivotDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var fixture = await SeedAsync(db);
        var history = await db.ServiceOperationHistories.SingleAsync();
        history.DispatchAttemptAt = DateTimeOffset.UtcNow;
        history.DispatchedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();
        var dispatcher = new DeploymentDispatcher(
            db,
            new OfflineTransport(),
            new AesGcmGitCredentialProtector(Enumerable.Range(1, 32).Select(value => (byte)value).ToArray()),
            new AuditWriter(db));

        await dispatcher.HandleAcceptedAsync(
            CreateAccepted(history, DateTimeOffset.UtcNow),
            CancellationToken.None);
        await dispatcher.HandleCompletedAsync(
            CreateCompleted(history, DateTimeOffset.UtcNow, success: true, exitCode: 17),
            CancellationToken.None);

        history = await db.ServiceOperationHistories.AsNoTracking().SingleAsync();
        Assert.Equal("failed", history.TaskStatus);
        Assert.Equal(17, history.ExitCode);
        Assert.Equal("completion_exit_code_conflict", history.ErrorCode);
        Assert.Contains(await db.AuditLogs.ToListAsync(), audit =>
            audit.Action == AuditActions.TaskFailed
            && audit.Result == "failed"
            && audit.ErrorCode == "completion_exit_code_conflict");
        Assert.DoesNotContain(await db.AuditLogs.ToListAsync(), audit => audit.Action == AuditActions.TaskSucceeded);
    }

    [Theory]
    [InlineData(false, 0, "completion_exit_code_conflict")]
    [InlineData(false, 17, "agent_error")]
    public async Task FailedCompletionAlwaysHasAStableErrorCode(
        bool success,
        int exitCode,
        string expectedErrorCode)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<StackPivotDbContext>().UseSqlite(connection).Options;
        await using var db = new StackPivotDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var fixture = await SeedAsync(db);
        var history = await db.ServiceOperationHistories.SingleAsync();
        history.DispatchAttemptAt = DateTimeOffset.UtcNow;
        history.DispatchedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();
        var dispatcher = new DeploymentDispatcher(
            db,
            new OfflineTransport(),
            new AesGcmGitCredentialProtector(Enumerable.Range(1, 32).Select(value => (byte)value).ToArray()),
            new AuditWriter(db));

        await dispatcher.HandleAcceptedAsync(
            CreateAccepted(history, DateTimeOffset.UtcNow),
            CancellationToken.None);
        await dispatcher.HandleCompletedAsync(
            CreateCompleted(history, DateTimeOffset.UtcNow, success, exitCode),
            CancellationToken.None);

        history = await db.ServiceOperationHistories.AsNoTracking().SingleAsync();
        Assert.Equal("failed", history.TaskStatus);
        Assert.Equal(expectedErrorCode, history.ErrorCode);
    }

    [Fact]
    public async Task LogsAndCompletionBeforeAcceptanceAreIgnored()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<StackPivotDbContext>().UseSqlite(connection).Options;
        await using var db = new StackPivotDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var fixture = await SeedAsync(db);
        var history = await db.ServiceOperationHistories.SingleAsync();
        history.DispatchedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();
        var dispatcher = new DeploymentDispatcher(
            db,
            new OfflineTransport(),
            new AesGcmGitCredentialProtector(Enumerable.Range(1, 32).Select(value => (byte)value).ToArray()),
            new AuditWriter(db));

        await dispatcher.HandleLogAsync(CreateLog(history, 0, "stdout", "premature", DateTimeOffset.UtcNow), CancellationToken.None);
        await dispatcher.HandleCompletedAsync(CreateCompleted(history, DateTimeOffset.UtcNow, success: true, exitCode: 0), CancellationToken.None);

        history = await db.ServiceOperationHistories.SingleAsync();
        Assert.Equal("pending", history.TaskStatus);
        Assert.Equal(-1, history.LastSequence);
        Assert.Empty(history.OutputLog);
    }

    [Fact]
    public async Task AcceptedEventIsHandledBeforeDispatchMarkerIsPersisted()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<StackPivotDbContext>().UseSqlite(connection).Options;
        await using var db = new StackPivotDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var fixture = await SeedAsync(db);
        var history = await db.ServiceOperationHistories.SingleAsync();
        history.DispatchAttemptAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();
        var dispatcher = new DeploymentDispatcher(
            db,
            new OfflineTransport(),
            new AesGcmGitCredentialProtector(Enumerable.Range(1, 32).Select(value => (byte)value).ToArray()),
            new AuditWriter(db));

        await dispatcher.HandleAcceptedAsync(
            CreateAccepted(history, DateTimeOffset.UtcNow),
            CancellationToken.None);

        history = await db.ServiceOperationHistories.AsNoTracking().SingleAsync();
        Assert.NotNull(history.StartTime);
        Assert.Null(history.DispatchedAt);
    }

    [Fact]
    public async Task DispatchSendExceptionKeepsTheTaskPendingForAConvergentAgentReply()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<StackPivotDbContext>().UseSqlite(connection).Options;
        await using var db = new StackPivotDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var fixture = await SeedAsync(db);
        await db.GlobalGitSettings.AddAsync(new GlobalGitSetting
        {
            Id = 1,
            GitRepo = "https://git.example/repository.git",
            GitUserName = "git-user",
            AccessTokenEncrypted = new AesGcmGitCredentialProtector(Enumerable.Range(1, 32).Select(value => (byte)value).ToArray()).Protect("git-token", "git-key-v1"),
            TokenKeyId = "git-key-v1"
        });
        await db.SaveChangesAsync();
        var dispatcher = new DeploymentDispatcher(
            db,
            new ThrowingTransport(),
            new AesGcmGitCredentialProtector(Enumerable.Range(1, 32).Select(value => (byte)value).ToArray()),
            new AuditWriter(db));

        await dispatcher.DispatchPendingAsync(CancellationToken.None);

        var history = await db.ServiceOperationHistories.AsNoTracking().SingleAsync();
        Assert.Equal("pending", history.TaskStatus);
        Assert.Null(history.ErrorCode);
        Assert.NotNull(history.DispatchAttemptAt);
        Assert.DoesNotContain(await db.AuditLogs.ToListAsync(), audit => audit.Action == AuditActions.TaskFailed);

        await dispatcher.HandleAcceptedAsync(
            CreateAccepted(history, DateTimeOffset.UtcNow),
            CancellationToken.None);
        await dispatcher.HandleCompletedAsync(
            CreateCompleted(history, DateTimeOffset.UtcNow, success: true, exitCode: 0),
            CancellationToken.None);

        history = await db.ServiceOperationHistories.AsNoTracking().SingleAsync();
        Assert.Equal("success", history.TaskStatus);
        Assert.Equal(0, history.ExitCode);
    }

    [Fact]
    public async Task UnknownDispatchExpiresToARecoverableErrorAndMatchingLateReportsComplete()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<StackPivotDbContext>().UseSqlite(connection).Options;
        await using var db = new StackPivotDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var fixture = await SeedAsync(db);
        var protector = new AesGcmGitCredentialProtector(Enumerable.Range(1, 32).Select(value => (byte)value).ToArray());
        await db.GlobalGitSettings.AddAsync(new GlobalGitSetting
        {
            Id = 1,
            GitRepo = "https://git.example/repository.git",
            GitUserName = "git-user",
            AccessTokenEncrypted = protector.Protect("git-token", "git-key-v1"),
            TokenKeyId = "git-key-v1"
        });
        await db.SaveChangesAsync();

        var dispatcher = new DeploymentDispatcher(
            db,
            new ThrowingTransport(),
            protector,
            new AuditWriter(db));

        await dispatcher.DispatchPendingAsync(CancellationToken.None);
        var attempted = await db.ServiceOperationHistories.SingleAsync();
        attempted.DispatchAttemptAt = DateTimeOffset.UtcNow.AddMinutes(-10);
        await db.SaveChangesAsync();

        await dispatcher.DispatchPendingAsync(CancellationToken.None);

        var uncertain = await db.ServiceOperationHistories.AsNoTracking().SingleAsync();
        Assert.Equal("failed", uncertain.TaskStatus);
        Assert.Equal("agent_dispatch_unknown", uncertain.ErrorCode);

        var acceptedAt = DateTimeOffset.UtcNow;
        await dispatcher.HandleAcceptedAsync(CreateAccepted(uncertain, acceptedAt), CancellationToken.None);
        await dispatcher.HandleCompletedAsync(
            CreateCompleted(uncertain, acceptedAt.AddSeconds(1), success: true, exitCode: 0),
            CancellationToken.None);

        var completed = await db.ServiceOperationHistories.AsNoTracking().SingleAsync();
        Assert.Equal("success", completed.TaskStatus);
        Assert.Equal(0, completed.ExitCode);
    }

    [Fact]
    public async Task TaskReportsWithAMismatchedDispatchFingerprintAreIgnored()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<StackPivotDbContext>().UseSqlite(connection).Options;
        await using var db = new StackPivotDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var fixture = await SeedAsync(db);
        var history = await db.ServiceOperationHistories.SingleAsync();
        history.DispatchedAt = DateTimeOffset.UtcNow;
        history.DispatchAttemptAt = history.DispatchedAt;
        await db.SaveChangesAsync();
        var dispatcher = new DeploymentDispatcher(
            db,
            new OfflineTransport(),
            new AesGcmGitCredentialProtector(Enumerable.Range(1, 32).Select(value => (byte)value).ToArray()),
            new AuditWriter(db));

        var accepted = CreateAccepted(history, DateTimeOffset.UtcNow) with
        {
            DispatchFingerprint = new string('0', 64)
        };

        await dispatcher.HandleAcceptedAsync(accepted, CancellationToken.None);

        history = await db.ServiceOperationHistories.AsNoTracking().SingleAsync();
        Assert.Null(history.AcceptedAt);
        Assert.Null(history.StartTime);
    }

    [Fact]
    public async Task SuccessfulSendKeepsTheDispatchMarkerWhenAuditPersistenceFails()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var persistenceFailure = new PersistenceFailureInterceptor();
        var options = new DbContextOptionsBuilder<StackPivotDbContext>()
            .UseSqlite(connection)
            .AddInterceptors(persistenceFailure)
            .Options;
        await using var db = new StackPivotDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var fixture = await SeedAsync(db);
        var protector = new AesGcmGitCredentialProtector(Enumerable.Range(1, 32).Select(value => (byte)value).ToArray());
        await db.GlobalGitSettings.AddAsync(new GlobalGitSetting
        {
            Id = 1,
            GitRepo = "https://git.example/repository.git",
            GitUserName = "git-user",
            AccessTokenEncrypted = protector.Protect("git-token", "git-key-v1"),
            TokenKeyId = "git-key-v1"
        });
        await db.SaveChangesAsync();
        persistenceFailure.Enabled = true;
        persistenceFailure.Target = db;
        var transport = new RecordingTransport();
        var dispatcher = new DeploymentDispatcher(db, transport, protector, new AuditWriter(db));

        await dispatcher.DispatchPendingAsync(CancellationToken.None);

        var history = await db.ServiceOperationHistories.AsNoTracking().SingleAsync();
        Assert.Single(transport.Commands);
        Assert.Equal("pending", history.TaskStatus);
        Assert.NotNull(history.DispatchedAt);
        Assert.Null(history.ErrorCode);
        Assert.DoesNotContain(await db.AuditLogs.ToListAsync(), audit => audit.Action == AuditActions.TaskFailed);
    }

    [Fact]
    public async Task CommittedDispatchMarkerIsNotDowngradedWhenCommitResultIsUnknown()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var commitFailure = new CommitFailureInterceptor();
        var options = new DbContextOptionsBuilder<StackPivotDbContext>()
            .UseSqlite(connection)
            .AddInterceptors(commitFailure)
            .Options;
        await using var db = new StackPivotDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var fixture = await SeedAsync(db);
        var protector = new AesGcmGitCredentialProtector(Enumerable.Range(1, 32).Select(value => (byte)value).ToArray());
        await db.GlobalGitSettings.AddAsync(new GlobalGitSetting
        {
            Id = 1,
            GitRepo = "https://git.example/repository.git",
            GitUserName = "git-user",
            AccessTokenEncrypted = protector.Protect("git-token", "git-key-v1"),
            TokenKeyId = "git-key-v1"
        });
        await db.SaveChangesAsync();
        var transport = new RecordingTransport
        {
            OnSend = () => commitFailure.Enabled = true
        };
        var dispatcher = new DeploymentDispatcher(db, transport, protector, new AuditWriter(db));

        await dispatcher.DispatchPendingAsync(CancellationToken.None);

        var history = await db.ServiceOperationHistories.AsNoTracking().SingleAsync();
        Assert.Single(transport.Commands);
        Assert.Equal("pending", history.TaskStatus);
        Assert.NotNull(history.DispatchedAt);
        Assert.Null(history.ErrorCode);
    }

    [Fact]
    public async Task AcceptedTaskIsNotDowngradedWhenDispatchMarkerPersistenceFails()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), "stackpivot-dispatch-accepted-" + Guid.NewGuid().ToString("N") + ".db");
        var connectionString = $"Data Source={databasePath};Default Timeout=5";
        var markerFailure = new DispatchMarkerFailureInterceptor();
        try
        {
            var baseOptions = new DbContextOptionsBuilder<StackPivotDbContext>()
                .UseSqlite(connectionString)
                .Options;
            var dispatchOptions = new DbContextOptionsBuilder<StackPivotDbContext>()
                .UseSqlite(connectionString)
                .AddInterceptors(markerFailure)
                .Options;
            await using (var seedDb = new StackPivotDbContext(baseOptions))
            {
                await seedDb.Database.EnsureCreatedAsync();
                var fixture = await SeedAsync(seedDb);
                var protector = new AesGcmGitCredentialProtector(Enumerable.Range(1, 32).Select(value => (byte)value).ToArray());
                seedDb.GlobalGitSettings.Add(new GlobalGitSetting
                {
                    Id = 1,
                    GitRepo = "https://git.example/repository.git",
                    GitUserName = "git-user",
                    AccessTokenEncrypted = protector.Protect("git-token", "git-key-v1"),
                    TokenKeyId = "git-key-v1"
                });
                await seedDb.SaveChangesAsync();

                await using var dispatchDb = new StackPivotDbContext(dispatchOptions);
                markerFailure.Target = dispatchDb;
                var transport = new RecordingTransport
                {
                    OnSend = () =>
                    {
                        using var acceptanceDb = new StackPivotDbContext(baseOptions);
                        var history = acceptanceDb.ServiceOperationHistories.Single();
                        var acceptedAt = DateTimeOffset.UtcNow;
                        history.AcceptedAt = acceptedAt;
                        history.StartTime = acceptedAt;
                        acceptanceDb.SaveChanges();
                        markerFailure.Enabled = true;
                    }
                };
                var dispatcher = new DeploymentDispatcher(
                    dispatchDb,
                    transport,
                    protector,
                    new AuditWriter(dispatchDb));

                await dispatcher.DispatchPendingAsync(CancellationToken.None);

                await using var verifyDb = new StackPivotDbContext(baseOptions);
                var persisted = await verifyDb.ServiceOperationHistories.AsNoTracking().SingleAsync();
                Assert.Equal("pending", persisted.TaskStatus);
                Assert.NotNull(persisted.AcceptedAt);
                Assert.NotNull(persisted.StartTime);
                Assert.Null(persisted.ErrorCode);
            }
        }
        finally
        {
            if (File.Exists(databasePath))
            {
                File.Delete(databasePath);
            }
        }
    }

    [Fact]
    public async Task AcceptedReportCommitResultUnknownConvergesFromPersistedState()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), "stackpivot-accepted-commit-" + Guid.NewGuid().ToString("N") + ".db");
        var connectionString = $"Data Source={databasePath};Default Timeout=5";
        try
        {
            var baseOptions = new DbContextOptionsBuilder<StackPivotDbContext>()
                .UseSqlite(connectionString)
                .Options;
            var commitFailure = new CommitFailureInterceptor { Enabled = true };
            var dispatchOptions = new DbContextOptionsBuilder<StackPivotDbContext>()
                .UseSqlite(connectionString)
                .AddInterceptors(commitFailure)
                .Options;
            await using (var seedDb = new StackPivotDbContext(baseOptions))
            {
                await seedDb.Database.EnsureCreatedAsync();
                await SeedAsync(seedDb);
                var history = await seedDb.ServiceOperationHistories.SingleAsync();
                history.DispatchAttemptAt = DateTimeOffset.UtcNow;
                history.DispatchedAt = history.DispatchAttemptAt;
                await seedDb.SaveChangesAsync();
            }

            await using var db = new StackPivotDbContext(dispatchOptions);
            var historyForReport = await db.ServiceOperationHistories.AsNoTracking().SingleAsync();
            var protector = new AesGcmGitCredentialProtector(Enumerable.Range(1, 32).Select(value => (byte)value).ToArray());
            var dispatcher = new DeploymentDispatcher(
                db,
                new OfflineTransport(),
                protector,
                new AuditWriter(db),
                new TestDbContextFactory(baseOptions));

            await dispatcher.HandleAcceptedAsync(
                CreateAccepted(historyForReport, DateTimeOffset.UtcNow),
                CancellationToken.None);

            await using var verifyDb = new StackPivotDbContext(baseOptions);
            var persisted = await verifyDb.ServiceOperationHistories.AsNoTracking().SingleAsync();
            Assert.Equal("pending", persisted.TaskStatus);
            Assert.NotNull(persisted.AcceptedAt);
            Assert.NotNull(persisted.StartTime);
        }
        finally
        {
            if (File.Exists(databasePath))
            {
                File.Delete(databasePath);
            }
        }
    }

    [Fact]
    public async Task CompletedReportCommitResultUnknownConvergesFromPersistedState()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), "stackpivot-completed-commit-" + Guid.NewGuid().ToString("N") + ".db");
        var connectionString = $"Data Source={databasePath};Default Timeout=5";
        try
        {
            var baseOptions = new DbContextOptionsBuilder<StackPivotDbContext>()
                .UseSqlite(connectionString)
                .Options;
            var commitFailure = new CommitFailureInterceptor { Enabled = true };
            var dispatchOptions = new DbContextOptionsBuilder<StackPivotDbContext>()
                .UseSqlite(connectionString)
                .AddInterceptors(commitFailure)
                .Options;
            await using (var seedDb = new StackPivotDbContext(baseOptions))
            {
                await seedDb.Database.EnsureCreatedAsync();
                await SeedAsync(seedDb);
                var history = await seedDb.ServiceOperationHistories.SingleAsync();
                var acceptedAt = DateTimeOffset.UtcNow;
                history.DispatchAttemptAt = acceptedAt;
                history.DispatchedAt = acceptedAt;
                history.AcceptedAt = acceptedAt;
                history.StartTime = acceptedAt;
                await seedDb.SaveChangesAsync();
            }

            await using var db = new StackPivotDbContext(dispatchOptions);
            var historyForReport = await db.ServiceOperationHistories.AsNoTracking().SingleAsync();
            var protector = new AesGcmGitCredentialProtector(Enumerable.Range(1, 32).Select(value => (byte)value).ToArray());
            var dispatcher = new DeploymentDispatcher(
                db,
                new OfflineTransport(),
                protector,
                new AuditWriter(db),
                new TestDbContextFactory(baseOptions));

            await dispatcher.HandleCompletedAsync(
                CreateCompleted(historyForReport, DateTimeOffset.UtcNow, success: true, exitCode: 0),
                CancellationToken.None);

            await using var verifyDb = new StackPivotDbContext(baseOptions);
            var persisted = await verifyDb.ServiceOperationHistories.AsNoTracking().SingleAsync();
            Assert.Equal("success", persisted.TaskStatus);
            Assert.Equal(0, persisted.ExitCode);
            Assert.Null(persisted.ErrorCode);
            Assert.NotNull(persisted.FinishTime);
        }
        finally
        {
            if (File.Exists(databasePath))
            {
                File.Delete(databasePath);
            }
        }
    }

    [Fact]
    public async Task SuccessfullyDispatchedTaskIsNotSentAgainBeforeAcceptance()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<StackPivotDbContext>().UseSqlite(connection).Options;
        await using var db = new StackPivotDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var fixture = await SeedAsync(db);
        var protector = new AesGcmGitCredentialProtector(Enumerable.Range(1, 32).Select(value => (byte)value).ToArray());
        await db.GlobalGitSettings.AddAsync(new GlobalGitSetting
        {
            Id = 1,
            GitRepo = "https://git.example/repository.git",
            GitUserName = "git-user",
            AccessTokenEncrypted = protector.Protect("git-token", "git-key-v1"),
            TokenKeyId = "git-key-v1"
        });
        await db.SaveChangesAsync();
        var transport = new RecordingTransport();
        var dispatcher = new DeploymentDispatcher(db, transport, protector, new AuditWriter(db));

        await dispatcher.DispatchPendingAsync(CancellationToken.None);
        await dispatcher.DispatchPendingAsync(CancellationToken.None);

        Assert.Single(transport.Commands);
        var history = await db.ServiceOperationHistories.AsNoTracking().SingleAsync();
        Assert.NotNull(history.DispatchedAt);
        Assert.Equal("git-key-v1", history.TokenKeyId);
    }

    [Fact]
    public async Task SuccessfulDispatchUsesPersistedSnapshotAndClearsTokenAfterSend()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<StackPivotDbContext>().UseSqlite(connection).Options;
        await using var db = new StackPivotDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var fixture = await SeedAsync(db);
        var cryptoProtector = new AesGcmGitCredentialProtector(Enumerable.Range(1, 32).Select(value => (byte)value).ToArray());
        await db.GlobalGitSettings.AddAsync(new GlobalGitSetting
        {
            Id = 1,
            GitRepo = "https://git.example/repository.git",
            GitUserName = "git-user",
            AccessTokenEncrypted = cryptoProtector.Protect("git-token", "git-key-v1"),
            TokenKeyId = "git-key-v1"
        });
        await db.SaveChangesAsync();
        var transport = new SnapshotTransport();
        var protector = new RecordingCredentialProtector(cryptoProtector);
        var dispatcher = new DeploymentDispatcher(db, transport, protector, new AuditWriter(db));

        await dispatcher.DispatchPendingAsync(CancellationToken.None);

        var history = await db.ServiceOperationHistories.AsNoTracking().SingleAsync();
        var command = transport.Command ?? throw new InvalidOperationException("Expected a deployment command.");
        Assert.Equal(history.TaskId, command.TaskId);
        Assert.Equal(history.RequestId, command.RequestId);
        Assert.Equal(history.StackId, command.StackId);
        Assert.Equal(history.AgentId, command.AgentId);
        Assert.Equal(history.TargetCommitHash, command.TargetCommitHash);
        Assert.Equal(history.GitRepoSnapshot, command.GitRepo);
        Assert.Equal(history.GitUserNameSnapshot, command.GitUserName);
        Assert.Equal(history.StackGitRelativePathSnapshot, command.StackGitRelativePath);
        Assert.Equal(history.AgentStackLocalPathSnapshot, command.AgentStackLocalPath);
        Assert.Equal(history.TokenKeyId, protector.UnprotectKeyId);
        Assert.Equal("git-token", Encoding.UTF8.GetString(transport.AccessTokenBeforeClear));
        CryptographicOperations.ZeroMemory(transport.AccessTokenBeforeClear);
        Assert.All(command.AccessToken, value => Assert.Equal(0, value));
        Assert.DoesNotContain("git-token", JsonSerializer.Serialize(history), StringComparison.Ordinal);

        var audit = Assert.Single(await db.AuditLogs
            .Where(value => value.Action == AuditActions.TaskDispatched)
            .ToListAsync());
        Assert.Equal(history.RequestId, audit.RequestId);
        Assert.Equal(history.UserId, audit.ActorUserId);
        Assert.Equal(history.AgentId, audit.AgentId);
        Assert.Equal("task", audit.ResourceType);
        Assert.Equal(history.TaskId.ToString(), audit.ResourceId);
        Assert.Equal("sent", audit.Result);
        Assert.Null(audit.ErrorCode);
    }

    [Fact]
    public async Task FailedDispatchReleasesTargetForAnExplicitlyNewDeployment()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<StackPivotDbContext>().UseSqlite(connection).Options;
        await using var db = new StackPivotDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var fixture = await SeedAsync(db);
        var protector = new AesGcmGitCredentialProtector(Enumerable.Range(1, 32).Select(value => (byte)value).ToArray());
        await db.GlobalGitSettings.AddAsync(new GlobalGitSetting
        {
            Id = 1,
            GitRepo = "https://git.example/repository.git",
            GitUserName = "git-user",
            AccessTokenEncrypted = protector.Protect("git-token", "git-key-v1"),
            TokenKeyId = "git-key-v1"
        });
        await db.SaveChangesAsync();
        var dispatcher = new DeploymentDispatcher(
            db,
            new OfflineTransport(),
            protector,
            new AuditWriter(db));

        await dispatcher.DispatchPendingAsync(CancellationToken.None);

        var failed = await db.ServiceOperationHistories.AsNoTracking().SingleAsync();
        Assert.Equal("failed", failed.TaskStatus);
        Assert.Equal("agent_offline", failed.ErrorCode);

        var service = new DeploymentService(
            db,
            new WorkspaceAuthorizationService(db),
            new RecoveryPreflight(),
            new AuditWriter(db));
        var result = await service.RequestAsync(
            failed.UserId,
            failed.StackId,
            new DeployStackRequest(
                "abcdef0123456789abcdef0123456789abcdef01",
                DeploymentMode.BoundAgents,
                null),
            Guid.NewGuid(),
            Guid.NewGuid(),
            CancellationToken.None);

        var task = Assert.Single(result.Tasks);
        Assert.Equal("pending", task.Status);
        Assert.Equal(1, await db.ServiceOperationHistories.CountAsync(value => value.TaskStatus == "pending"));
        Assert.Equal(2, await db.ServiceOperationHistories.CountAsync());
    }

    [Fact]
    public async Task TaskWithoutAConfigurationSnapshotFailsClosedBeforeDispatch()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<StackPivotDbContext>().UseSqlite(connection).Options;
        await using var db = new StackPivotDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var fixture = await SeedAsync(db);
        var protector = new AesGcmGitCredentialProtector(Enumerable.Range(1, 32).Select(value => (byte)value).ToArray());
        await db.GlobalGitSettings.AddAsync(new GlobalGitSetting
        {
            Id = 1,
            GitRepo = "https://git.example/repository.git",
            GitUserName = "git-user",
            AccessTokenEncrypted = protector.Protect("git-token", "git-key-v1"),
            TokenKeyId = "git-key-v1"
        });
        await db.SaveChangesAsync();
        var history = await db.ServiceOperationHistories.SingleAsync();
        history.GitRepoSnapshot = null;
        history.TokenKeyId = string.Empty;
        await db.SaveChangesAsync();
        var transport = new RecordingTransport();
        var dispatcher = new DeploymentDispatcher(db, transport, protector, new AuditWriter(db));

        await dispatcher.DispatchPendingAsync(CancellationToken.None);

        history = await db.ServiceOperationHistories.AsNoTracking().SingleAsync();
        Assert.Equal("failed", history.TaskStatus);
        Assert.Equal("configuration_snapshot_missing", history.ErrorCode);
        Assert.Empty(transport.Commands);
        Assert.Equal(fixture.AgentId, history.AgentId);
    }

    [Fact]
    public async Task HeartbeatUsesControlServerTimeInsteadOfAgentTimestamp()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<StackPivotDbContext>().UseSqlite(connection).Options;
        await using var db = new StackPivotDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var fixture = await SeedAsync(db);
        var dispatcher = new DeploymentDispatcher(
            db,
            new OfflineTransport(),
            new AesGcmGitCredentialProtector(Enumerable.Range(1, 32).Select(value => (byte)value).ToArray()),
            new AuditWriter(db));
        var oldTimestamp = DateTimeOffset.UtcNow.AddYears(-1);

        await dispatcher.HandleHeartbeatAsync(new HeartbeatMessage(1, fixture.AgentId, oldTimestamp), CancellationToken.None);

        var agent = await db.AgentNodes.SingleAsync(value => value.AgentId == fixture.AgentId);
        Assert.True(agent.LastSeenAt > oldTimestamp);
        Assert.True(agent.LastSeenAt >= DateTimeOffset.UtcNow.AddSeconds(-5));
    }

    [Fact]
    public void DeploymentLogTruncationDoesNotSplitUtf8Characters()
    {
        var line = new string('\u754c', DeploymentLogSanitizer.MaxLineBytes);

        var sanitized = DeploymentLogSanitizer.SanitizeLine(line);

        Assert.True(System.Text.Encoding.UTF8.GetByteCount(sanitized) <= DeploymentLogSanitizer.MaxLineBytes);
        Assert.DoesNotContain('\ufffd', sanitized);
    }

    [Fact]
    public void ControlLogSanitizerRedactsQuotedValuesWithSpaces()
    {
        var sanitized = DeploymentLogSanitizer.SanitizeLine(
            "password=\"hunter 2\" token='quoted secret' keep=ok");

        Assert.DoesNotContain("hunter 2", sanitized);
        Assert.DoesNotContain("quoted secret", sanitized);
        Assert.Contains("[REDACTED]", sanitized);
        Assert.Contains("keep=ok", sanitized);
    }

    [Fact]
    public async Task StructuredLogEntriesRemainBoundedWithBothStreamsAvailable()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<StackPivotDbContext>().UseSqlite(connection).Options;
        await using var db = new StackPivotDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var fixture = await SeedAsync(db);
        var history = await db.ServiceOperationHistories.SingleAsync();
        history.DispatchedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();
        var dispatcher = new DeploymentDispatcher(
            db,
            new OfflineTransport(),
            new AesGcmGitCredentialProtector(Enumerable.Range(1, 32).Select(value => (byte)value).ToArray()),
            new AuditWriter(db));
        await dispatcher.HandleAcceptedAsync(
            CreateAccepted(history, DateTimeOffset.UtcNow),
            CancellationToken.None);

        var line = new string('x', DeploymentLogSanitizer.MaxLineBytes);
        for (var index = 0; index < 100; index++)
        {
            await dispatcher.HandleLogAsync(
                CreateLog(
                    history,
                    index,
                    index % 2 == 0 ? "stdout" : "stderr",
                    line,
                    DateTimeOffset.UtcNow),
                CancellationToken.None);
        }

        history = await db.ServiceOperationHistories.AsNoTracking().SingleAsync();
        Assert.True(history.LogTruncated);
        Assert.True(Encoding.UTF8.GetByteCount(history.OutputLogEntriesJson) <= DeploymentLogSanitizer.MaxTaskBytes);
        var entries = JsonSerializer.Deserialize<List<DeploymentLogEntryView>>(history.OutputLogEntriesJson);
        Assert.NotNull(entries);
        Assert.Contains(entries!, entry => entry.Stream == "stdout");
        Assert.Contains(entries!, entry => entry.Stream == "stderr");
    }

    [Fact]
    public async Task DispatchLifecycleMigrationUsesAnEmptyJsonArrayAsTheLogEntryDefault()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), "stackpivot-migration-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            var options = new DbContextOptionsBuilder<StackPivotDbContext>()
                .UseSqlite($"Data Source={databasePath}")
                .Options;
            await using var db = new StackPivotDbContext(options);
            await db.Database.MigrateAsync();
            await db.Database.OpenConnectionAsync();
            await using var command = db.Database.GetDbConnection().CreateCommand();
            command.CommandText = "PRAGMA table_info('service_operation_history')";
            await using var reader = await command.ExecuteReaderAsync();
            var found = false;
            while (await reader.ReadAsync())
            {
                if (string.Equals(reader.GetString(1), "output_log_entries_json", StringComparison.Ordinal))
                {
                    found = true;
                    Assert.Equal("'[]'", reader.IsDBNull(4) ? null : reader.GetString(4));
                    break;
                }
            }

            Assert.True(found);
        }
        finally
        {
            if (File.Exists(databasePath))
            {
                File.Delete(databasePath);
            }
        }
    }

    [Fact]
    public async Task OfflinePendingTaskIsFailedWithStableErrorCode()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<StackPivotDbContext>().UseSqlite(connection).Options;
        await using var db = new StackPivotDbContext(options);
        await db.Database.EnsureCreatedAsync();
        await SeedAsync(db);
        var dispatcher = new DeploymentDispatcher(
            db,
            new OfflineTransport(),
            new AesGcmGitCredentialProtector(Enumerable.Range(1, 32).Select(value => (byte)value).ToArray()),
            new AuditWriter(db));

        await dispatcher.DispatchPendingAsync(CancellationToken.None);

        var history = await db.ServiceOperationHistories.AsNoTracking().SingleAsync();
        Assert.Equal("failed", history.TaskStatus);
        Assert.Equal("agent_offline", history.ErrorCode);
    }

    [Fact]
    public async Task AgentGoingOfflineDuringSendIsFailedWithStableOfflineErrorCode()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<StackPivotDbContext>().UseSqlite(connection).Options;
        await using var db = new StackPivotDbContext(options);
        await db.Database.EnsureCreatedAsync();
        await SeedAsync(db);
        var transport = new OfflineDuringSendTransport();
        var protector = new AesGcmGitCredentialProtector(Enumerable.Range(1, 32).Select(value => (byte)value).ToArray());
        await db.GlobalGitSettings.AddAsync(new GlobalGitSetting
        {
            Id = 1,
            GitRepo = "https://git.example/repository.git",
            GitUserName = "git-user",
            AccessTokenEncrypted = protector.Protect("git-token", "git-key-v1"),
            TokenKeyId = "git-key-v1"
        });
        await db.SaveChangesAsync();

        var dispatcher = new DeploymentDispatcher(db, transport, protector, new AuditWriter(db));

        await dispatcher.DispatchPendingAsync(CancellationToken.None);

        var history = await db.ServiceOperationHistories.AsNoTracking().SingleAsync();
        Assert.Equal("failed", history.TaskStatus);
        Assert.Equal("agent_offline", history.ErrorCode);
        Assert.Equal(1, transport.SendAttempts);
    }

    [Fact]
    public async Task AcceptedTaskCannotBeOverwrittenByConcurrentOfflineFailure()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), "stackpivot-dispatch-accepted-offline-" + Guid.NewGuid().ToString("N") + ".db");
        var connectionString = $"Data Source={databasePath};Default Timeout=5";
        try
        {
            var options = new DbContextOptionsBuilder<StackPivotDbContext>()
                .UseSqlite(connectionString)
                .Options;
            await using (var seedDb = new StackPivotDbContext(options))
            {
                await seedDb.Database.EnsureCreatedAsync();
                await SeedAsync(seedDb);
            }

            var protector = new AesGcmGitCredentialProtector(Enumerable.Range(1, 32).Select(value => (byte)value).ToArray());
            await using (var dispatchDb = new StackPivotDbContext(options))
            {
                var transport = new CallbackTransport(async (agentId, cancellationToken) =>
                {
                    await using var acceptedDb = new StackPivotDbContext(options);
                    var history = await acceptedDb.ServiceOperationHistories.SingleAsync(
                        value => value.AgentId == agentId,
                        cancellationToken);
                    var acceptedDispatcher = new DeploymentDispatcher(
                        acceptedDb,
                        new OfflineTransport(),
                        protector,
                        new AuditWriter(acceptedDb));
                    await acceptedDispatcher.HandleAcceptedAsync(
                        CreateAccepted(history, DateTimeOffset.UtcNow),
                        cancellationToken);
                });
                var dispatcher = new DeploymentDispatcher(
                    dispatchDb,
                    transport,
                    protector,
                    new AuditWriter(dispatchDb));

                await dispatcher.DispatchPendingAsync(CancellationToken.None);
            }

            await using var verifyDb = new StackPivotDbContext(options);
            var persisted = await verifyDb.ServiceOperationHistories.SingleAsync();
            Assert.Equal("pending", persisted.TaskStatus);
            Assert.Null(persisted.ErrorCode);
            Assert.NotNull(persisted.AcceptedAt);
            Assert.NotNull(persisted.StartTime);
        }
        finally
        {
            if (File.Exists(databasePath))
            {
                File.Delete(databasePath);
            }
        }
    }

    [Fact]
    public async Task DispatchedTaskWithoutAcceptanceExpiresAndIsNotSentAgain()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<StackPivotDbContext>().UseSqlite(connection).Options;
        await using var db = new StackPivotDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var fixture = await SeedAsync(db);
        var protector = new AesGcmGitCredentialProtector(Enumerable.Range(1, 32).Select(value => (byte)value).ToArray());
        await db.GlobalGitSettings.AddAsync(new GlobalGitSetting
        {
            Id = 1,
            GitRepo = "https://git.example/repository.git",
            GitUserName = "git-user",
            AccessTokenEncrypted = protector.Protect("git-token", "git-key-v1"),
            TokenKeyId = "git-key-v1"
        });
        var history = await db.ServiceOperationHistories.SingleAsync();
        history.DispatchAttemptAt = DateTimeOffset.UtcNow.AddMinutes(-10);
        history.DispatchedAt = DateTimeOffset.UtcNow.AddMinutes(-10);
        await db.SaveChangesAsync();
        var transport = new RecordingTransport();
        var dispatcher = new DeploymentDispatcher(db, transport, protector, new AuditWriter(db));

        await dispatcher.DispatchPendingAsync(CancellationToken.None);

        history = await db.ServiceOperationHistories.AsNoTracking().SingleAsync();
        Assert.Equal("failed", history.TaskStatus);
        Assert.Equal("agent_accept_timeout", history.ErrorCode);
        Assert.Empty(transport.Commands);
    }

    [Fact]
    public async Task AcceptedTaskExpiresFromStartTimeEvenWhenRecentLogsUpdatedLastEvent()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<StackPivotDbContext>().UseSqlite(connection).Options;
        await using var db = new StackPivotDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var fixture = await SeedAsync(db);
        var startedAt = DateTimeOffset.UtcNow.AddHours(-2);
        var history = await db.ServiceOperationHistories.SingleAsync();
        history.DispatchAttemptAt = startedAt;
        history.DispatchedAt = startedAt;
        history.AcceptedAt = startedAt;
        history.StartTime = startedAt;
        history.LastEventAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();

        var dispatcher = new DeploymentDispatcher(
            db,
            new OfflineTransport(),
            new AesGcmGitCredentialProtector(Enumerable.Range(1, 32).Select(value => (byte)value).ToArray()),
            new AuditWriter(db));

        await dispatcher.DispatchPendingAsync(CancellationToken.None);

        history = await db.ServiceOperationHistories.AsNoTracking().SingleAsync();
        Assert.Equal("failed", history.TaskStatus);
        Assert.Equal("agent_execution_timeout", history.ErrorCode);
        Assert.Equal(fixture.AgentId, history.AgentId);
    }

    [Fact]
    public async Task AcceptedTaskCanCompleteAfterDispatchLifetimeBeforeExecutionTimeout()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<StackPivotDbContext>().UseSqlite(connection).Options;
        await using var db = new StackPivotDbContext(options);
        await db.Database.EnsureCreatedAsync();
        await SeedAsync(db);
        var dispatchStartedAt = DateTimeOffset.UtcNow.AddMinutes(-10);
        var history = await db.ServiceOperationHistories.SingleAsync();
        history.DispatchAttemptAt = dispatchStartedAt;
        history.DispatchedAt = dispatchStartedAt;
        history.AcceptedAt = dispatchStartedAt.AddMinutes(1);
        history.StartTime = history.AcceptedAt;
        await db.SaveChangesAsync();
        var dispatcher = new DeploymentDispatcher(
            db,
            new OfflineTransport(),
            new AesGcmGitCredentialProtector(Enumerable.Range(1, 32).Select(value => (byte)value).ToArray()),
            new AuditWriter(db));

        await dispatcher.HandleCompletedAsync(
            CreateCompleted(history, DateTimeOffset.UtcNow, success: true, exitCode: 0),
            CancellationToken.None);

        history = await db.ServiceOperationHistories.AsNoTracking().SingleAsync();
        Assert.Equal("success", history.TaskStatus);
        Assert.Equal(0, history.ExitCode);
    }

    [Fact]
    public async Task AgentDisconnectKeepsAnAcceptedTaskPendingForACompletionReply()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<StackPivotDbContext>().UseSqlite(connection).Options;
        await using var db = new StackPivotDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var fixture = await SeedAsync(db);
        var history = await db.ServiceOperationHistories.SingleAsync();
        history.DispatchAttemptAt = DateTimeOffset.UtcNow;
        history.DispatchedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();
        var dispatcher = new DeploymentDispatcher(
            db,
            new OfflineTransport(),
            new AesGcmGitCredentialProtector(Enumerable.Range(1, 32).Select(value => (byte)value).ToArray()),
            new AuditWriter(db));

        await dispatcher.HandleAgentDisconnectedAsync(fixture.AgentId, CancellationToken.None);

        history = await db.ServiceOperationHistories.AsNoTracking().SingleAsync();
        Assert.Equal("pending", history.TaskStatus);
        Assert.Null(history.ErrorCode);
    }

    [Fact]
    public async Task DisconnectCannotBeOverwrittenByAnInFlightDispatch()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), "stackpivot-dispatch-" + Guid.NewGuid().ToString("N") + ".db");
        var connectionString = $"Data Source={databasePath};Default Timeout=5";
        try
        {
            var options = new DbContextOptionsBuilder<StackPivotDbContext>()
                .UseSqlite(connectionString)
                .Options;
            await using (var seedDb = new StackPivotDbContext(options))
            {
                await seedDb.Database.EnsureCreatedAsync();
                var fixture = await SeedAsync(seedDb);
                var protector = new AesGcmGitCredentialProtector(Enumerable.Range(1, 32).Select(value => (byte)value).ToArray());
                seedDb.GlobalGitSettings.Add(new GlobalGitSetting
                {
                    Id = 1,
                    GitRepo = "https://git.example/repository.git",
                    GitUserName = "git-user",
                    AccessTokenEncrypted = protector.Protect("git-token", "git-key-v1"),
                    TokenKeyId = "git-key-v1"
                });
                await seedDb.SaveChangesAsync();

                await using var dispatchDb = new StackPivotDbContext(options);
                await using var disconnectDb = new StackPivotDbContext(options);
                var transport = new BlockingTransport();
                var dispatcher = new DeploymentDispatcher(dispatchDb, transport, protector, new AuditWriter(dispatchDb));
                var disconnectDispatcher = new DeploymentDispatcher(
                    disconnectDb,
                    new OfflineTransport(),
                    protector,
                    new AuditWriter(disconnectDb));

                var dispatchTask = dispatcher.DispatchPendingAsync(CancellationToken.None);
                await transport.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
                await disconnectDispatcher.HandleAgentDisconnectedAsync(fixture.AgentId, CancellationToken.None);
                transport.Release.TrySetResult(true);
                await dispatchTask;

                await using var verifyDb = new StackPivotDbContext(options);
                var history = await verifyDb.ServiceOperationHistories.SingleAsync();
                Assert.Equal("pending", history.TaskStatus);
                Assert.NotNull(history.DispatchedAt);
            }
        }
        finally
        {
            if (File.Exists(databasePath))
            {
                File.Delete(databasePath);
            }
        }
    }

    [Fact]
    public async Task DisconnectCannotBeOverwrittenByAnInFlightCompletion()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), "stackpivot-completion-" + Guid.NewGuid().ToString("N") + ".db");
        var connectionString = $"Data Source={databasePath};Default Timeout=5";
        var barrier = new CompletionQueryBarrier();
        try
        {
            var options = new DbContextOptionsBuilder<StackPivotDbContext>()
                .UseSqlite(connectionString)
                .AddInterceptors(barrier)
                .Options;
            await using (var seedDb = new StackPivotDbContext(options))
            {
                await seedDb.Database.EnsureCreatedAsync();
                var fixture = await SeedAsync(seedDb);
                var history = await seedDb.ServiceOperationHistories.SingleAsync();
                history.DispatchAttemptAt = DateTimeOffset.UtcNow;
                history.DispatchedAt = DateTimeOffset.UtcNow;
                history.AcceptedAt = DateTimeOffset.UtcNow;
                history.StartTime = DateTimeOffset.UtcNow;
                await seedDb.SaveChangesAsync();

                await using var completionDb = new StackPivotDbContext(options);
                await using var disconnectDb = new StackPivotDbContext(options);
                barrier.Target = completionDb;
                barrier.Enabled = true;
                var completionDispatcher = new DeploymentDispatcher(
                    completionDb,
                    new OfflineTransport(),
                    new AesGcmGitCredentialProtector(Enumerable.Range(1, 32).Select(value => (byte)value).ToArray()),
                    new AuditWriter(completionDb));
                var disconnectDispatcher = new DeploymentDispatcher(
                    disconnectDb,
                    new OfflineTransport(),
                    new AesGcmGitCredentialProtector(Enumerable.Range(1, 32).Select(value => (byte)value).ToArray()),
                    new AuditWriter(disconnectDb));

                var completion = completionDispatcher.HandleCompletedAsync(
                    CreateCompleted(history, DateTimeOffset.UtcNow, success: true, exitCode: 0),
                    CancellationToken.None);
                await barrier.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
                await disconnectDispatcher.HandleAgentDisconnectedAsync(fixture.AgentId, CancellationToken.None);
                barrier.Release.TrySetResult(true);
                await completion;

                await using var verifyDb = new StackPivotDbContext(options);
                var finalHistory = await verifyDb.ServiceOperationHistories.SingleAsync();
                Assert.Equal("success", finalHistory.TaskStatus);
                Assert.Equal(0, finalHistory.ExitCode);
                Assert.Null(finalHistory.ErrorCode);
            }
        }
        finally
        {
            if (File.Exists(databasePath))
            {
                File.Delete(databasePath);
            }
        }
    }

    private static async Task<Fixture> SeedAsync(StackPivotDbContext db)
    {
        var user = new UserAccount { UserId = Guid.NewGuid(), UserName = "editor", SsoSubject = Guid.NewGuid().ToString("N") };
        var workspace = new Workspace { WorkspaceId = Guid.NewGuid(), Name = "workspace_one", DisplayName = "Workspace One" };
        var stack = new Stack { StackId = Guid.NewGuid(), WorkspaceId = workspace.WorkspaceId, FolderName = "stack_web", DisplayName = "Web" };
        var agent = new AgentNode { AgentId = Guid.NewGuid(), Name = "agent", ApiKeyHash = Guid.NewGuid().ToString("N"), ApiKeyVersion = 1 };
        var taskId = Guid.NewGuid();
        db.AddRange(user, workspace, stack, agent);
        db.AddRange(
            new WorkspaceMember { Id = Guid.NewGuid(), WorkspaceId = workspace.WorkspaceId, UserId = user.UserId, Permission = WorkspacePermission.Editor },
            new StackAgentBinding { Id = Guid.NewGuid(), StackId = stack.StackId, AgentId = agent.AgentId },
            new DeploymentRequestEntity { RequestId = Guid.NewGuid(), StackId = stack.StackId, UserId = user.UserId, IdempotencyKey = Guid.NewGuid().ToString(), RequestFingerprint = "fingerprint", TargetCommitHash = "0123456789abcdef0123456789abcdef01234567", Mode = DeploymentMode.BoundAgents, CreatedAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync();
        var request = await db.DeploymentRequests.SingleAsync();
        db.ServiceOperationHistories.Add(new ServiceOperationHistory
        {
            HistoryId = Guid.NewGuid(), TaskId = taskId, RequestId = request.RequestId, StackId = stack.StackId, AgentId = agent.AgentId, UserId = user.UserId,
            TargetCommitHash = request.TargetCommitHash, TaskStatus = "pending", CommandText = "docker compose up -d", OutputLog = string.Empty, LastSequence = -1, LastEventAt = DateTimeOffset.UtcNow,
            TokenKeyId = "git-key-v1", GitRepoSnapshot = "https://git.example/repository.git", GitUserNameSnapshot = "git-user",
            StackGitRelativePathSnapshot = "workspace_one/stack_web", AgentStackLocalPathSnapshot = "/opt/agent-main/workspace_one/stack_web"
        });
        await db.SaveChangesAsync();
        return new Fixture(taskId, agent.AgentId);
    }

    private sealed record Fixture(Guid TaskId, Guid AgentId);

    private static TaskAccepted CreateAccepted(ServiceOperationHistory history, DateTimeOffset acceptedAt)
    {
        var dispatchStartedAt = history.DispatchAttemptAt ?? history.DispatchedAt
            ?? throw new InvalidOperationException("Expected a dispatch timestamp.");
        var dispatchExpiresAt = dispatchStartedAt.AddMinutes(5);
        return new TaskAccepted(
            ProtocolVersion.Current,
            history.TaskId,
            history.AgentId,
            acceptedAt,
            DispatchFingerprint.Compute(
                history.TaskId,
                history.RequestId,
                history.StackId,
                history.AgentId,
                history.GitRepoSnapshot!,
                history.GitUserNameSnapshot!,
                history.TargetCommitHash,
                history.StackGitRelativePathSnapshot!,
                history.AgentStackLocalPathSnapshot!,
                dispatchExpiresAt),
            history.TargetCommitHash,
            history.StackGitRelativePathSnapshot!,
            history.AgentStackLocalPathSnapshot!,
            dispatchExpiresAt);
    }

    private static TaskCompleted CreateCompleted(
        ServiceOperationHistory history,
        DateTimeOffset finishedAt,
        bool success,
        int exitCode)
    {
        var accepted = CreateAccepted(history, history.StartTime ?? finishedAt);
        return new TaskCompleted(
            ProtocolVersion.Current,
            history.TaskId,
            history.AgentId,
            success,
            exitCode,
            null,
            finishedAt,
            false,
            accepted.DispatchFingerprint,
            accepted.TargetCommitHash,
            accepted.StackGitRelativePath,
            accepted.AgentStackLocalPath,
            accepted.DispatchExpiresAt);
    }

    private static TaskLog CreateLog(
        ServiceOperationHistory history,
        long sequence,
        string stream,
        string line,
        DateTimeOffset emittedAt)
    {
        var accepted = CreateAccepted(history, emittedAt);
        return new TaskLog(
            ProtocolVersion.Current,
            history.TaskId,
            history.AgentId,
            sequence,
            stream,
            line,
            emittedAt,
            accepted.DispatchFingerprint,
            accepted.TargetCommitHash,
            accepted.StackGitRelativePath,
            accepted.AgentStackLocalPath,
            accepted.DispatchExpiresAt);
    }

    private sealed class OfflineTransport : IAgentTransport
    {
        public Task<bool> IsConnectedAsync(Guid agentId, CancellationToken cancellationToken) => Task.FromResult(false);
        public Task SendDeployAsync(DeployStackCommand command, CancellationToken cancellationToken) => throw new InvalidOperationException();
    }

    private sealed class ThrowingTransport : IAgentTransport
    {
        public Task<bool> IsConnectedAsync(Guid agentId, CancellationToken cancellationToken) => Task.FromResult(true);
        public Task SendDeployAsync(DeployStackCommand command, CancellationToken cancellationToken) => throw new TimeoutException("send failed");
    }

    private sealed class OfflineDuringSendTransport : IAgentTransport
    {
        public int SendAttempts { get; private set; }

        public Task<bool> IsConnectedAsync(Guid agentId, CancellationToken cancellationToken) => Task.FromResult(true);

        public Task SendDeployAsync(DeployStackCommand command, CancellationToken cancellationToken)
        {
            SendAttempts++;
            throw new AgentOfflineException();
        }
    }

    private sealed class CallbackTransport(
        Func<Guid, CancellationToken, Task> onConnected) : IAgentTransport
    {
        public async Task<bool> IsConnectedAsync(Guid agentId, CancellationToken cancellationToken)
        {
            await onConnected(agentId, cancellationToken);
            return false;
        }

        public Task SendDeployAsync(DeployStackCommand command, CancellationToken cancellationToken) =>
            throw new InvalidOperationException();
    }

    private sealed class RecordingTransport : IAgentTransport
    {
        public List<DeployStackCommand> Commands { get; } = new();
        public Action? OnSend { get; init; }
        public Task<bool> IsConnectedAsync(Guid agentId, CancellationToken cancellationToken) => Task.FromResult(true);
        public Task SendDeployAsync(DeployStackCommand command, CancellationToken cancellationToken)
        {
            OnSend?.Invoke();
            Commands.Add(command);
            return Task.CompletedTask;
        }
    }

    private sealed class SnapshotTransport : IAgentTransport
    {
        public DeployStackCommand? Command { get; private set; }
        public byte[] AccessTokenBeforeClear { get; private set; } = Array.Empty<byte>();

        public Task<bool> IsConnectedAsync(Guid agentId, CancellationToken cancellationToken) => Task.FromResult(true);

        public Task SendDeployAsync(DeployStackCommand command, CancellationToken cancellationToken)
        {
            AccessTokenBeforeClear = command.AccessToken.ToArray();
            Command = command;
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingCredentialProtector(IGitCredentialProtector inner) : IGitCredentialProtector
    {
        public string? UnprotectKeyId { get; private set; }

        public string Protect(string token) => inner.Protect(token);

        public string Protect(string token, string keyId) => inner.Protect(token, keyId);

        public byte[] Unprotect(string encrypted, string keyId)
        {
            UnprotectKeyId = keyId;
            return inner.Unprotect(encrypted, keyId);
        }
    }

    private sealed class RecoveryPreflight : ICentralGitPreflight
    {
        public Task<DeploymentPreflight> ValidateAsync(Guid stackId, string fullCommitHash, CancellationToken cancellationToken) =>
            Task.FromResult(new DeploymentPreflight(
                "https://git.example/repository.git",
                "git-user",
                "workspace_one/stack_web",
                "/opt/agent-main/workspace_one/stack_web",
                "git-key-v1"));
    }

    private sealed class BlockingTransport : IAgentTransport
    {
        public TaskCompletionSource<bool> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<bool> IsConnectedAsync(Guid agentId, CancellationToken cancellationToken) => Task.FromResult(true);

        public async Task SendDeployAsync(DeployStackCommand command, CancellationToken cancellationToken)
        {
            Started.TrySetResult(true);
            await Release.Task.WaitAsync(cancellationToken);
        }
    }

    private sealed class CompletionQueryBarrier : DbCommandInterceptor
    {
        public TaskCompletionSource<bool> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public StackPivotDbContext? Target { get; set; }
        public bool Enabled { get; set; }
        private int blocked;

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (Enabled
                && ReferenceEquals(eventData.Context, Target)
                && command.CommandText.Contains("service_operation_history", StringComparison.OrdinalIgnoreCase)
                && Interlocked.Exchange(ref blocked, 1) == 0)
            {
                Started.TrySetResult(true);
                await Release.Task.WaitAsync(cancellationToken);
            }

            return result;
        }
    }

    private sealed class PersistenceFailureInterceptor : SaveChangesInterceptor
    {
        public StackPivotDbContext? Target { get; set; }
        public bool Enabled { get; set; }

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (Enabled && ReferenceEquals(eventData.Context, Target))
            {
                throw new InvalidOperationException("simulated audit persistence failure");
            }

            return ValueTask.FromResult(result);
        }
    }

    private sealed class CommitFailureInterceptor : DbTransactionInterceptor
    {
        public bool Enabled { get; set; }
        private int failed;

        public override Task TransactionCommittedAsync(
            DbTransaction transaction,
            TransactionEndEventData eventData,
            CancellationToken cancellationToken = default)
        {
            if (Enabled && Interlocked.Exchange(ref failed, 1) == 0)
            {
                throw new InvalidOperationException("simulated unknown commit result");
            }

            return Task.CompletedTask;
        }
    }

    private sealed class TestDbContextFactory(DbContextOptions<StackPivotDbContext> options)
        : IDbContextFactory<StackPivotDbContext>
    {
        public StackPivotDbContext CreateDbContext() => new(options);

        public Task<StackPivotDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new StackPivotDbContext(options));
    }

    private sealed class DispatchMarkerFailureInterceptor : DbCommandInterceptor
    {
        public StackPivotDbContext? Target { get; set; }
        public bool Enabled { get; set; }
        private int failed;

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (Enabled
                && ReferenceEquals(eventData.Context, Target)
                && command.CommandText.Contains("dispatched_at", StringComparison.OrdinalIgnoreCase)
                && Interlocked.Exchange(ref failed, 1) == 0)
            {
                throw new InvalidOperationException("simulated dispatch marker persistence failure");
            }

            return ValueTask.FromResult(result);
        }
    }
}
