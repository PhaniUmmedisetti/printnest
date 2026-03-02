namespace PrintNest.Domain.Entities;

public static class StaffRoles
{
    public const string SuperAdmin = "SUPER_ADMIN";
    public const string StoreManager = "STORE_MANAGER";

    public static bool IsValid(string role)
        => role == SuperAdmin || role == StoreManager;
}
