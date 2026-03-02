using Microsoft.EntityFrameworkCore;
using PrintNest.Application.Interfaces;
using PrintNest.Domain.Entities;
using PrintNest.Infrastructure.Persistence;

namespace PrintNest.Infrastructure.Auth;

public sealed class StaffBootstrapService
{
    private readonly IConfiguration _config;
    private readonly IStaffPasswordService _passwords;
    private readonly ILogger<StaffBootstrapService> _logger;

    public StaffBootstrapService(
        IConfiguration config,
        IStaffPasswordService passwords,
        ILogger<StaffBootstrapService> logger)
    {
        _config = config;
        _passwords = passwords;
        _logger = logger;
    }

    public async Task EnsureBootstrapSuperAdminAsync(AppDbContext db, CancellationToken cancellationToken = default)
    {
        var username = (_config["StaffAuth:BootstrapUsername"] ?? "admin").Trim();
        var password = _config["StaffAuth:BootstrapPassword"]?.Trim();
        var displayName = (_config["StaffAuth:BootstrapDisplayName"] ?? "Bootstrap Admin").Trim();

        if (string.IsNullOrWhiteSpace(username))
            throw new InvalidOperationException("StaffAuth:BootstrapUsername must not be empty.");

        if (string.IsNullOrWhiteSpace(password) || password.Length < 10)
            throw new InvalidOperationException("StaffAuth:BootstrapPassword is required and must be at least 10 characters.");

        var normalizedUsername = username.ToLowerInvariant();
        var exists = await db.StaffUsers.AnyAsync(
            x => x.Username.ToLower() == normalizedUsername,
            cancellationToken);
        if (exists)
            return;

        db.StaffUsers.Add(new StaffUser
        {
            StaffUserId = Guid.NewGuid(),
            Username = username,
            DisplayName = displayName,
            PasswordHash = _passwords.Hash(password),
            Role = StaffRoles.SuperAdmin,
            StoreId = null,
            IsActive = true,
            CreatedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow
        });

        await db.SaveChangesAsync(cancellationToken);
        _logger.LogInformation("Created bootstrap super-admin account: {Username}", username);
    }
}
