namespace PrintNest.Api.Middleware;

internal sealed record AuthenticatedStaffContext(
    Guid StaffUserId,
    string Username,
    string Role,
    string? StoreId
);
