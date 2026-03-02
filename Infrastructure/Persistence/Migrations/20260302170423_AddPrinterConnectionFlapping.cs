using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PrintNest.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPrinterConnectionFlapping : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "printer_connection_flap_transitions",
                table: "devices",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTime>(
                name: "printer_connection_flap_window_started_at_utc",
                table: "devices",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "printer_connection_flap_transitions",
                table: "devices");

            migrationBuilder.DropColumn(
                name: "printer_connection_flap_window_started_at_utc",
                table: "devices");
        }
    }
}
