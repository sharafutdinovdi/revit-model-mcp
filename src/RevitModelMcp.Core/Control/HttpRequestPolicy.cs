using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace RevitModelMcp.Core.Control;

public static class HttpRequestPolicy
{
    public static int ValidateHeaders(string bindAddress, int port, IPAddress localAddress,
        string? host, string? origin, bool hasBody, string? contentType)
    {
        if (bindAddress is null) throw new ArgumentNullException(nameof(bindAddress));
        if (localAddress is null) throw new ArgumentNullException(nameof(localAddress));
        if (!TryAuthority(host, out var authority) || authority!.Port != port)
            return 400;
        var allowedAddress = bindAddress == "0.0.0.0" ? localAddress : IPAddress.Parse(bindAddress);
        var hostAllowed = IPAddress.TryParse(authority.Host, out var hostAddress)
            ? hostAddress.Equals(allowedAddress)
            : IPAddress.IsLoopback(allowedAddress) && authority.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase);
        if (!hostAllowed) return 400;
        if (origin is not null &&
            (!Uri.TryCreate(origin, UriKind.Absolute, out var originUri) ||
             originUri.Scheme != "http" || originUri.UserInfo.Length != 0 ||
             originUri.AbsolutePath != "/" || originUri.Query.Length != 0 || originUri.Fragment.Length != 0 ||
             !originUri.Authority.Equals(authority.Authority, StringComparison.OrdinalIgnoreCase)))
            return 403;
        if (hasBody && !string.Equals(contentType?.Split(';')[0].Trim(), "application/json", StringComparison.OrdinalIgnoreCase))
            return 415;
        return 0;
    }

    public static string RefusalReason(int statusCode) => statusCode switch
    {
        400 => "Host header is not allowed.",
        403 => "Origin header is not allowed.",
        415 => "Content-Type must be application/json.",
        _ => throw new ArgumentOutOfRangeException(nameof(statusCode))
    };

    public static bool Authenticated(string token, string? authorization)
    {
        if (token is null) throw new ArgumentNullException(nameof(token));
        if (authorization is null || !authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) return false;
        using var hash = SHA256.Create();
        var actual = hash.ComputeHash(Encoding.UTF8.GetBytes(authorization.Substring(7)));
        var expected = hash.ComputeHash(Encoding.UTF8.GetBytes(token));
        var difference = 0;
        for (var index = 0; index < expected.Length; index++) difference |= actual[index] ^ expected[index];
        return difference == 0;
    }

    private static bool TryAuthority(string? host, out Uri? authority)
    {
        authority = null;
        return !string.IsNullOrWhiteSpace(host) && host!.IndexOfAny(['/', '\\', '?', '#', '@', ',']) < 0 &&
               Uri.TryCreate("http://" + host + "/", UriKind.Absolute, out authority) &&
               authority.UserInfo.Length == 0;
    }
}
