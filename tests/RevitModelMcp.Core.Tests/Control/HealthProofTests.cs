using RevitModelMcp.Core.Control;

namespace RevitModelMcp.Core.Tests.Control;

public sealed class HealthProofTests
{
    [Test]
    public async Task TryCompute_FixedTokenAndNonce_MatchesKnownProof()
    {
        const string nonce = "AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8";

        var valid = HealthProof.TryCompute("test-token", nonce, out var proof);

        await Assert.That(valid).IsTrue();
        await Assert.That(proof).IsEqualTo("GfUwWjTyg3_Dag6VadPsgEniE7ybFFAH9msV89o4grU");
    }

    [Test]
    public async Task TryCompute_RejectsInvalidNonce()
    {
        foreach (var nonce in new string?[] { null, string.Empty, "*", "A", new string('A', 87), Convert.ToBase64String(new byte[65]).TrimEnd('=') })
        {
            var valid = HealthProof.TryCompute("test-token", nonce, out var proof);
            await Assert.That(valid).IsFalse();
            await Assert.That(proof).IsNull();
        }
    }

    [Test]
    public async Task TryCompute_AcceptsMaximumNonceLength()
    {
        var nonce = Convert.ToBase64String(new byte[64]).TrimEnd('=').Replace('+', '-').Replace('/', '_');

        await Assert.That(HealthProof.TryCompute("test-token", nonce, out var proof)).IsTrue();
        await Assert.That(proof).IsNotNull();
    }
}
