using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PrintNest.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddStaffUserAuthentication : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "staff_users",
                columns: table => new
                {
                    staff_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    username = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    display_name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    password_hash = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    role = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    store_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    is_active = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    last_login_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_staff_users", x => x.staff_user_id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_staff_users_role",
                table: "staff_users",
                column: "role");

            migrationBuilder.CreateIndex(
                name: "ix_staff_users_store_id",
                table: "staff_users",
                column: "store_id");

            migrationBuilder.CreateIndex(
                name: "ux_staff_users_username",
                table: "staff_users",
                column: "username",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "staff_users");
        }
    }
}
