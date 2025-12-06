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
                newName: "ProRpmPerProxy");

            migrationBuilder.RenameColumn(
                name: "GlobalMaxRequests",
                table: "LocalApiSettings",
                newName: "ProRpm");

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

            migrationBuilder.AddColumn<int>(
                name: "FlashRpdPerKey",
                table: "LocalApiSettings",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "FlashRpm",
                table: "LocalApiSettings",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "FlashRpmPerProxy",
                table: "LocalApiSettings",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "ProRpdPerKey",
                table: "LocalApiSettings",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "ProfessionalModel",
                table: "LocalApiSettings",
                type: "TEXT",
                maxLength: 100,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<int>(
                name: "ModelType",
                table: "AvailableApiModels",
                type: "INTEGER",
                nullable: true);
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
                name: "FlashRpdPerKey",
                table: "LocalApiSettings");

            migrationBuilder.DropColumn(
                name: "FlashRpm",
                table: "LocalApiSettings");

            migrationBuilder.DropColumn(
                name: "FlashRpmPerProxy",
                table: "LocalApiSettings");

            migrationBuilder.DropColumn(
                name: "ProRpdPerKey",
                table: "LocalApiSettings");

            migrationBuilder.DropColumn(
                name: "ProfessionalModel",
                table: "LocalApiSettings");

            migrationBuilder.DropColumn(
                name: "ModelType",
                table: "AvailableApiModels");

            migrationBuilder.RenameColumn(
                name: "ProRpmPerProxy",
                table: "LocalApiSettings",
                newName: "GlobalWindowMinutes");

            migrationBuilder.RenameColumn(
                name: "ProRpm",
                table: "LocalApiSettings",
                newName: "GlobalMaxRequests");
        }
    }
}
