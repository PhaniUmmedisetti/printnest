using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PrintNest.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAlertEscalationAndInkPrediction : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "printer_cartridge_missing_since_utc",
                table: "devices",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "printer_door_open_since_utc",
                table: "devices",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "printer_error_since_utc",
                table: "devices",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "printer_ink_low_since_utc",
                table: "devices",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "printer_ink_state_changed_at_utc",
                table: "devices",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "printer_low_to_empty_avg_minutes",
                table: "devices",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "printer_low_to_empty_samples",
                table: "devices",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTime>(
                name: "printer_offline_since_utc",
                table: "devices",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "printer_paper_out_since_utc",
                table: "devices",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "printer_cartridge_missing_since_utc",
                table: "devices");

            migrationBuilder.DropColumn(
                name: "printer_door_open_since_utc",
                table: "devices");

            migrationBuilder.DropColumn(
                name: "printer_error_since_utc",
                table: "devices");

            migrationBuilder.DropColumn(
                name: "printer_ink_low_since_utc",
                table: "devices");

            migrationBuilder.DropColumn(
                name: "printer_ink_state_changed_at_utc",
                table: "devices");

            migrationBuilder.DropColumn(
                name: "printer_low_to_empty_avg_minutes",
                table: "devices");

            migrationBuilder.DropColumn(
                name: "printer_low_to_empty_samples",
                table: "devices");

            migrationBuilder.DropColumn(
                name: "printer_offline_since_utc",
                table: "devices");

            migrationBuilder.DropColumn(
                name: "printer_paper_out_since_utc",
                table: "devices");
        }
    }
}
