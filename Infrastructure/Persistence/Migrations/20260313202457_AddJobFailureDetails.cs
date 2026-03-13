using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PrintNest.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddJobFailureDetails : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "last_failure_code",
                table: "print_jobs",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "last_failure_message",
                table: "print_jobs",
                type: "character varying(512)",
                maxLength: 512,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "last_failure_code",
                table: "print_jobs");

            migrationBuilder.DropColumn(
                name: "last_failure_message",
                table: "print_jobs");
        }
    }
}
