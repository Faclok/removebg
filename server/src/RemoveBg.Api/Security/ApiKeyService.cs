using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.WebUtilities;

namespace RemoveBg.Api.Security;

public static class ApiKeyService
{
    public static (string Raw, string Prefix, string Hash) Issue()
    {
        var raw = $"rb_live_{WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32))}";
        return (raw, raw[..16], Hash(raw));
    }

    public static string Hash(string raw) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw))).ToLowerInvariant();
}
