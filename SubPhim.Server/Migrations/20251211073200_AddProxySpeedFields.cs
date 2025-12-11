using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SubPhim.Server.Migrations
{
    /// <inheritdoc />
    public partial class AddProxySpeedFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "SpeedMs",
                table: "Proxies",
                type: "INTEGER",
                nullable: false,
                defaultValue: -1);

            migrationBuilder.AddColumn<DateTime>(
                name: "LastSpeedTestAt",
                table: "Proxies",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SpeedMs",
                table: "Proxies");

            migrationBuilder.DropColumn(
                name: "LastSpeedTestAt",
                table: "Proxies");
        }
    }
}
