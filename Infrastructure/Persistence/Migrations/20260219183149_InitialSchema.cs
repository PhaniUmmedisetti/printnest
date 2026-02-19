using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace PrintNest.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "audit_events",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    job_id = table.Column<Guid>(type: "uuid", nullable: false),
                    type = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    meta_json = table.Column<string>(type: "jsonb", nullable: true),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_audit_events", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "devices",
                columns: table => new
                {
                    device_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    store_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    shared_secret = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    last_heartbeat_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    capabilities_json = table.Column<string>(type: "jsonb", nullable: true),
                    is_active = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_devices", x => x.device_id);
                });

            migrationBuilder.CreateTable(
                name: "print_jobs",
                columns: table => new
                {
                    job_id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    object_key = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    options_json = table.Column<string>(type: "jsonb", nullable: true),
                    price_cents = table.Column<int>(type: "integer", nullable: false),
                    currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false, defaultValue: "INR"),
                    otp_hash = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    otp_expiry_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    otp_attempts = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    otp_last_attempt_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    otp_locked_until_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    assigned_device_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    assigned_store_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    release_lock_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    printed_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    deleted_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    delete_pending = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_print_jobs", x => x.job_id);
                });

            migrationBuilder.CreateTable(
                name: "stores",
                columns: table => new
                {
                    store_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    address = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    latitude = table.Column<double>(type: "double precision", nullable: false),
                    longitude = table.Column<double>(type: "double precision", nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_stores", x => x.store_id);
                });

            migrationBuilder.CreateTable(
                name: "used_file_tokens",
                columns: table => new
                {
                    jti = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    job_id = table.Column<Guid>(type: "uuid", nullable: false),
                    device_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    used_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_used_file_tokens", x => x.jti);
                });

            migrationBuilder.CreateIndex(
                name: "ix_audit_events_job_id",
                table: "audit_events",
                column: "job_id");

            migrationBuilder.CreateIndex(
                name: "ix_devices_is_active",
                table: "devices",
                column: "is_active");

            migrationBuilder.CreateIndex(
                name: "ix_devices_store_id",
                table: "devices",
                column: "store_id");

            migrationBuilder.CreateIndex(
                name: "ix_print_jobs_assigned_device",
                table: "print_jobs",
                column: "assigned_device_id");

            migrationBuilder.CreateIndex(
                name: "ix_print_jobs_created_at",
                table: "print_jobs",
                column: "created_at_utc");

            migrationBuilder.CreateIndex(
                name: "ix_print_jobs_delete_pending",
                table: "print_jobs",
                column: "delete_pending");

            migrationBuilder.CreateIndex(
                name: "ix_print_jobs_otp_expiry",
                table: "print_jobs",
                column: "otp_expiry_utc");

            migrationBuilder.CreateIndex(
                name: "ix_print_jobs_status",
                table: "print_jobs",
                column: "status");

            migrationBuilder.CreateIndex(
                name: "ix_stores_is_active",
                table: "stores",
                column: "is_active");

            migrationBuilder.CreateIndex(
                name: "ix_used_file_tokens_used_at",
                table: "used_file_tokens",
                column: "used_at_utc");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "audit_events");

            migrationBuilder.DropTable(
                name: "devices");

            migrationBuilder.DropTable(
                name: "print_jobs");

            migrationBuilder.DropTable(
                name: "stores");

            migrationBuilder.DropTable(
                name: "used_file_tokens");
        }
    }
}
