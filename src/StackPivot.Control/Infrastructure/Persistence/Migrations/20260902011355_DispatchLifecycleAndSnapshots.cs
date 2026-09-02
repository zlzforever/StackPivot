using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace StackPivot.Control.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class DispatchLifecycleAndSnapshots : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "accepted_at",
                table: "service_operation_history",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "agent_stack_local_path_snapshot",
                table: "service_operation_history",
                type: "TEXT",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "dispatch_attempt_at",
                table: "service_operation_history",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "git_repo_snapshot",
                table: "service_operation_history",
                type: "TEXT",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "git_user_name_snapshot",
                table: "service_operation_history",
                type: "TEXT",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "output_log_entries_json",
                table: "service_operation_history",
                type: "TEXT",
                nullable: false,
                defaultValue: "[]");

            migrationBuilder.AddColumn<string>(
                name: "stack_git_relative_path_snapshot",
                table: "service_operation_history",
                type: "TEXT",
                maxLength: 200,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_service_operation_history_stack_id_agent_id",
                table: "service_operation_history",
                columns: new[] { "stack_id", "agent_id" },
                unique: true,
                filter: "task_status = 'pending'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_service_operation_history_stack_id_agent_id",
                table: "service_operation_history");

            migrationBuilder.DropColumn(
                name: "accepted_at",
                table: "service_operation_history");

            migrationBuilder.DropColumn(
                name: "agent_stack_local_path_snapshot",
                table: "service_operation_history");

            migrationBuilder.DropColumn(
                name: "dispatch_attempt_at",
                table: "service_operation_history");

            migrationBuilder.DropColumn(
                name: "git_repo_snapshot",
                table: "service_operation_history");

            migrationBuilder.DropColumn(
                name: "git_user_name_snapshot",
                table: "service_operation_history");

            migrationBuilder.DropColumn(
                name: "output_log_entries_json",
                table: "service_operation_history");

            migrationBuilder.DropColumn(
                name: "stack_git_relative_path_snapshot",
                table: "service_operation_history");
        }
    }
}
