using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SubPhim.Server.Migrations
{
    /// <inheritdoc />
    public partial class AddBatchProcessingSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // NOTE: MaxSrtLineLength is already added in migration 20251209155100 and handled by EnsureMissingColumnsExist
            // Removed to prevent duplicate column error

            migrationBuilder.AddColumn<int>(
                name: "BatchTimeoutMinutes",
                table: "LocalApiSettings",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<bool>(
                name: "EnableBatchProcessing",
                table: "LocalApiSettings",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // NOTE: MaxSrtLineLength is managed by migration 20251209155100
            // Don't drop it here

            migrationBuilder.DropColumn(
                name: "BatchTimeoutMinutes",
                table: "LocalApiSettings");

            migrationBuilder.DropColumn(
                name: "EnableBatchProcessing",
                table: "LocalApiSettings");
        }
    }
}
