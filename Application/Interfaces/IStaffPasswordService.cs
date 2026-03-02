namespace PrintNest.Application.Interfaces;

public interface IStaffPasswordService
{
    string Hash(string plaintextPassword);
    bool Verify(string plaintextPassword, string passwordHash);
}
