using System;
using System.Security.Cryptography;

namespace Shared.Mcp;

// Cryptographic random hex token. 16 bytes = 128 bits, formatted as
// 32 lowercase hex chars (BitConverter then strips "-"). Both lanes use it:
// - McpServer.HandleRequest mints a fresh session id on every `initialize`.
// - Client Config seeds an empty SecretKey on first load.
public static class TokenGenerator
{
    public static string Generate()
    {
        var bytes = new byte[16];
        // RandomNumberGenerator.Create() returns the abstract base, sidestepping
        // the SYSLIB0023 obsoletion of RNGCryptoServiceProvider on net10.0 while
        // staying compatible with net48 (the static factory and the GetBytes
        // instance method are both available there). The newer
        // RandomNumberGenerator.Fill(Span<byte>) / .GetHexString() are net6+/net9+
        // only, so they would force #if-fences for no real win.
        using (var rng = RandomNumberGenerator.Create())
            rng.GetBytes(bytes);
        return BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant();
    }
}
