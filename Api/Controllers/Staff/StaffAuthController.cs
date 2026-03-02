using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PrintNest.Application.Interfaces;
using PrintNest.Domain.Entities;
using PrintNest.Domain.Errors;
using PrintNest.Infrastructure.Persistence;

namespace PrintNest.Api.Controllers.Staff;

[ApiController]
[Route("api/v1/staff/auth")]
public sealed class StaffAuthController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly IStaffPasswordService _passwords;
    private readonly IStaffTokenService _tokens;
    private readonly IConfiguration _config;

    public StaffAuthController(
        AppDbContext db,
        IStaffPasswordService passwords,
        IStaffTokenService tokens,
        IConfiguration config)
    {
        _db = db;
        _passwords = passwords;
        _tokens = tokens;
        _config = config;
    }

    [HttpPost("login")]
    public async Task<IActionResult> Login([FromBody] StaffLoginRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Username) || string.IsNullOrWhiteSpace(req.Password))
            throw new DomainException(ErrorCodes.AdminUnauthorized, "Unauthorized.", httpStatus: 401);

        var username = req.Username.Trim();
        var normalizedUsername = username.ToLowerInvariant();

        var user = await _db.StaffUsers
            .FirstOrDefaultAsync(x => x.Username.ToLower() == normalizedUsername && x.IsActive);

        if (user is null || !_passwords.Verify(req.Password, user.PasswordHash))
            throw new DomainException(ErrorCodes.AdminUnauthorized, "Unauthorized.", httpStatus: 401);

        var accessToken = _tokens.IssueAccessToken(new StaffTokenInput(
            StaffUserId: user.StaffUserId,
            Username: user.Username,
            Role: user.Role,
            StoreId: user.StoreId));

        var ttlMinutes = int.TryParse(_config["StaffAuth:AccessTokenTtlMinutes"], out var ttl) ? ttl : 480;

        user.LastLoginAtUtc = DateTime.UtcNow;
        user.UpdatedAtUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        return Ok(new
        {
            accessToken,
            tokenType = "Bearer",
            expiresInSeconds = ttlMinutes * 60,
            user = new
            {
                user.StaffUserId,
                user.Username,
                user.DisplayName,
                user.Role,
                user.StoreId
            }
        });
    }
}

public sealed record StaffLoginRequest(string Username, string Password);
