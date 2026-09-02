using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace StackPivot.Control.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddDispatcherStatusIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_service_operation_history_task_status_last_event_at",
                table: "service_operation_history",
                columns: new[] { "task_status", "last_event_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_service_operation_history_task_status_last_event_at",
                table: "service_operation_history");
        }
    }
}
