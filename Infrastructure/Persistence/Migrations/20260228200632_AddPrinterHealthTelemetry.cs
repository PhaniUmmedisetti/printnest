using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PrintNest.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPrinterHealthTelemetry : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "printer_cartridge_missing",
                table: "devices",
                type: "boolean",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "printer_connection_state",
                table: "devices",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "printer_door_open",
                table: "devices",
                type: "boolean",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "printer_ink_state",
                table: "devices",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "printer_model",
                table: "devices",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "printer_operational_state",
                table: "devices",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "printer_paper_out",
                table: "devices",
                type: "boolean",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "printer_raw_status_json",
                table: "devices",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "printer_status_updated_at_utc",
                table: "devices",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_devices_printer_status_updated",
                table: "devices",
                column: "printer_status_updated_at_utc");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_devices_printer_status_updated",
                table: "devices");

            migrationBuilder.DropColumn(
                name: "printer_cartridge_missing",
                table: "devices");

            migrationBuilder.DropColumn(
                name: "printer_connection_state",
                table: "devices");

            migrationBuilder.DropColumn(
                name: "printer_door_open",
                table: "devices");

            migrationBuilder.DropColumn(
                name: "printer_ink_state",
                table: "devices");

            migrationBuilder.DropColumn(
                name: "printer_model",
                table: "devices");

            migrationBuilder.DropColumn(
                name: "printer_operational_state",
                table: "devices");

            migrationBuilder.DropColumn(
                name: "printer_paper_out",
                table: "devices");

            migrationBuilder.DropColumn(
                name: "printer_raw_status_json",
                table: "devices");

            migrationBuilder.DropColumn(
                name: "printer_status_updated_at_utc",
                table: "devices");
        }
    }
}
