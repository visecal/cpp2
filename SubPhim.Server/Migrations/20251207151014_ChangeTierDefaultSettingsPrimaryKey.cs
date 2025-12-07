using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SubPhim.Server.Migrations
{
    /// <inheritdoc />
    public partial class ChangeTierDefaultSettingsPrimaryKey : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropPrimaryKey(
                name: "PK_TierDefaultSettings",
                table: "TierDefaultSettings");

            migrationBuilder.AddColumn<int>(
                name: "Id",
                table: "TierDefaultSettings",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0)
                .Annotation("Sqlite:Autoincrement", true);

            migrationBuilder.AddPrimaryKey(
                name: "PK_TierDefaultSettings",
                table: "TierDefaultSettings",
                column: "Id");

            migrationBuilder.CreateIndex(
                name: "IX_TierDefaultSettings_Tier_IsYearlyProSettings",
                table: "TierDefaultSettings",
                columns: new[] { "Tier", "IsYearlyProSettings" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropPrimaryKey(
                name: "PK_TierDefaultSettings",
                table: "TierDefaultSettings");

            migrationBuilder.DropIndex(
                name: "IX_TierDefaultSettings_Tier_IsYearlyProSettings",
                table: "TierDefaultSettings");

            migrationBuilder.DropColumn(
                name: "Id",
                table: "TierDefaultSettings");

            migrationBuilder.AddPrimaryKey(
                name: "PK_TierDefaultSettings",
                table: "TierDefaultSettings",
                column: "Tier");
        }
    }
}
