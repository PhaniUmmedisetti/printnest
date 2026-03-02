namespace PrintNest.Domain.Entities;

/// <summary>
/// Backoffice/staff account used by admin and store-manager portals.
/// </summary>
public sealed class StaffUser
{
    public Guid StaffUserId { get; init; } = Guid.NewGuid();

    /// <summary>Unique login name (case-insensitive).</summary>
    public string Username { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    /// <summary>Argon2id password hash. Never log this.</summary>
    public string PasswordHash { get; set; } = string.Empty;

    /// <summary>Role: SUPER_ADMIN | STORE_MANAGER.</summary>
    public string Role { get; set; } = StaffRoles.StoreManager;

    /// <summary>Required for STORE_MANAGER, null for SUPER_ADMIN.</summary>
    public string? StoreId { get; set; }

    public bool IsActive { get; set; } = true;
    public DateTime? LastLoginAtUtc { get; set; }
    public DateTime CreatedAtUtc { get; init; } = DateTime.UtcNow;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}
