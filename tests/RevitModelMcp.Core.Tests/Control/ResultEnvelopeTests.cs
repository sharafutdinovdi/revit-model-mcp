using System.Diagnostics;
using RevitModelMcp.Core.Control;

namespace RevitModelMcp.Core.Tests.Control;

public sealed class ResultEnvelopeTests
{
    [Test]
    [Arguments("{\"success\":true,\"partial\":false}", true, false)]
    [Arguments("{\"success\":false}", false, false)]
    [Arguments("{\"success\":false,\"partial\":true}", false, true)]
    [Arguments("{\"data\":{\"success\":true,\"partial\":true},\"success\":false}", false, false)]
    [Arguments("{\"data\":[{\"success\":true}],\"partial\":true}", false, true)]
    [Arguments("{}", false, false)]
    [Arguments("{\"success\":\"true\"}", true, false)]
    public async Task TryReadFlags_ReadsOnlyTopLevelFlags(string json, bool success, bool partial)
    {
        await Assert.That(ResultEnvelope.TryReadFlags(json, out var actualSuccess, out var actualPartial)).IsTrue();
        await Assert.That(actualSuccess).IsEqualTo(success);
        await Assert.That(actualPartial).IsEqualTo(partial);
    }

    [Test]
    [Arguments("")]
    [Arguments("[]")]
    [Arguments("{\"success\":true,")]
    [Arguments("{\"success\":true,\"data\":{invalid}}")]
    public async Task TryReadFlags_InvalidJson_ReturnsFalse(string json)
    {
        await Assert.That(ResultEnvelope.TryReadFlags(json, out var success, out var partial)).IsFalse();
        await Assert.That(success).IsFalse();
        await Assert.That(partial).IsFalse();
    }

    [Test]
    public async Task TryReadFlags_LargePayload_DoesNotMaterializeData()
    {
        var json = "{\"data\":{\"text\":\"" + new string('x', 5 * 1024 * 1024) +
                   "\",\"success\":false},\"success\":true,\"partial\":false}";
        ResultEnvelope.TryReadFlags("{}", out _, out _);
        var stopwatch = Stopwatch.StartNew();
        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        var read = ResultEnvelope.TryReadFlags(json, out var success, out var partial);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        stopwatch.Stop();
        Console.WriteLine($"Result envelope: {stopwatch.Elapsed.TotalMilliseconds:F1} ms, {allocated} bytes allocated.");
        await Assert.That(read).IsTrue();
        await Assert.That(success).IsTrue();
        await Assert.That(partial).IsFalse();
        await Assert.That(stopwatch.ElapsedMilliseconds).IsLessThan(1000);
        await Assert.That(allocated).IsLessThan(10L * 1024 * 1024);
    }
}
