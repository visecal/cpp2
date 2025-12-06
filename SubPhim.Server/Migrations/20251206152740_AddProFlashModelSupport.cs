using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SubPhim.Server.Migrations
{
    /// <inheritdoc />
    public partial class AddProFlashModelSupport : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "GlobalWindowMinutes",
                table: "LocalApiSettings",
                newName: "ProRpdPerKey");

            migrationBuilder.RenameColumn(
                name: "GlobalMaxRequests",
                table: "LocalApiSettings",
                newName: "FlashRpdPerKey");

            migrationBuilder.AddColumn<int>(
                name: "FlashRequestsToday",
                table: "ManagedApiKeys",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "ProRequestsToday",
                table: "ManagedApiKeys",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "ProModelName",
                table: "LocalApiSettings",
                type: "TEXT",
                maxLength: 100,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<int>(
                name: "ModelType",
                table: "AvailableApiModels",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "FlashRequestsToday",
                table: "ManagedApiKeys");

            migrationBuilder.DropColumn(
                name: "ProRequestsToday",
                table: "ManagedApiKeys");

            migrationBuilder.DropColumn(
                name: "ProModelName",
                table: "LocalApiSettings");

            migrationBuilder.DropColumn(
                name: "ModelType",
                table: "AvailableApiModels");

            migrationBuilder.RenameColumn(
                name: "ProRpdPerKey",
                table: "LocalApiSettings",
                newName: "GlobalWindowMinutes");

            migrationBuilder.RenameColumn(
                name: "FlashRpdPerKey",
                table: "LocalApiSettings",
                newName: "GlobalMaxRequests");
        }
    }
}
