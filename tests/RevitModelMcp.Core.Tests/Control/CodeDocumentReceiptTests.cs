using RevitModelMcp.Core.Control;
using RevitModelMcp.Core.Models;
using RevitModelMcp.Core.Serialization;

namespace RevitModelMcp.Core.Tests.Control;

public sealed class CodeDocumentReceiptTests
{
    [Test]
    [Arguments("committed", true, false)]
    [Arguments("rolledBack", false, true)]
    public async Task UniformOutcomes_Aggregate(string outcome, bool committed, bool rolledBack)
    {
        var result = new ActionResultData();
        CodeDocumentReceipt.ApplyOutcome(result,
        [
            new CodeDocumentReceipt { Document = "First", Outcome = outcome },
            new CodeDocumentReceipt { Document = "Second", Outcome = outcome }
        ]);
        await Assert.That(result.Committed).IsEqualTo(committed);
        await Assert.That(result.RolledBack).IsEqualTo(rolledBack);
        await Assert.That(result.Warning).IsNull();
    }

    [Test]
    [Arguments("committed")]
    [Arguments("rolledBack")]
    [Arguments("unknown")]
    public async Task UnknownOutcome_OverridesAggregateSuccess(string knownOutcome)
    {
        var result = new ActionResultData { Committed = true, RolledBack = true, Summary = "Nothing changed." };
        CodeDocumentReceipt.ApplyOutcome(result,
        [
            new CodeDocumentReceipt { Document = "First", Outcome = knownOutcome },
            new CodeDocumentReceipt { Document = "Second", Added = 1 }
        ]);
        await Assert.That(result.Committed).IsEqualTo(false);
        await Assert.That(result.RolledBack).IsEqualTo(false);
        await Assert.That(result.Warning!).Contains("Changes may remain");
        await Assert.That(result.Summary!).Contains("unconfirmed document changes");
    }

    [Test]
    public async Task MixedOutcomes_DoNotClaimRollbackOrNoChanges()
    {
        var result = new ActionResultData { RolledBack = true, Summary = "Nothing changed." };
        CodeDocumentReceipt.ApplyOutcome(result,
        [
            new CodeDocumentReceipt { Document = "First", Outcome = "rolledBack" },
            new CodeDocumentReceipt { Document = "Second", Added = 1, Outcome = "committed" }
        ]);
        await Assert.That(result.Committed).IsEqualTo(false);
        await Assert.That(result.RolledBack).IsEqualTo(false);
        await Assert.That(result.Summary!).Contains("Committed changes remain");
    }

    [Test]
    public async Task EmptyReceipts_DoNotClaimSuccess()
    {
        var result = new ActionResultData();
        CodeDocumentReceipt.ApplyOutcome(result, []);
        await Assert.That(result.Committed).IsEqualTo(false);
        await Assert.That(result.RolledBack).IsEqualTo(false);
    }

    [Test]
    public async Task Serialize_PreservesPerDocumentOutcomesAndCounts()
    {
        var result = new ActionResultData
        {
            DocumentReceipts =
            [
                new CodeDocumentReceipt { Document = "First", Added = 2, Modified = 3, Deleted = 4, Outcome = "rolledBack" },
                new CodeDocumentReceipt { Document = "Second", Added = 1, Warning = "Rollback unconfirmed." }
            ]
        };
        var json = CommandResponseJsonSerializer.Serialize(CommandResponse<ActionResultData>.Ok("execute-code", result, 1));
        await Assert.That(json).Contains("\"documentReceipts\"");
        await Assert.That(json).Contains("\"document\":\"Second\"");
        await Assert.That(json).Contains("\"added\":2");
        await Assert.That(json).Contains("\"modified\":3");
        await Assert.That(json).Contains("\"deleted\":4");
        await Assert.That(json).Contains("\"outcome\":\"unknown\"");
        await Assert.That(json).Contains("Rollback unconfirmed.");
    }

    [Test]
    public async Task Serialize_ProcessCodePreservesDocumentReceipts()
    {
        var result = new ProcessModelCodeResult
        {
            DocumentReceipts = [new CodeDocumentReceipt { Document = "Second", Added = 1 }],
            Warning = "Changes may remain."
        };
        var json = CommandResponseJsonSerializer.Serialize(CommandResponse<ProcessModelCodeResult>.Ok("process-models", result, 1));
        await Assert.That(json).Contains("\"documentReceipts\"");
        await Assert.That(json).Contains("\"outcome\":\"unknown\"");
        await Assert.That(json).Contains("Changes may remain.");
    }
}
