using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.IdentityModel.Tokens;
using PrintNest.Domain.Entities;
using PrintNest.Domain.Errors;
using PrintNest.Infrastructure.Auth;

namespace PrintNest.Api.Middleware;

/// <summary>
/// Protects /api/v1/admin/* endpoints with JWT bearer staff authentication.
/// </summary>
public sealed class StaffAuthMiddleware
{
    private readonly RequestDelegate _next;
    private readonly TokenValidationParameters _validationParameters;
    private readonly JwtSecurityTokenHandler _handler = new();

    public StaffAuthMiddleware(RequestDelegate next, IConfiguration config)
    {
        _next = next;

        var signingKeyRaw = config["Jwt:SigningKey"]
            ?? throw new InvalidOperationException("Jwt:SigningKey is required in configuration.");

        if (signingKeyRaw.Length < 32)
            throw new InvalidOperationException("Jwt:SigningKey must be at least 32 characters.");

        _validationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = StaffTokenService.Issuer,
            ValidateAudience = true,
            ValidAudience = StaffTokenService.Audience,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(signingKeyRaw)),
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromSeconds(30)
        };
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var authHeader = context.Request.Headers.Authorization.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(authHeader) || !authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            throw new DomainException(ErrorCodes.AdminUnauthorized, "Unauthorized.", httpStatus: 401);

        var token = authHeader["Bearer ".Length..].Trim();
        ClaimsPrincipal principal;
        try
        {
            principal = _handler.ValidateToken(token, _validationParameters, out _);
        }
        catch
        {
            throw new DomainException(ErrorCodes.AdminUnauthorized, "Unauthorized.", httpStatus: 401);
        }

        var staffIdRaw = principal.FindFirstValue(JwtRegisteredClaimNames.Sub)
            ?? principal.FindFirstValue(ClaimTypes.NameIdentifier);
        var username = principal.FindFirstValue(JwtRegisteredClaimNames.UniqueName)
            ?? principal.FindFirstValue(ClaimTypes.Name);
        var role = principal.FindFirstValue(ClaimTypes.Role) ?? principal.FindFirstValue("role");
        var storeId = principal.FindFirstValue(StaffTokenService.ClaimStoreId);

        if (!Guid.TryParse(staffIdRaw, out var staffUserId) ||
            string.IsNullOrWhiteSpace(username) ||
            string.IsNullOrWhiteSpace(role) ||
            !StaffRoles.IsValid(role))
        {
            throw new DomainException(ErrorCodes.AdminUnauthorized, "Unauthorized.", httpStatus: 401);
        }

        context.Items["AuthenticatedStaff"] = new AuthenticatedStaffContext(
            StaffUserId: staffUserId,
            Username: username,
            Role: role,
            StoreId: storeId);

        await _next(context);
    }
}
