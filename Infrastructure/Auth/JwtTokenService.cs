using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.IdentityModel.Tokens;
using PrintNest.Application.Interfaces;
using PrintNest.Domain.Errors;

namespace PrintNest.Infrastructure.Auth;

/// <summary>
/// Issues and validates short-lived file tokens (JWTs).
///
/// Token TTL: 120 seconds (configurable via "Jwt:FileTokenTtlSeconds").
/// Algorithm: HS256 with symmetric key from "Jwt:SigningKey".
///
/// The signing key must be at least 32 characters. App will throw on startup if it's shorter.
/// </summary>
public sealed class JwtTokenService : ITokenService
{
    private readonly JwtSecurityTokenHandler _handler = new();
    private readonly SymmetricSecurityKey _signingKey;
    private readonly int _tokenTtlSeconds;

    // Custom claim names — stable, never change these without updating ValidateFileToken too
    private const string ClaimDeviceId = "did";

    public JwtTokenService(IConfiguration config)
    {
        var signingKeyRaw = config["Jwt:SigningKey"]
            ?? throw new InvalidOperationException("Jwt:SigningKey is required in configuration.");

        if (signingKeyRaw.Length < 32)
            throw new InvalidOperationException("Jwt:SigningKey must be at least 32 characters.");

        _signingKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(signingKeyRaw));
        _tokenTtlSeconds = int.TryParse(config["Jwt:FileTokenTtlSeconds"], out var ttl) ? ttl : 120;
    }

    /// <summary>
    /// Issues a file token. Valid for _tokenTtlSeconds, bound to one device, single-use.
    /// </summary>
    public string IssueFileToken(Guid jobId, string deviceId)
    {
        var now = DateTime.UtcNow;
        var jti = Guid.NewGuid().ToString("N"); // compact, no dashes

        var token = new JwtSecurityToken(
            issuer: "printnest",
            audience: "printnest-device",
            claims: new[]
            {
                new Claim(JwtRegisteredClaimNames.Sub, jobId.ToString()),
                new Claim(ClaimDeviceId, deviceId),
                new Claim(JwtRegisteredClaimNames.Jti, jti),
                new Claim(JwtRegisteredClaimNames.Iat,
                    new DateTimeOffset(now).ToUnixTimeSeconds().ToString(),
                    ClaimValueTypes.Integer64)
            },
            notBefore: now,
            expires: now.AddSeconds(_tokenTtlSeconds),
            signingCredentials: new SigningCredentials(_signingKey, SecurityAlgorithms.HmacSha256)
        );

        return _handler.WriteToken(token);
    }

    /// <summary>
    /// Validates a file token. Throws DomainException on any failure.
    /// Does NOT check single-use — caller must check UsedFileTokens table.
    /// </summary>
    public FileTokenClaims ValidateFileToken(string token)
    {
        var validationParams = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = "printnest",
            ValidateAudience = true,
            ValidAudience = "printnest-device",
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = _signingKey,
            ValidateLifetime = true,
            ClockSkew = TimeSpan.Zero // strict — no grace period on 120s tokens
        };

        try
        {
            var principal = _handler.ValidateToken(token, validationParams, out _);

            var jobIdStr = principal.FindFirstValue(JwtRegisteredClaimNames.Sub)
                ?? principal.FindFirstValue(ClaimTypes.NameIdentifier)
                ?? throw new DomainException(ErrorCodes.TokenInvalid, "Token is missing job ID.", 401);

            var deviceId = principal.FindFirstValue(ClaimDeviceId)
                ?? throw new DomainException(ErrorCodes.TokenInvalid, "Token is missing device ID.", 401);

            var jti = principal.FindFirstValue(JwtRegisteredClaimNames.Jti)
                ?? principal.FindFirstValue(ClaimTypes.SerialNumber)
                ?? throw new DomainException(ErrorCodes.TokenInvalid, "Token is missing JTI.", 401);

            if (!Guid.TryParse(jobIdStr, out var jobId))
                throw new DomainException(ErrorCodes.TokenInvalid, "Token contains invalid job ID.", 401);

            return new FileTokenClaims(jobId, deviceId, jti);
        }
        catch (SecurityTokenExpiredException)
        {
            throw new DomainException(ErrorCodes.TokenExpired, "File token has expired.", 401);
        }
        catch (DomainException)
        {
            throw; // re-throw our own exceptions unchanged
        }
        catch
        {
            throw new DomainException(ErrorCodes.TokenInvalid, "File token is invalid.", 401);
        }
    }
}
