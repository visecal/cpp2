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
            migrationBuilder.AddColumn<int>(
                name: "MaxSrtLineLength",
                table: "VipTranslationSettings",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

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
            migrationBuilder.DropColumn(
                name: "MaxSrtLineLength",
                table: "VipTranslationSettings");

            migrationBuilder.DropColumn(
                name: "BatchTimeoutMinutes",
                table: "LocalApiSettings");

            migrationBuilder.DropColumn(
                name: "EnableBatchProcessing",
                table: "LocalApiSettings");
        }
    }
}
