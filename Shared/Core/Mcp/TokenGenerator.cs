using System;
using System.Security.Cryptography;

namespace Shared.Mcp;

// 128 cryptographically random bits as 32 lowercase hex characters: the session ids McpServer hands
// out, and the plugins' SecretKey.
public static class TokenGenerator
{
    public static string Generate()
    {
        var bytes = new byte[16];
        // The one form both net48 and net10 have: Fill and GetHexString are missing on net48, and
        // RNGCryptoServiceProvider is obsolete on net10 (SYSLIB0023).
        using (var rng = RandomNumberGenerator.Create())
            rng.GetBytes(bytes);
        return BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant();
    }
}
