using RevitModelMcp.Core.Control;

namespace RevitModelMcp.Core.Tests.Control;

public sealed class HttpLimitsTests
{
    [Test]
    public async Task DefaultsMatchConstants()
    {
        await Assert.That(HttpLimits.Default.MaxStoredResponses).IsEqualTo(HttpLimits.DefaultMaxStoredResponses);
        await Assert.That(HttpLimits.Default.MaxStoredBytes).IsEqualTo(HttpLimits.DefaultMaxStoredBytes);
        await Assert.That(HttpLimits.Default.RequestReadTimeoutSeconds).IsEqualTo(HttpLimits.DefaultRequestReadTimeoutSeconds);
        await Assert.That(HttpLimits.Default.ShutdownDrainSeconds).IsEqualTo(HttpLimits.DefaultShutdownDrainSeconds);
        await Assert.That(HttpLimits.Default.Validate()).IsNull();
    }

    [Test]
    public async Task BoundariesAreInclusiveAndErrorsNameSettings()
    {
        HttpLimits[] valid =
        [
            HttpLimits.Default with { MaxStoredResponses = 1 },
            HttpLimits.Default with { MaxStoredResponses = 10000 },
            HttpLimits.Default with { MaxStoredBytes = 1048576 },
            HttpLimits.Default with { MaxStoredBytes = 1073741824 },
            HttpLimits.Default with { RequestReadTimeoutSeconds = 1 },
            HttpLimits.Default with { RequestReadTimeoutSeconds = 300 },
            HttpLimits.Default with { ShutdownDrainSeconds = 0 },
            HttpLimits.Default with { ShutdownDrainSeconds = 60 }
        ];
        foreach (var limits in valid) await Assert.That(limits.Validate()).IsNull();
        (HttpLimits Limits, string Setting)[] invalid =
        [
            (HttpLimits.Default with { MaxStoredResponses = 0 }, "maxStoredResponses"),
            (HttpLimits.Default with { MaxStoredResponses = 10001 }, "maxStoredResponses"),
            (HttpLimits.Default with { MaxStoredBytes = 1048575 }, "maxStoredBytes"),
            (HttpLimits.Default with { MaxStoredBytes = 1073741825 }, "maxStoredBytes"),
            (HttpLimits.Default with { RequestReadTimeoutSeconds = 0 }, "requestReadTimeoutSeconds"),
            (HttpLimits.Default with { RequestReadTimeoutSeconds = 301 }, "requestReadTimeoutSeconds"),
            (HttpLimits.Default with { ShutdownDrainSeconds = -1 }, "shutdownDrainSeconds"),
            (HttpLimits.Default with { ShutdownDrainSeconds = 61 }, "shutdownDrainSeconds")
        ];
        foreach (var (limits, setting) in invalid) await Assert.That(limits.Validate()!).Contains(setting);
    }
}
