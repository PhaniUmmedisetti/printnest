using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PrintNest.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddRetryableFailedOtpRegeneration : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "retry_allowed",
                table: "print_jobs",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateIndex(
                name: "ix_print_jobs_retry_allowed",
                table: "print_jobs",
                column: "retry_allowed");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_print_jobs_retry_allowed",
                table: "print_jobs");

            migrationBuilder.DropColumn(
                name: "retry_allowed",
                table: "print_jobs");
        }
    }
}
