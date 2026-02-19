using Isopoh.Cryptography.Argon2;
using PrintNest.Application.Interfaces;
using System.Security.Cryptography;

namespace PrintNest.Infrastructure.Auth;

/// <summary>
/// Generates and verifies 6-digit OTPs using Argon2id hashing.
///
/// Argon2id is chosen because:
///   - Resistant to GPU brute force (memory-hard)
///   - Recommended by OWASP for password/PIN hashing
///
/// Parameters are set conservatively for MVP running on a Pi/low-spec server.
/// Increase memory and iterations for production.
/// </summary>
public sealed class OtpService : IOtpService
{
    // Argon2id parameters — balance between security and performance on low-spec hardware
    private const int MemorySize = 65536;  // 64 MB
    private const int Iterations = 3;
    private const int Parallelism = 1;
    private const int HashLength = 32;

    /// <summary>
    /// Generates a 6-digit numeric OTP using a cryptographically secure RNG.
    /// Range: 100000–999999 (always 6 digits, no leading zeros).
    /// </summary>
    public OtpResult Generate()
    {
        // Use cryptographically secure random number
        var plaintext = RandomNumberGenerator.GetInt32(100000, 1000000).ToString();
        var hash = HashOtp(plaintext);
        return new OtpResult(plaintext, hash);
    }

    /// <summary>
    /// Verifies a plaintext OTP against a stored Argon2id hash.
    /// This is intentionally slow (~100-300ms) — that's the security feature.
    /// </summary>
    public bool Verify(string plaintext, string storedHash)
    {
        try
        {
            return Argon2.Verify(storedHash, plaintext);
        }
        catch
        {
            // If hash is malformed, treat as verification failure
            return false;
        }
    }

    private static string HashOtp(string plaintext)
    {
        var config = new Argon2Config
        {
            Type = Argon2Type.HybridAddressing, // Argon2id
            Version = Argon2Version.Nineteen,
            MemoryCost = MemorySize,
            TimeCost = Iterations,
            Lanes = Parallelism,
            Threads = Parallelism,
            Password = System.Text.Encoding.UTF8.GetBytes(plaintext),
            HashLength = HashLength
        };

        using var argon2 = new Argon2(config);
        using var hash = argon2.Hash();
        return config.EncodeString(hash.Buffer);
    }
}
