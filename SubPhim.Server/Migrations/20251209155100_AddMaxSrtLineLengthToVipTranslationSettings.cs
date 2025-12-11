using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SubPhim.Server.Migrations
{
    /// <inheritdoc />
    public partial class AddMaxSrtLineLengthToVipTranslationSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // NOTE: Column MaxSrtLineLength is now added via EnsureMissingColumnsExist() in Program.cs
            // This migration is kept for history but does nothing since SQLite doesn't support
            // IF NOT EXISTS for ALTER TABLE ADD COLUMN
            // The column addition was moved to Program.cs to handle cases where it already exists
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // NOTE: Column is managed by EnsureMissingColumnsExist() in Program.cs
            // Don't drop it here as it may break other functionality
        }
    }
}
