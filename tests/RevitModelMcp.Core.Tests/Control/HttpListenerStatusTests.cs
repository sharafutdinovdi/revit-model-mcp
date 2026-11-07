using RevitModelMcp.Core.Control;

namespace RevitModelMcp.Core.Tests.Control;

public sealed class HttpListenerStatusTests
{
    [Test]
    [Arguments(false, false)]
    [Arguments(false, true)]
    [Arguments(true, false)]
    [Arguments(true, true)]
    public async Task RibbonAvailabilityIncludesEveryUsableTransport(bool pipeListening, bool fileEnabled)
    {
        foreach (var http in new[] { HttpListenerStatus.DisabledBySettings(), HttpListenerStatus.Started(), HttpListenerStatus.ConfigError("IOException") })
        {
            var status = ChannelRibbonStatus.Derive(pipeListening, http, fileEnabled);
            await Assert.That(status.IsAvailable).IsEqualTo(pipeListening || fileEnabled || http.State == HttpListenerStatus.Listening);
            await Assert.That(status.Pipe).IsEqualTo(pipeListening ? "listening" : "failed");
            await Assert.That(status.Http).IsEqualTo(http.State);
            await Assert.That(status.File).IsEqualTo(fileEnabled ? "listening" : "failed");
            await Assert.That(status.Description).IsEqualTo($"Pipe: {status.Pipe}. HTTP: {http.State}. File channel and discovery: {status.File}.");
        }
    }

    [Test]
    public async Task RibbonStatusTracksProbeFailureAndFileRecovery()
    {
        var started = ChannelRibbonStatus.Derive(false, HttpListenerStatus.Started(), false);
        await Assert.That(started.IsAvailable).IsTrue();
        var failed = ChannelRibbonStatus.Derive(false, HttpListenerStatus.FromSelfProbe(503, null), false);
        await Assert.That(failed.IsAvailable).IsFalse();
        var recovered = ChannelRibbonStatus.Derive(false, HttpListenerStatus.FromSelfProbe(503, null), true);
        await Assert.That(recovered.IsAvailable).IsTrue();
    }

    [Test]
    public async Task DisabledBySettings_ReportsReason()
    {
        var status = HttpListenerStatus.DisabledBySettings();
        await Assert.That(status.State).IsEqualTo(HttpListenerStatus.Disabled);
        await Assert.That(status.Reason).IsEqualTo("HTTP is disabled in settings.");
        await Assert.That(status.IsFailed).IsFalse();
    }

    [Test]
    public async Task DisabledByPolicy_NamesTheOrganizationPolicy()
    {
        var status = HttpListenerStatus.ForDisabled(false);
        await Assert.That(status.State).IsEqualTo(HttpListenerStatus.Disabled);
        await Assert.That(status.Reason).IsEqualTo("HTTP is disabled by organization policy.");
        await Assert.That(HttpListenerStatus.ForDisabled(null).Reason).IsEqualTo("HTTP is disabled in settings.");
        await Assert.That(HttpListenerStatus.ForDisabled(true).Reason).IsEqualTo("HTTP is disabled in settings.");
    }

    [Test]
    public async Task Started_HasNoReason()
    {
        var status = HttpListenerStatus.Started();
        await Assert.That(status.State).IsEqualTo(HttpListenerStatus.Listening);
        await Assert.That(status.Reason).IsNull();
        await Assert.That(status.IsFailed).IsFalse();
    }

    [Test]
    public async Task ConfigError_ReportsExceptionTypeAndSettingsChecks()
    {
        var status = HttpListenerStatus.ConfigError("ArgumentException");
        await Assert.That(status.State).IsEqualTo(HttpListenerStatus.Failed);
        await Assert.That(status.IsFailed).IsTrue();
        await Assert.That(status.Reason).IsEqualTo("HTTP settings could not be loaded or are invalid (ArgumentException). Check settings.json and the REVIT_MCP_HTTP_* variables.");
    }

    [Test]
    [Arguments(5)]
    [Arguments(32)]
    [Arguments(183)]
    [Arguments(87)]
    public async Task FromStartFailure_ReportsPrefixAndNativeError(int nativeErrorCode)
    {
        const string prefix = "http://127.0.0.1:53110/";
        var status = HttpListenerStatus.FromStartFailure(nativeErrorCode, prefix);
        await Assert.That(status.State).IsEqualTo(HttpListenerStatus.Failed);
        await Assert.That(status.IsFailed).IsTrue();
        await Assert.That(status.Reason).Contains(prefix);
        if (nativeErrorCode == 5)
        {
            await Assert.That(status.Reason).Contains("Access denied");
            await Assert.That(status.Reason).Contains("netsh http show urlacl");
            await Assert.That(status.Reason).Contains("netsh http add urlacl");
        }
        else if (nativeErrorCode is 32 or 183)
        {
            await Assert.That(status.Reason).Contains("port is already in use");
            await Assert.That(status.Reason).Contains("netsh http show servicestate");
        }
        else
        {
            await Assert.That(status.Reason).Contains($"error {nativeErrorCode}");
            await Assert.That(status.Reason).Contains("netsh http show urlacl");
            await Assert.That(status.Reason).Contains("netsh http show servicestate");
        }
    }

    [Test]
    public async Task FromSelfProbe_200_HasNoReason()
    {
        var status = HttpListenerStatus.FromSelfProbe(200, null);
        await Assert.That(status.State).IsEqualTo(HttpListenerStatus.Listening);
        await Assert.That(status.Reason).IsNull();
        await Assert.That(status.IsFailed).IsFalse();
    }

    [Test]
    public async Task FromSelfProbe_503_ReportsHttpSysChecks()
    {
        var status = HttpListenerStatus.FromSelfProbe(503, null);
        await Assert.That(status.State).IsEqualTo(HttpListenerStatus.Failed);
        await Assert.That(status.IsFailed).IsTrue();
        await Assert.That(status.Reason).Contains("HTTP.sys answered 503");
        await Assert.That(status.Reason).Contains("netsh http show urlacl");
        await Assert.That(status.Reason).Contains("netsh http show servicestate");
    }

    [Test]
    [Arguments(404)]
    [Arguments(204)]
    public async Task FromSelfProbe_OtherStatus_ReportsStatusCode(int statusCode)
    {
        var status = HttpListenerStatus.FromSelfProbe(statusCode, "WebException");
        await Assert.That(status.State).IsEqualTo(HttpListenerStatus.Failed);
        await Assert.That(status.IsFailed).IsTrue();
        await Assert.That(status.Reason).IsEqualTo($"The self-probe returned HTTP {statusCode}.");
    }

    [Test]
    public async Task FromSelfProbe_NoResponse_ReportsExceptionType()
    {
        var status = HttpListenerStatus.FromSelfProbe(null, "WebException");
        await Assert.That(status.State).IsEqualTo(HttpListenerStatus.Failed);
        await Assert.That(status.IsFailed).IsTrue();
        await Assert.That(status.Reason).IsEqualTo("The self-probe did not complete (WebException).");
    }
}
