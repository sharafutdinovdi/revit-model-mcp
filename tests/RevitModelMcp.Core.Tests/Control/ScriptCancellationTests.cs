using System.Text.Json;
using RevitModelMcp.Core.Control;
using RevitModelMcp.Core.Models;
using RevitModelMcp.Core.Serialization;

namespace RevitModelMcp.Core.Tests.Control;

public sealed class ScriptCancellationTests
{
    [Test]
    public async Task ExecutionLimitIsOptionalAndChecksTheBoundary()
    {
        await Assert.That(CodeSource.CancellationRequested(false, TimeSpan.FromDays(1), null)).IsFalse();
        await Assert.That(CodeSource.CancellationRequested(false, TimeSpan.FromMilliseconds(1999), 2)).IsFalse();
        await Assert.That(CodeSource.CancellationRequested(false, TimeSpan.FromSeconds(2), 2)).IsTrue();
        await Assert.That(CodeSource.CancellationRequested(true, TimeSpan.Zero, null)).IsTrue();
        CodeSource.ThrowIfCancellationRequested(false, TimeSpan.FromDays(1), null);
        await Assert.That(() => CodeSource.ThrowIfCancellationRequested(false, TimeSpan.FromSeconds(2), 2))
            .Throws<OperationCanceledException>().WithMessage("Script exceeded max_seconds=2 after 2.000 seconds.");
    }

    [Test]
    public async Task CancellationFailureIncludesElapsedTimeAndRollbackReceipt()
    {
        try
        {
            CodeSource.ThrowIfCancellationRequested(true, TimeSpan.FromSeconds(3), null);
            throw new InvalidOperationException("Cancellation was ignored.");
        }
        catch (OperationCanceledException exception)
        {
            var data = new ActionResultData
            {
                CodeError = exception.GetType().Name + ": " + exception.Message,
                ElapsedMs = 3000,
                DocumentReceipts = [new CodeDocumentReceipt { Document = "Project", Outcome = "rolledBack" }]
            };
            var response = CommandResponse<ActionResultData>.Fail("execute-code", data.CodeError, 3000);
            response.Data = data;
            response.Error = data.CodeError;
            using var json = JsonDocument.Parse(CommandResponseJsonSerializer.Serialize(response));
            await Assert.That(json.RootElement.GetProperty("success").GetBoolean()).IsFalse();
            await Assert.That(json.RootElement.GetProperty("error").GetString()).Contains("cancelled after 3.000 seconds");
            await Assert.That(json.RootElement.GetProperty("data").GetProperty("elapsedMs").GetInt64()).IsEqualTo(3000);
            await Assert.That(json.RootElement.GetProperty("data").GetProperty("documentReceipts")[0].GetProperty("outcome").GetString())
                .IsEqualTo("rolledBack");
        }
    }

    [Test]
    [Arguments(1)]
    [Arguments(2)]
    public async Task ParserCarriesExecutionLimit(int seconds)
    {
        var job = ControlJobParser.Parse($$"""{"command":"execute-code","code":"return 1;","maxSeconds":{{seconds}}}""");
        await Assert.That(job.Action!.MaxSeconds).IsEqualTo(seconds);
        await Assert.That(ControlJobParser.Parse("""{"command":"execute-code","code":"return 1;","maxSeconds":0}""").Kind)
            .IsEqualTo(ControlJobKind.Invalid);
    }
}
