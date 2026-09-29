using System.Security.Cryptography;
using System.Text;

namespace RevitModelMcp.Core.Control;

public static class HealthProof
{
    private static readonly byte[] MessagePrefix = Encoding.ASCII.GetBytes("revit-model-mcp/health/v1\n");

    public static bool TryCompute(string token, string? encodedNonce, out string? proof)
    {
        if (token is null) throw new ArgumentNullException(nameof(token));
        proof = null;
        if (encodedNonce is null || encodedNonce.Length is < 2 or > 86) return false;
        foreach (var character in encodedNonce)
        {
            if (character is not (>= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '_'))
                return false;
        }

        byte[] nonce;
        try
        {
            var base64 = encodedNonce.Replace('-', '+').Replace('_', '/');
            nonce = Convert.FromBase64String(base64.PadRight((base64.Length + 3) / 4 * 4, '='));
        }
        catch (FormatException)
        {
            return false;
        }
        if (nonce.Length is < 1 or > 64) return false;

        var message = new byte[MessagePrefix.Length + nonce.Length];
        Buffer.BlockCopy(MessagePrefix, 0, message, 0, MessagePrefix.Length);
        Buffer.BlockCopy(nonce, 0, message, MessagePrefix.Length, nonce.Length);
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(token));
        proof = Convert.ToBase64String(hmac.ComputeHash(message)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return true;
    }
}
