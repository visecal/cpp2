using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SubPhim.Server.Migrations
{
    /// <inheritdoc />
    public partial class AddVipTranslation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "DailyVipTranslationLimit",
                table: "Users",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "DailyVipTranslationLimitOverride",
                table: "Users",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTime>(
                name: "LastVipTranslationResetUtc",
                table: "Users",
                type: "TEXT",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));

            migrationBuilder.AddColumn<int>(
                name: "VipTranslationLinesUsedToday",
                table: "Users",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "DailyVipTranslationLimit",
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
                name: "VipTranslationJobs",
                columns: table => new
                {
                    SessionId = table.Column<string>(type: "TEXT", nullable: false),
                    UserId = table.Column<int>(type: "INTEGER", nullable: false),
                    SystemInstruction = table.Column<string>(type: "TEXT", nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CompletedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ErrorMessage = table.Column<string>(type: "TEXT", nullable: true),
                    TotalLines = table.Column<int>(type: "INTEGER", nullable: false),
                    ProcessedLines = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VipTranslationJobs", x => x.SessionId);
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

            migrationBuilder.CreateTable(
                name: "VipOriginalSrtLines",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    SessionId = table.Column<string>(type: "TEXT", nullable: false),
                    LineNumber = table.Column<int>(type: "INTEGER", nullable: false),
                    TimeCode = table.Column<string>(type: "TEXT", nullable: false),
                    OriginalText = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VipOriginalSrtLines", x => x.Id);
                    table.ForeignKey(
                        name: "FK_VipOriginalSrtLines_VipTranslationJobs_SessionId",
                        column: x => x.SessionId,
                        principalTable: "VipTranslationJobs",
                        principalColumn: "SessionId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "VipTranslatedSrtLines",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    SessionId = table.Column<string>(type: "TEXT", nullable: false),
                    LineNumber = table.Column<int>(type: "INTEGER", nullable: false),
                    TimeCode = table.Column<string>(type: "TEXT", nullable: false),
                    TranslatedText = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VipTranslatedSrtLines", x => x.Id);
                    table.ForeignKey(
                        name: "FK_VipTranslatedSrtLines_VipTranslationJobs_SessionId",
                        column: x => x.SessionId,
                        principalTable: "VipTranslationJobs",
                        principalColumn: "SessionId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_VipOriginalSrtLines_SessionId",
                table: "VipOriginalSrtLines",
                column: "SessionId");

            migrationBuilder.CreateIndex(
                name: "IX_VipTranslatedSrtLines_SessionId",
                table: "VipTranslatedSrtLines",
                column: "SessionId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "VipApiKeys");

            migrationBuilder.DropTable(
                name: "VipOriginalSrtLines");

            migrationBuilder.DropTable(
                name: "VipTranslatedSrtLines");

            migrationBuilder.DropTable(
                name: "VipTranslationSettings");

            migrationBuilder.DropTable(
                name: "VipTranslationJobs");

            migrationBuilder.DropColumn(
                name: "DailyVipTranslationLimit",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "DailyVipTranslationLimitOverride",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "LastVipTranslationResetUtc",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "VipTranslationLinesUsedToday",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "DailyVipTranslationLimit",
                table: "TierDefaultSettings");
        }
    }
}
