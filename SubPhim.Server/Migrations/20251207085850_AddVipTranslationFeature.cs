using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SubPhim.Server.Migrations
{
    /// <inheritdoc />
    public partial class AddVipTranslationFeature : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "DailyVipSrtLimit",
                table: "Users",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTime>(
                name: "LastVipSrtResetUtc",
                table: "Users",
                type: "TEXT",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));

            migrationBuilder.AddColumn<int>(
                name: "VipSrtLinesUsedToday",
                table: "Users",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "DailyVipSrtLimit",
                table: "TierDefaultSettings",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "VipApiKeys",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    EncryptedApiKey = table.Column<string>(type: "TEXT", nullable: false),
                    Iv = table.Column<string>(type: "TEXT", nullable: false),
                    IsEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    TotalTokensUsed = table.Column<long>(type: "INTEGER", nullable: false),
                    RequestsToday = table.Column<int>(type: "INTEGER", nullable: false),
                    LastRequestCountResetUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    TemporaryCooldownUntil = table.Column<DateTime>(type: "TEXT", nullable: true),
                    DisabledReason = table.Column<string>(type: "TEXT", maxLength: 300, nullable: true),
                    Consecutive429Count = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VipApiKeys", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "VipAvailableApiModels",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ModelName = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    IsActive = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VipAvailableApiModels", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "VipTranslationSettings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false),
                    Rpm = table.Column<int>(type: "INTEGER", nullable: false),
                    BatchSize = table.Column<int>(type: "INTEGER", nullable: false),
                    MaxRetries = table.Column<int>(type: "INTEGER", nullable: false),
                    RetryDelayMs = table.Column<int>(type: "INTEGER", nullable: false),
                    DelayBetweenBatchesMs = table.Column<int>(type: "INTEGER", nullable: false),
                    Temperature = table.Column<decimal>(type: "decimal(3, 2)", nullable: false),
                    MaxOutputTokens = table.Column<int>(type: "INTEGER", nullable: false),
                    EnableThinkingBudget = table.Column<bool>(type: "INTEGER", nullable: false),
                    ThinkingBudget = table.Column<int>(type: "INTEGER", nullable: false),
                    RpmPerProxy = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VipTranslationSettings", x => x.Id);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "VipApiKeys");

            migrationBuilder.DropTable(
                name: "VipAvailableApiModels");

            migrationBuilder.DropTable(
                name: "VipTranslationSettings");

            migrationBuilder.DropColumn(
                name: "DailyVipSrtLimit",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "LastVipSrtResetUtc",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "VipSrtLinesUsedToday",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "DailyVipSrtLimit",
                table: "TierDefaultSettings");
        }
    }
}
