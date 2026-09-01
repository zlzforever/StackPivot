using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace StackPivot.Control.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddDispatchMetadata : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "dispatched_at",
                table: "service_operation_history",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "token_key_id",
                table: "service_operation_history",
                type: "TEXT",
                maxLength: 100,
                nullable: false,
                defaultValue: "");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "dispatched_at",
                table: "service_operation_history");

            migrationBuilder.DropColumn(
                name: "token_key_id",
                table: "service_operation_history");
        }
    }
}
