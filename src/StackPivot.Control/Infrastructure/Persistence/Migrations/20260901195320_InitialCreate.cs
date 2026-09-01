using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace StackPivot.Control.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "agent_node",
                columns: table => new
                {
                    agent_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    remark = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    api_key_hash = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    api_key_version = table.Column<int>(type: "INTEGER", nullable: false),
                    api_key_last4 = table.Column<string>(type: "TEXT", maxLength: 4, nullable: true),
                    revoked_at = table.Column<string>(type: "TEXT", nullable: true),
                    last_seen_at = table.Column<string>(type: "TEXT", nullable: true),
                    capabilities_json = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_agent_node", x => x.agent_id);
                });

            migrationBuilder.CreateTable(
                name: "audit_log",
                columns: table => new
                {
                    audit_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    request_id = table.Column<Guid>(type: "TEXT", nullable: true),
                    actor_user_id = table.Column<Guid>(type: "TEXT", nullable: true),
                    agent_id = table.Column<Guid>(type: "TEXT", nullable: true),
                    action = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    resource_type = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    resource_id = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    result = table.Column<string>(type: "TEXT", maxLength: 30, nullable: false),
                    error_code = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    created_at = table.Column<string>(type: "TEXT", nullable: false),
                    remote_ip = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_audit_log", x => x.audit_id);
                });

            migrationBuilder.CreateTable(
                name: "global_git_setting",
                columns: table => new
                {
                    id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    git_repo = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: false),
                    git_user_name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    access_token_encrypted = table.Column<string>(type: "TEXT", nullable: false),
                    token_key_id = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    updated_at = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_global_git_setting", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "user",
                columns: table => new
                {
                    user_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    user_name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    sso_subject = table.Column<string>(type: "TEXT", maxLength: 300, nullable: false),
                    is_platform_admin = table.Column<bool>(type: "INTEGER", nullable: false),
                    created_at = table.Column<string>(type: "TEXT", nullable: false),
                    updated_at = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_user", x => x.user_id);
                });

            migrationBuilder.CreateTable(
                name: "workspace",
                columns: table => new
                {
                    workspace_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    name = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    display_name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    created_at = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_workspace", x => x.workspace_id);
                });

            migrationBuilder.CreateTable(
                name: "stack",
                columns: table => new
                {
                    stack_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    workspace_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    folder_name = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    display_name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    created_at = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_stack", x => x.stack_id);
                    table.ForeignKey(
                        name: "FK_stack_workspace_workspace_id",
                        column: x => x.workspace_id,
                        principalTable: "workspace",
                        principalColumn: "workspace_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "workspace_member",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    workspace_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    user_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    permission = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    created_at = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_workspace_member", x => x.id);
                    table.ForeignKey(
                        name: "FK_workspace_member_user_user_id",
                        column: x => x.user_id,
                        principalTable: "user",
                        principalColumn: "user_id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_workspace_member_workspace_workspace_id",
                        column: x => x.workspace_id,
                        principalTable: "workspace",
                        principalColumn: "workspace_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "deployment_request",
                columns: table => new
                {
                    request_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    stack_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    user_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    idempotency_key = table.Column<string>(type: "TEXT", maxLength: 36, nullable: false),
                    request_fingerprint = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    target_commit_hash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    mode = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    created_at = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_deployment_request", x => x.request_id);
                    table.ForeignKey(
                        name: "FK_deployment_request_stack_stack_id",
                        column: x => x.stack_id,
                        principalTable: "stack",
                        principalColumn: "stack_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "stack_agent_binding",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    stack_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    agent_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    created_at = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_stack_agent_binding", x => x.id);
                    table.ForeignKey(
                        name: "FK_stack_agent_binding_agent_node_agent_id",
                        column: x => x.agent_id,
                        principalTable: "agent_node",
                        principalColumn: "agent_id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_stack_agent_binding_stack_stack_id",
                        column: x => x.stack_id,
                        principalTable: "stack",
                        principalColumn: "stack_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "service_operation_history",
                columns: table => new
                {
                    history_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    task_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    request_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    stack_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    agent_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    user_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    operation_type = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    target_commit_hash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    task_status = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    command_text = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    exit_code = table.Column<int>(type: "INTEGER", nullable: true),
                    start_time = table.Column<string>(type: "TEXT", nullable: true),
                    finish_time = table.Column<string>(type: "TEXT", nullable: true),
                    output_log = table.Column<string>(type: "TEXT", nullable: false),
                    log_truncated = table.Column<bool>(type: "INTEGER", nullable: false),
                    error_code = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    last_sequence = table.Column<long>(type: "INTEGER", nullable: false),
                    last_event_at = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_service_operation_history", x => x.history_id);
                    table.ForeignKey(
                        name: "FK_service_operation_history_deployment_request_request_id",
                        column: x => x.request_id,
                        principalTable: "deployment_request",
                        principalColumn: "request_id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_service_operation_history_stack_stack_id",
                        column: x => x.stack_id,
                        principalTable: "stack",
                        principalColumn: "stack_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_agent_node_api_key_hash",
                table: "agent_node",
                column: "api_key_hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_audit_log_actor_user_id",
                table: "audit_log",
                column: "actor_user_id");

            migrationBuilder.CreateIndex(
                name: "IX_audit_log_created_at",
                table: "audit_log",
                column: "created_at");

            migrationBuilder.CreateIndex(
                name: "IX_audit_log_resource_id",
                table: "audit_log",
                column: "resource_id");

            migrationBuilder.CreateIndex(
                name: "IX_deployment_request_stack_id",
                table: "deployment_request",
                column: "stack_id");

            migrationBuilder.CreateIndex(
                name: "IX_deployment_request_user_id_idempotency_key",
                table: "deployment_request",
                columns: new[] { "user_id", "idempotency_key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_service_operation_history_agent_id_last_event_at",
                table: "service_operation_history",
                columns: new[] { "agent_id", "last_event_at" });

            migrationBuilder.CreateIndex(
                name: "IX_service_operation_history_request_id",
                table: "service_operation_history",
                column: "request_id");

            migrationBuilder.CreateIndex(
                name: "IX_service_operation_history_stack_id_last_event_at",
                table: "service_operation_history",
                columns: new[] { "stack_id", "last_event_at" });

            migrationBuilder.CreateIndex(
                name: "IX_service_operation_history_task_id",
                table: "service_operation_history",
                column: "task_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_stack_workspace_id_folder_name",
                table: "stack",
                columns: new[] { "workspace_id", "folder_name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_stack_agent_binding_agent_id",
                table: "stack_agent_binding",
                column: "agent_id");

            migrationBuilder.CreateIndex(
                name: "IX_stack_agent_binding_stack_id_agent_id",
                table: "stack_agent_binding",
                columns: new[] { "stack_id", "agent_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_user_sso_subject",
                table: "user",
                column: "sso_subject",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_workspace_name",
                table: "workspace",
                column: "name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_workspace_member_user_id",
                table: "workspace_member",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "IX_workspace_member_workspace_id_user_id",
                table: "workspace_member",
                columns: new[] { "workspace_id", "user_id" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "audit_log");

            migrationBuilder.DropTable(
                name: "global_git_setting");

            migrationBuilder.DropTable(
                name: "service_operation_history");

            migrationBuilder.DropTable(
                name: "stack_agent_binding");

            migrationBuilder.DropTable(
                name: "workspace_member");

            migrationBuilder.DropTable(
                name: "deployment_request");

            migrationBuilder.DropTable(
                name: "agent_node");

            migrationBuilder.DropTable(
                name: "user");

            migrationBuilder.DropTable(
                name: "stack");

            migrationBuilder.DropTable(
                name: "workspace");
        }
    }
}
