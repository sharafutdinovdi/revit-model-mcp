using System.Net;
using RevitModelMcp.Core.Control;

namespace RevitModelMcp.Core.Tests.Control;

public sealed class HttpRequestPolicyTests
{
    [Test]
    [Arguments("127.0.0.1", "127.0.0.1", "127.0.0.1:53110", null, false, null, 0)]
    [Arguments("127.0.0.1", "127.0.0.1", "localhost:53110", "http://localhost:53110", true, "application/json", 0)]
    [Arguments("127.0.0.1", "127.0.0.1", "LOCALHOST:53110", null, true, "Application/Json; charset=utf-8", 0)]
    [Arguments("127.0.0.1", "127.0.0.1", "attacker.example:53110", null, false, null, 400)]
    [Arguments("127.0.0.1", "127.0.0.1", "127.0.0.1:80", null, false, null, 400)]
    [Arguments("127.0.0.1", "127.0.0.1", null, null, false, null, 400)]
    [Arguments("127.0.0.1", "127.0.0.1", "127.0.0.1:53110@evil", null, false, null, 400)]
    [Arguments("127.0.0.1", "127.0.0.1", "127.0.0.1:53110/path", null, false, null, 400)]
    [Arguments("127.0.0.1", "127.0.0.1", "127.0.0.1:53110", "http://evil.example", false, null, 403)]
    [Arguments("127.0.0.1", "127.0.0.1", "127.0.0.1:53110", "null", false, null, 403)]
    [Arguments("127.0.0.1", "127.0.0.1", "127.0.0.1:53110", "", false, null, 403)]
    [Arguments("127.0.0.1", "127.0.0.1", "127.0.0.1:53110", "https://127.0.0.1:53110", false, null, 403)]
    [Arguments("127.0.0.1", "127.0.0.1", "127.0.0.1:53110", "http://127.0.0.1:53110/path", false, null, 403)]
    [Arguments("127.0.0.1", "127.0.0.1", "127.0.0.1:53110", "http://127.0.0.1:53111", false, null, 403)]
    [Arguments("127.0.0.1", "127.0.0.1", "127.0.0.1:53110", "http://localhost:53110", false, null, 403)]
    [Arguments("192.0.2.10", "192.0.2.10", "192.0.2.10:53110", "http://192.0.2.10:53110", true, "application/json", 0)]
    [Arguments("192.0.2.10", "192.0.2.10", "localhost:53110", null, false, null, 400)]
    [Arguments("192.0.2.10", "192.0.2.10", "workstation:53110", null, false, null, 400)]
    [Arguments("0.0.0.0", "192.0.2.10", "192.0.2.10:53110", null, true, "application/json", 0)]
    [Arguments("0.0.0.0", "127.0.0.1", "localhost:53110", null, false, null, 0)]
    [Arguments("0.0.0.0", "192.0.2.10", "0.0.0.0:53110", null, false, null, 400)]
    [Arguments("0.0.0.0", "192.0.2.10", "198.51.100.1:53110", null, false, null, 400)]
    [Arguments("127.0.0.1", "127.0.0.1", "127.0.0.1:53110", null, true, "text/plain", 415)]
    [Arguments("127.0.0.1", "127.0.0.1", "127.0.0.1:53110", null, true, "application/x-www-form-urlencoded", 415)]
    [Arguments("127.0.0.1", "127.0.0.1", "127.0.0.1:53110", null, true, "multipart/form-data; boundary=test", 415)]
    [Arguments("127.0.0.1", "127.0.0.1", "127.0.0.1:53110", null, true, "application/jsonp", 415)]
    [Arguments("127.0.0.1", "127.0.0.1", "127.0.0.1:53110", null, true, null, 415)]
    [Arguments("127.0.0.1", "127.0.0.1", "evil:53110", "http://evil", true, "text/plain", 400)]
    [Arguments("127.0.0.1", "127.0.0.1", "127.0.0.1:53110", "http://evil", true, "text/plain", 403)]
    public async Task ValidateHeaders_EnforcesBoundary(string bindAddress, string localAddress, string? host,
        string? origin, bool hasBody, string? contentType, int expectedStatus)
    {
        var status = HttpRequestPolicy.ValidateHeaders(bindAddress, 53110, IPAddress.Parse(localAddress),
            host, origin, hasBody, contentType);

        await Assert.That(status).IsEqualTo(expectedStatus);
    }

    [Test]
    [Arguments(null, false)]
    [Arguments("", false)]
    [Arguments("Bearer wrong", false)]
    [Arguments("Basic secret", false)]
    [Arguments("Bearer secret", true)]
    [Arguments("bearer secret", true)]
    public async Task Health_RequiresValidBearerToken(string? authorization, bool expected)
    {
        await Assert.That(HttpRequestPolicy.Authenticated("secret", authorization)).IsEqualTo(expected);
    }

    [Test]
    [Arguments(400, "Host header is not allowed.")]
    [Arguments(403, "Origin header is not allowed.")]
    [Arguments(415, "Content-Type must be application/json.")]
    public async Task RefusalReason_NamesOnlyTheRule(int status, string expected)
    {
        await Assert.That(HttpRequestPolicy.RefusalReason(status)).IsEqualTo(expected);
    }
}
