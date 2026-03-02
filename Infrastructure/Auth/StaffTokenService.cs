using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.IdentityModel.Tokens;
using PrintNest.Application.Interfaces;

namespace PrintNest.Infrastructure.Auth;

public sealed class StaffTokenService : IStaffTokenService
{
    internal const string Issuer = "printnest";
    internal const string Audience = "printnest-staff";
    internal const string ClaimStoreId = "store_id";

    private readonly JwtSecurityTokenHandler _handler = new();
    private readonly SymmetricSecurityKey _signingKey;
    private readonly int _accessTokenTtlMinutes;

    public StaffTokenService(IConfiguration config)
    {
        var signingKeyRaw = config["Jwt:SigningKey"]
            ?? throw new InvalidOperationException("Jwt:SigningKey is required in configuration.");

        if (signingKeyRaw.Length < 32)
            throw new InvalidOperationException("Jwt:SigningKey must be at least 32 characters.");

        _signingKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(signingKeyRaw));
        _accessTokenTtlMinutes = int.TryParse(config["StaffAuth:AccessTokenTtlMinutes"], out var ttl) ? ttl : 480;
    }

    public string IssueAccessToken(StaffTokenInput input)
    {
        var now = DateTime.UtcNow;
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, input.StaffUserId.ToString()),
            new(JwtRegisteredClaimNames.UniqueName, input.Username),
            new(ClaimTypes.Role, input.Role),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("N")),
            new(JwtRegisteredClaimNames.Iat, new DateTimeOffset(now).ToUnixTimeSeconds().ToString(), ClaimValueTypes.Integer64)
        };

        if (!string.IsNullOrWhiteSpace(input.StoreId))
            claims.Add(new Claim(ClaimStoreId, input.StoreId));

        var token = new JwtSecurityToken(
            issuer: Issuer,
            audience: Audience,
            claims: claims,
            notBefore: now,
            expires: now.AddMinutes(_accessTokenTtlMinutes),
            signingCredentials: new SigningCredentials(_signingKey, SecurityAlgorithms.HmacSha256));

        return _handler.WriteToken(token);
    }
}
