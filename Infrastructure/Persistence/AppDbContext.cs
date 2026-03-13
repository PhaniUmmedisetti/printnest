using Microsoft.EntityFrameworkCore;
using PrintNest.Domain.Entities;
using PrintNest.Domain.Enums;

namespace PrintNest.Infrastructure.Persistence;

/// <summary>
/// EF Core database context. Single source of truth for all DB schema configuration.
///
/// All table names, column names, indexes, and constraints are configured here via Fluent API.
/// Data annotations on entities are intentionally avoided — schema is infrastructure concern, not domain.
///
/// Connection string is read from configuration key "ConnectionStrings:Postgres".
/// </summary>
public sealed class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<PrintJob> PrintJobs => Set<PrintJob>();
    public DbSet<Device> Devices => Set<Device>();
    public DbSet<Store> Stores => Set<Store>();
    public DbSet<StaffUser> StaffUsers => Set<StaffUser>();
    public DbSet<AuditEvent> AuditEvents => Set<AuditEvent>();
    public DbSet<UsedFileToken> UsedFileTokens => Set<UsedFileToken>();

    protected override void OnModelCreating(ModelBuilder model)
    {
        ConfigurePrintJobs(model);
        ConfigureDevices(model);
        ConfigureStores(model);
        ConfigureStaffUsers(model);
        ConfigureAuditEvents(model);
        ConfigureUsedFileTokens(model);
    }

    // ─────────────────────────────────────────────────────────────
    // PrintJobs
    // ─────────────────────────────────────────────────────────────
    private static void ConfigurePrintJobs(ModelBuilder model)
    {
        model.Entity<PrintJob>(e =>
        {
            e.ToTable("print_jobs");
            e.HasKey(x => x.JobId);

            e.Property(x => x.JobId)
                .HasColumnName("job_id")
                .HasDefaultValueSql("gen_random_uuid()");

            // Status stored as string so migrations aren't needed when enum values are added.
            // IsConcurrencyToken() enforces optimistic concurrency:
            // EF Core includes Status in the WHERE clause of UPDATE statements.
            // If two devices try to release the same job simultaneously, only one UPDATE
            // will find Status='Paid' — the other will get DbUpdateConcurrencyException.
            e.Property(x => x.Status)
                .HasColumnName("status")
                .HasConversion<string>()
                .HasMaxLength(32)
                .IsRequired()
                .IsConcurrencyToken();

            e.Property(x => x.ObjectKey).HasColumnName("object_key").HasMaxLength(512);
            e.Property(x => x.Sha256).HasColumnName("sha256").HasMaxLength(64);
            e.Property(x => x.OptionsJson).HasColumnName("options_json").HasColumnType("jsonb");
            e.Property(x => x.PriceCents).HasColumnName("price_cents");
            e.Property(x => x.Currency).HasColumnName("currency").HasMaxLength(3).HasDefaultValue("INR");

            // OTP fields — never logged, never returned in API responses
            e.Property(x => x.OtpHash).HasColumnName("otp_hash").HasMaxLength(256);
            e.Property(x => x.OtpExpiryUtc).HasColumnName("otp_expiry_utc");
            e.Property(x => x.OtpAttempts).HasColumnName("otp_attempts").HasDefaultValue(0);
            e.Property(x => x.OtpLastAttemptUtc).HasColumnName("otp_last_attempt_utc");
            e.Property(x => x.OtpLockedUntilUtc).HasColumnName("otp_locked_until_utc");

            // Assignment fields — null until Released
            e.Property(x => x.AssignedDeviceId).HasColumnName("assigned_device_id").HasMaxLength(128);
            e.Property(x => x.AssignedStoreId).HasColumnName("assigned_store_id").HasMaxLength(128);
            e.Property(x => x.ReleaseLockUtc).HasColumnName("release_lock_utc");

            // Last failure details — set by device or watchdog, cleared on retry/completion
            e.Property(x => x.LastFailureCode).HasColumnName("last_failure_code").HasMaxLength(64);
            e.Property(x => x.LastFailureMessage).HasColumnName("last_failure_message").HasMaxLength(512);

            // Completion / deletion
            e.Property(x => x.PrintedAtUtc).HasColumnName("printed_at_utc");
            e.Property(x => x.RetryAllowed).HasColumnName("retry_allowed").HasDefaultValue(false);
            e.Property(x => x.DeletedAtUtc).HasColumnName("deleted_at_utc");
            e.Property(x => x.DeletePending).HasColumnName("delete_pending").HasDefaultValue(false);

            // Timestamps
            e.Property(x => x.CreatedAtUtc).HasColumnName("created_at_utc");
            e.Property(x => x.UpdatedAtUtc).HasColumnName("updated_at_utc");

            // ── Indexes ───────────────────────────────────────────
            // Used by background workers to find jobs needing expiry/cleanup
            e.HasIndex(x => x.Status).HasDatabaseName("ix_print_jobs_status");

            // Used by expiry worker: WHERE otp_expiry_utc < now
            e.HasIndex(x => x.OtpExpiryUtc).HasDatabaseName("ix_print_jobs_otp_expiry");

            // Used to find all jobs assigned to a specific device
            e.HasIndex(x => x.AssignedDeviceId).HasDatabaseName("ix_print_jobs_assigned_device");

            // Used by cleanup worker: WHERE delete_pending = true
            e.HasIndex(x => x.DeletePending).HasDatabaseName("ix_print_jobs_delete_pending");

            // Used by retry flow queries/status screens for failed jobs.
            e.HasIndex(x => x.RetryAllowed).HasDatabaseName("ix_print_jobs_retry_allowed");

            // Used by expiry worker: WHERE created_at_utc < now - 7 days AND status = 'Paid'
            e.HasIndex(x => x.CreatedAtUtc).HasDatabaseName("ix_print_jobs_created_at");
        });
    }

    // ─────────────────────────────────────────────────────────────
    // Devices
    // ─────────────────────────────────────────────────────────────
    private static void ConfigureDevices(ModelBuilder model)
    {
        model.Entity<Device>(e =>
        {
            e.ToTable("devices");
            e.HasKey(x => x.DeviceId);

            e.Property(x => x.DeviceId).HasColumnName("device_id").HasMaxLength(128);
            e.Property(x => x.StoreId).HasColumnName("store_id").HasMaxLength(128);

            // SharedSecret stored plaintext — HMAC requires the raw value
            e.Property(x => x.SharedSecret).HasColumnName("shared_secret").HasMaxLength(256).IsRequired();

            e.Property(x => x.LastHeartbeatUtc).HasColumnName("last_heartbeat_utc");
            e.Property(x => x.CapabilitiesJson).HasColumnName("capabilities_json").HasColumnType("jsonb");

            // Normalized printer health telemetry (reported by device heartbeat)
            e.Property(x => x.PrinterModel).HasColumnName("printer_model").HasMaxLength(256);
            e.Property(x => x.PrinterConnectionState).HasColumnName("printer_connection_state").HasMaxLength(32);
            e.Property(x => x.PrinterOfflineSinceUtc).HasColumnName("printer_offline_since_utc");
            e.Property(x => x.PrinterOperationalState).HasColumnName("printer_operational_state").HasMaxLength(32);
            e.Property(x => x.PrinterErrorSinceUtc).HasColumnName("printer_error_since_utc");
            e.Property(x => x.PrinterPaperOut).HasColumnName("printer_paper_out");
            e.Property(x => x.PrinterPaperOutSinceUtc).HasColumnName("printer_paper_out_since_utc");
            e.Property(x => x.PrinterDoorOpen).HasColumnName("printer_door_open");
            e.Property(x => x.PrinterDoorOpenSinceUtc).HasColumnName("printer_door_open_since_utc");
            e.Property(x => x.PrinterCartridgeMissing).HasColumnName("printer_cartridge_missing");
            e.Property(x => x.PrinterCartridgeMissingSinceUtc).HasColumnName("printer_cartridge_missing_since_utc");
            e.Property(x => x.PrinterInkState).HasColumnName("printer_ink_state").HasMaxLength(32);
            e.Property(x => x.PrinterInkStateChangedAtUtc).HasColumnName("printer_ink_state_changed_at_utc");
            e.Property(x => x.PrinterInkLowSinceUtc).HasColumnName("printer_ink_low_since_utc");
            e.Property(x => x.PrinterLowToEmptyAvgMinutes).HasColumnName("printer_low_to_empty_avg_minutes");
            e.Property(x => x.PrinterLowToEmptySamples).HasColumnName("printer_low_to_empty_samples").HasDefaultValue(0);
            e.Property(x => x.PrinterRawStatusJson).HasColumnName("printer_raw_status_json").HasColumnType("jsonb");
            e.Property(x => x.PrinterConnectionFlapWindowStartedAtUtc).HasColumnName("printer_connection_flap_window_started_at_utc");
            e.Property(x => x.PrinterConnectionFlapTransitions).HasColumnName("printer_connection_flap_transitions").HasDefaultValue(0);
            e.Property(x => x.PrinterStatusUpdatedAtUtc).HasColumnName("printer_status_updated_at_utc");

            e.Property(x => x.IsActive).HasColumnName("is_active").HasDefaultValue(true);
            e.Property(x => x.CreatedAtUtc).HasColumnName("created_at_utc");
            e.Property(x => x.UpdatedAtUtc).HasColumnName("updated_at_utc");

            e.HasIndex(x => x.StoreId).HasDatabaseName("ix_devices_store_id");
            e.HasIndex(x => x.IsActive).HasDatabaseName("ix_devices_is_active");
            e.HasIndex(x => x.PrinterStatusUpdatedAtUtc).HasDatabaseName("ix_devices_printer_status_updated");
        });
    }

    // ─────────────────────────────────────────────────────────────
    // Stores
    // ─────────────────────────────────────────────────────────────
    private static void ConfigureStores(ModelBuilder model)
    {
        model.Entity<Store>(e =>
        {
            e.ToTable("stores");
            e.HasKey(x => x.StoreId);

            e.Property(x => x.StoreId).HasColumnName("store_id").HasMaxLength(128);
            e.Property(x => x.Name).HasColumnName("name").HasMaxLength(256).IsRequired();
            e.Property(x => x.Address).HasColumnName("address").HasMaxLength(512);
            e.Property(x => x.Latitude).HasColumnName("latitude");
            e.Property(x => x.Longitude).HasColumnName("longitude");
            e.Property(x => x.IsActive).HasColumnName("is_active").HasDefaultValue(true);
            e.Property(x => x.CreatedAtUtc).HasColumnName("created_at_utc");
            e.Property(x => x.UpdatedAtUtc).HasColumnName("updated_at_utc");

            // Used by map queries: WHERE is_active = true ORDER BY distance
            e.HasIndex(x => x.IsActive).HasDatabaseName("ix_stores_is_active");
        });
    }

    private static void ConfigureStaffUsers(ModelBuilder model)
    {
        model.Entity<StaffUser>(e =>
        {
            e.ToTable("staff_users");
            e.HasKey(x => x.StaffUserId);

            e.Property(x => x.StaffUserId).HasColumnName("staff_user_id");
            e.Property(x => x.Username).HasColumnName("username").HasMaxLength(128).IsRequired();
            e.Property(x => x.DisplayName).HasColumnName("display_name").HasMaxLength(256).IsRequired();
            e.Property(x => x.PasswordHash).HasColumnName("password_hash").HasMaxLength(512).IsRequired();
            e.Property(x => x.Role).HasColumnName("role").HasMaxLength(32).IsRequired();
            e.Property(x => x.StoreId).HasColumnName("store_id").HasMaxLength(128);
            e.Property(x => x.IsActive).HasColumnName("is_active").HasDefaultValue(true);
            e.Property(x => x.LastLoginAtUtc).HasColumnName("last_login_at_utc");
            e.Property(x => x.CreatedAtUtc).HasColumnName("created_at_utc");
            e.Property(x => x.UpdatedAtUtc).HasColumnName("updated_at_utc");

            e.HasIndex(x => x.Username).IsUnique().HasDatabaseName("ux_staff_users_username");
            e.HasIndex(x => x.Role).HasDatabaseName("ix_staff_users_role");
            e.HasIndex(x => x.StoreId).HasDatabaseName("ix_staff_users_store_id");
        });
    }

    // ─────────────────────────────────────────────────────────────
    // AuditEvents
    // ─────────────────────────────────────────────────────────────
    private static void ConfigureAuditEvents(ModelBuilder model)
    {
        model.Entity<AuditEvent>(e =>
        {
            e.ToTable("audit_events");
            e.HasKey(x => x.Id);

            e.Property(x => x.Id).HasColumnName("id").UseIdentityAlwaysColumn();
            e.Property(x => x.JobId).HasColumnName("job_id");
            e.Property(x => x.Type)
                .HasColumnName("type")
                .HasConversion<string>()
                .HasMaxLength(64)
                .IsRequired();
            e.Property(x => x.MetaJson).HasColumnName("meta_json").HasColumnType("jsonb");
            e.Property(x => x.CreatedAtUtc).HasColumnName("created_at_utc");

            // Primary query: all events for a specific job
            e.HasIndex(x => x.JobId).HasDatabaseName("ix_audit_events_job_id");
        });
    }

    // ─────────────────────────────────────────────────────────────
    // UsedFileTokens
    // ─────────────────────────────────────────────────────────────
    private static void ConfigureUsedFileTokens(ModelBuilder model)
    {
        model.Entity<UsedFileToken>(e =>
        {
            e.ToTable("used_file_tokens");
            e.HasKey(x => x.Jti);

            e.Property(x => x.Jti).HasColumnName("jti").HasMaxLength(128);
            e.Property(x => x.JobId).HasColumnName("job_id");
            e.Property(x => x.DeviceId).HasColumnName("device_id").HasMaxLength(128);
            e.Property(x => x.UsedAtUtc).HasColumnName("used_at_utc");

            // Used by cleanup worker: DELETE WHERE used_at_utc < now - 24h
            e.HasIndex(x => x.UsedAtUtc).HasDatabaseName("ix_used_file_tokens_used_at");
        });
    }
}
