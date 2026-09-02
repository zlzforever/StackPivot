using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using StackPivot.Control.Authorization;
using StackPivot.Control.Domain.Entities;
using StackPivot.Contracts.Deployments;

namespace StackPivot.Control.Infrastructure.Persistence;

public sealed class StackPivotDbContext(DbContextOptions<StackPivotDbContext> options) : DbContext(options)
{
    public DbSet<UserAccount> Users => Set<UserAccount>();
    public DbSet<Workspace> Workspaces => Set<Workspace>();
    public DbSet<Stack> Stacks => Set<Stack>();
    public DbSet<WorkspaceMember> WorkspaceMembers => Set<WorkspaceMember>();
    public DbSet<AgentNode> AgentNodes => Set<AgentNode>();
    public DbSet<StackAgentBinding> StackAgentBindings => Set<StackAgentBinding>();
    public DbSet<GlobalGitSetting> GlobalGitSettings => Set<GlobalGitSetting>();
    public DbSet<ServiceOperationHistory> ServiceOperationHistories => Set<ServiceOperationHistory>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
    public DbSet<DeploymentRequestEntity> DeploymentRequests => Set<DeploymentRequestEntity>();

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        configurationBuilder.Properties<DateTimeOffset>().HaveConversion<DateTimeOffsetToStringConverter>();
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ConfigureUser(modelBuilder);
        ConfigureWorkspace(modelBuilder);
        ConfigureStack(modelBuilder);
        ConfigureWorkspaceMember(modelBuilder);
        ConfigureAgent(modelBuilder);
        ConfigureBinding(modelBuilder);
        ConfigureGitSetting(modelBuilder);
        ConfigureHistory(modelBuilder);
        ConfigureAudit(modelBuilder);
        ConfigureRequest(modelBuilder);
    }

    private static void ConfigureUser(ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<UserAccount>();
        entity.ToTable("user");
        entity.HasKey(value => value.UserId);
        entity.Property(value => value.UserId).HasColumnName("user_id");
        entity.Property(value => value.UserName).HasColumnName("user_name").HasMaxLength(200).IsRequired();
        entity.Property(value => value.SsoSubject).HasColumnName("sso_subject").HasMaxLength(300).IsRequired();
        entity.Property(value => value.IsPlatformAdmin).HasColumnName("is_platform_admin");
        entity.Property(value => value.CreatedAt).HasColumnName("created_at");
        entity.Property(value => value.UpdatedAt).HasColumnName("updated_at");
        entity.HasIndex(value => value.SsoSubject).IsUnique();
    }

    private static void ConfigureWorkspace(ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<Workspace>();
        entity.ToTable("workspace");
        entity.HasKey(value => value.WorkspaceId);
        entity.Property(value => value.WorkspaceId).HasColumnName("workspace_id");
        entity.Property(value => value.Name).HasColumnName("name").HasMaxLength(50).IsRequired();
        entity.Property(value => value.DisplayName).HasColumnName("display_name").HasMaxLength(200).IsRequired();
        entity.Property(value => value.CreatedAt).HasColumnName("created_at");
        entity.HasIndex(value => value.Name).IsUnique();
    }

    private static void ConfigureStack(ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<Stack>();
        entity.ToTable("stack");
        entity.HasKey(value => value.StackId);
        entity.Property(value => value.StackId).HasColumnName("stack_id");
        entity.Property(value => value.WorkspaceId).HasColumnName("workspace_id");
        entity.Property(value => value.FolderName).HasColumnName("folder_name").HasMaxLength(50).IsRequired();
        entity.Property(value => value.DisplayName).HasColumnName("display_name").HasMaxLength(200).IsRequired();
        entity.Property(value => value.CreatedAt).HasColumnName("created_at");
        entity.HasIndex(value => new { value.WorkspaceId, value.FolderName }).IsUnique();
        entity.HasOne(value => value.Workspace)
            .WithMany(value => value.Stacks)
            .HasForeignKey(value => value.WorkspaceId)
            .OnDelete(DeleteBehavior.Cascade);
    }

    private static void ConfigureWorkspaceMember(ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<WorkspaceMember>();
        entity.ToTable("workspace_member");
        entity.HasKey(value => value.Id);
        entity.Property(value => value.Id).HasColumnName("id");
        entity.Property(value => value.WorkspaceId).HasColumnName("workspace_id");
        entity.Property(value => value.UserId).HasColumnName("user_id");
        entity.Property(value => value.Permission)
            .HasColumnName("permission")
            .HasConversion(
                value => value == WorkspacePermission.Editor
                    ? "editor"
                    : value == WorkspacePermission.PlatformAdmin ? "platform_admin" : "readonly",
                value => value == "editor"
                    ? WorkspacePermission.Editor
                    : value == "platform_admin" ? WorkspacePermission.PlatformAdmin : WorkspacePermission.ReadOnly)
            .HasMaxLength(20);
        entity.Property(value => value.CreatedAt).HasColumnName("created_at");
        entity.HasIndex(value => new { value.WorkspaceId, value.UserId }).IsUnique();
        entity.HasOne(value => value.Workspace)
            .WithMany(value => value.Members)
            .HasForeignKey(value => value.WorkspaceId)
            .OnDelete(DeleteBehavior.Cascade);
        entity.HasOne(value => value.User)
            .WithMany(value => value.WorkspaceMembers)
            .HasForeignKey(value => value.UserId)
            .OnDelete(DeleteBehavior.Cascade);
    }

    private static void ConfigureAgent(ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<AgentNode>();
        entity.ToTable("agent_node");
        entity.HasKey(value => value.AgentId);
        entity.Property(value => value.AgentId).HasColumnName("agent_id");
        entity.Property(value => value.Name).HasColumnName("name").HasMaxLength(100).IsRequired();
        entity.Property(value => value.Remark).HasColumnName("remark").HasMaxLength(500).IsRequired();
        entity.Property(value => value.ApiKeyHash).HasColumnName("api_key_hash").HasMaxLength(100).IsRequired();
        entity.Property(value => value.ApiKeyVersion).HasColumnName("api_key_version").IsConcurrencyToken();
        entity.Property(value => value.ApiKeyLast4).HasColumnName("api_key_last4").HasMaxLength(4);
        entity.Property(value => value.RevokedAt).HasColumnName("revoked_at");
        entity.Property(value => value.LastSeenAt).HasColumnName("last_seen_at");
        entity.Property(value => value.CapabilitiesJson).HasColumnName("capabilities_json").IsRequired();
        entity.HasIndex(value => value.ApiKeyHash).IsUnique();
    }

    private static void ConfigureBinding(ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<StackAgentBinding>();
        entity.ToTable("stack_agent_binding");
        entity.HasKey(value => value.Id);
        entity.Property(value => value.Id).HasColumnName("id");
        entity.Property(value => value.StackId).HasColumnName("stack_id");
        entity.Property(value => value.AgentId).HasColumnName("agent_id");
        entity.Property(value => value.CreatedAt).HasColumnName("created_at");
        entity.HasIndex(value => new { value.StackId, value.AgentId }).IsUnique();
        entity.HasOne(value => value.Stack)
            .WithMany(value => value.AgentBindings)
            .HasForeignKey(value => value.StackId)
            .OnDelete(DeleteBehavior.Cascade);
        entity.HasOne(value => value.Agent)
            .WithMany(value => value.StackBindings)
            .HasForeignKey(value => value.AgentId)
            .OnDelete(DeleteBehavior.Cascade);
    }

    private static void ConfigureGitSetting(ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<GlobalGitSetting>();
        entity.ToTable("global_git_setting");
        entity.HasKey(value => value.Id);
        entity.Property(value => value.Id).HasColumnName("id");
        entity.Property(value => value.GitRepo).HasColumnName("git_repo").HasMaxLength(1000).IsRequired();
        entity.Property(value => value.GitUserName).HasColumnName("git_user_name").HasMaxLength(200).IsRequired();
        entity.Property(value => value.AccessTokenEncrypted).HasColumnName("access_token_encrypted").IsRequired();
        entity.Property(value => value.TokenKeyId).HasColumnName("token_key_id").HasMaxLength(100).IsRequired();
        entity.Property(value => value.UpdatedAt).HasColumnName("updated_at");
    }

    private static void ConfigureHistory(ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<ServiceOperationHistory>();
        entity.ToTable("service_operation_history");
        entity.HasKey(value => value.HistoryId);
        entity.Property(value => value.HistoryId).HasColumnName("history_id");
        entity.Property(value => value.TaskId).HasColumnName("task_id");
        entity.Property(value => value.RequestId).HasColumnName("request_id");
        entity.Property(value => value.StackId).HasColumnName("stack_id");
        entity.Property(value => value.AgentId).HasColumnName("agent_id");
        entity.Property(value => value.UserId).HasColumnName("user_id");
        entity.Property(value => value.OperationType).HasColumnName("operation_type").HasMaxLength(50).IsRequired();
        entity.Property(value => value.TargetCommitHash).HasColumnName("target_commit_hash").HasMaxLength(64).IsRequired();
        entity.Property(value => value.TaskStatus).HasColumnName("task_status").HasMaxLength(20).IsRequired();
        entity.Property(value => value.CommandText).HasColumnName("command_text").HasMaxLength(100).IsRequired();
        entity.Property(value => value.ExitCode).HasColumnName("exit_code");
        entity.Property(value => value.StartTime).HasColumnName("start_time");
        entity.Property(value => value.FinishTime).HasColumnName("finish_time");
        entity.Property(value => value.OutputLog).HasColumnName("output_log").IsRequired();
        entity.Property(value => value.LogTruncated).HasColumnName("log_truncated");
        entity.Property(value => value.ErrorCode).HasColumnName("error_code").HasMaxLength(100);
        entity.Property(value => value.LastSequence).HasColumnName("last_sequence");
        entity.Property(value => value.LastEventAt).HasColumnName("last_event_at");
        entity.Property(value => value.DispatchedAt).HasColumnName("dispatched_at");
        entity.Property(value => value.DispatchAttemptAt).HasColumnName("dispatch_attempt_at");
        entity.Property(value => value.AcceptedAt).HasColumnName("accepted_at");
        entity.Property(value => value.TokenKeyId).HasColumnName("token_key_id").HasMaxLength(100).IsRequired();
        entity.Property(value => value.GitRepoSnapshot).HasColumnName("git_repo_snapshot").HasMaxLength(1000);
        entity.Property(value => value.GitUserNameSnapshot).HasColumnName("git_user_name_snapshot").HasMaxLength(200);
        entity.Property(value => value.StackGitRelativePathSnapshot).HasColumnName("stack_git_relative_path_snapshot").HasMaxLength(200);
        entity.Property(value => value.AgentStackLocalPathSnapshot).HasColumnName("agent_stack_local_path_snapshot").HasMaxLength(1000);
        entity.Property(value => value.OutputLogEntriesJson).HasColumnName("output_log_entries_json").IsRequired();
        entity.HasIndex(value => new { value.StackId, value.AgentId })
            .IsUnique()
            .HasFilter("task_status = 'pending'");
        entity.HasIndex(value => value.TaskId).IsUnique();
        entity.HasIndex(value => value.RequestId);
        entity.HasIndex(value => new { value.TaskStatus, value.LastEventAt });
        entity.HasIndex(value => new { value.StackId, value.LastEventAt });
        entity.HasIndex(value => new { value.AgentId, value.LastEventAt });
        entity.HasOne(value => value.Stack)
            .WithMany(value => value.Operations)
            .HasForeignKey(value => value.StackId)
            .OnDelete(DeleteBehavior.Cascade);
    }

    private static void ConfigureAudit(ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<AuditLog>();
        entity.ToTable("audit_log");
        entity.HasKey(value => value.AuditId);
        entity.Property(value => value.AuditId).HasColumnName("audit_id");
        entity.Property(value => value.RequestId).HasColumnName("request_id");
        entity.Property(value => value.ActorUserId).HasColumnName("actor_user_id");
        entity.Property(value => value.AgentId).HasColumnName("agent_id");
        entity.Property(value => value.Action).HasColumnName("action").HasMaxLength(100).IsRequired();
        entity.Property(value => value.ResourceType).HasColumnName("resource_type").HasMaxLength(100).IsRequired();
        entity.Property(value => value.ResourceId).HasColumnName("resource_id").HasMaxLength(100).IsRequired();
        entity.Property(value => value.Result).HasColumnName("result").HasMaxLength(30).IsRequired();
        entity.Property(value => value.ErrorCode).HasColumnName("error_code").HasMaxLength(100);
        entity.Property(value => value.CreatedAt).HasColumnName("created_at");
        entity.Property(value => value.RemoteIp).HasColumnName("remote_ip").HasMaxLength(100);
        entity.HasIndex(value => value.CreatedAt);
        entity.HasIndex(value => value.ActorUserId);
        entity.HasIndex(value => value.ResourceId);
    }

    private static void ConfigureRequest(ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<DeploymentRequestEntity>();
        entity.ToTable("deployment_request");
        entity.HasKey(value => value.RequestId);
        entity.Property(value => value.RequestId).HasColumnName("request_id");
        entity.Property(value => value.StackId).HasColumnName("stack_id");
        entity.Property(value => value.UserId).HasColumnName("user_id");
        entity.Property(value => value.IdempotencyKey).HasColumnName("idempotency_key").HasMaxLength(36).IsRequired();
        entity.Property(value => value.RequestFingerprint).HasColumnName("request_fingerprint").HasMaxLength(64).IsRequired();
        entity.Property(value => value.TargetCommitHash).HasColumnName("target_commit_hash").HasMaxLength(64).IsRequired();
        entity.Property(value => value.Mode)
            .HasColumnName("mode")
            .HasConversion(
                value => value == DeploymentMode.SingleAgent ? "single_agent" : "bound_agents",
                value => value == "single_agent" ? DeploymentMode.SingleAgent : DeploymentMode.BoundAgents)
            .HasMaxLength(20);
        entity.Property(value => value.CreatedAt).HasColumnName("created_at");
        entity.HasIndex(value => new { value.UserId, value.IdempotencyKey }).IsUnique();
        entity.HasOne(value => value.Stack)
            .WithMany()
            .HasForeignKey(value => value.StackId)
            .OnDelete(DeleteBehavior.Cascade);
        entity.HasMany(value => value.Operations)
            .WithOne()
            .HasForeignKey(value => value.RequestId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
