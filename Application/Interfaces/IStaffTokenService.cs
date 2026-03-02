namespace PrintNest.Application.Interfaces;

public interface IStaffTokenService
{
    string IssueAccessToken(StaffTokenInput input);
}

public sealed record StaffTokenInput(
    Guid StaffUserId,
    string Username,
    string Role,
    string? StoreId
);
