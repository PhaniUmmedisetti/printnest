using Isopoh.Cryptography.Argon2;
using PrintNest.Application.Interfaces;
using System.Security.Cryptography;
using System.Text;

namespace PrintNest.Infrastructure.Auth;

/// <summary>
/// Argon2id password hashing for staff logins.
/// </summary>
public sealed class StaffPasswordService : IStaffPasswordService
{
    private const int MemorySize = 131072; // 128MB
    private const int Iterations = 3;
    private const int Parallelism = 1;
    private const int HashLength = 32;

    public string Hash(string plaintextPassword)
    {
        if (string.IsNullOrWhiteSpace(plaintextPassword))
            throw new InvalidOperationException("Password cannot be empty.");

        var config = new Argon2Config
        {
            Type = Argon2Type.HybridAddressing,
            Version = Argon2Version.Nineteen,
            MemoryCost = MemorySize,
            TimeCost = Iterations,
            Lanes = Parallelism,
            Threads = Parallelism,
            Password = Encoding.UTF8.GetBytes(plaintextPassword),
            Salt = RandomNumberGenerator.GetBytes(16),
            HashLength = HashLength
        };

        using var argon2 = new Argon2(config);
        using var hash = argon2.Hash();
        return config.EncodeString(hash.Buffer);
    }

    public bool Verify(string plaintextPassword, string passwordHash)
    {
        try
        {
            return Argon2.Verify(passwordHash, plaintextPassword);
        }
        catch
        {
            return false;
        }
    }
}
