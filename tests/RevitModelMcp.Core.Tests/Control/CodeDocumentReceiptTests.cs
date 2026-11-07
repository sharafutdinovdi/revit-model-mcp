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
        await Assert.That(result.Committed).IsFalse();
        await Assert.That(result.RolledBack).IsFalse();
        await Assert.That(result.Warning!).Contains("Changes may remain");
        await Assert.That(result.Summary!).Contains("unconfirmed document changes");
    }

    [Test]
    public async Task MixedOutcomes_DoNotClaimRollbackOrNoChanges()
    {
        var result = new ActionResultData { RolledBack = true, Summary = "Nothing changed." };
        CodeDocumentReceipt.ApplyOutcome(result,
        [
            new CodeDocumentReceipt { Document = "First", Added = 1, Outcome = "rolledBack" },
            new CodeDocumentReceipt { Document = "Second", Added = 1, Outcome = "committed" }
        ]);
        await Assert.That(result.Committed).IsFalse();
        await Assert.That(result.RolledBack).IsFalse();
        await Assert.That(result.Summary!).Contains("Committed changes remain");
    }

    [Test]
    public async Task EmptyReceipts_DoNotClaimSuccess()
    {
        var result = new ActionResultData();
        CodeDocumentReceipt.ApplyOutcome(result, []);
        await Assert.That(result.Committed).IsFalse();
        await Assert.That(result.RolledBack).IsFalse();
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

    [Test]
    [Arguments(false, false, false, true)]
    [Arguments(false, false, true, false)]
    [Arguments(false, true, false, false)]
    [Arguments(false, true, true, false)]
    [Arguments(true, false, false, false)]
    [Arguments(true, false, true, false)]
    [Arguments(true, true, false, false)]
    [Arguments(true, true, true, false)]
    public async Task ProtectionPlan_OnlyGroupsWritableDocumentsWithoutAnOpenTransaction(
        bool isLinked, bool isReadOnly, bool isModifiable, bool expected)
    {
        await Assert.That(CodeDocumentReceipt.ShouldProtect(isLinked, isReadOnly, isModifiable)).IsEqualTo(expected);
    }

    [Test]
    [Arguments(true, true, true, "rolledBack")]
    [Arguments(true, true, false, "rolledBack")]
    [Arguments(false, true, true, "committed")]
    [Arguments(false, true, false, "rolledBack")]
    [Arguments(true, false, true, "unknown")]
    [Arguments(true, false, false, "unknown")]
    [Arguments(false, false, true, "unknown")]
    [Arguments(false, false, false, "unknown")]
    public async Task GroupOutcome_RequiresConfirmedCompletion(bool rollback, bool confirmed, bool changed, string expected)
    {
        await Assert.That(CodeDocumentReceipt.GroupOutcome(rollback, confirmed, changed)).IsEqualTo(expected);
    }

    [Test]
    public async Task EmptySiblingGroup_DoesNotInvalidateCommittedChanges()
    {
        var result = new ActionResultData();
        CodeDocumentReceipt.ApplyOutcome(result,
        [
            new CodeDocumentReceipt { Document = "Changed", Modified = 1, Outcome = "committed" },
            new CodeDocumentReceipt { Document = "Empty", Outcome = "rolledBack" }
        ]);
        await Assert.That(result.Committed).IsTrue();
        await Assert.That(result.RolledBack).IsFalse();
        await Assert.That(result.Warning).IsNull();
    }

    [Test]
    [Arguments("leftOpen")]
    [Arguments("closed")]
    public async Task OpenedDocument_KeepsItsKnownLifecycleOutcome(string outcome)
    {
        var result = new ActionResultData();
        CodeDocumentReceipt.ApplyOutcome(result,
        [
            new CodeDocumentReceipt { Document = "Existing", Outcome = "rolledBack" },
            new CodeDocumentReceipt { Document = "Created", OpenedByCall = true, Outcome = outcome }
        ]);
        await Assert.That(result.Committed).IsTrue();
        await Assert.That(result.RolledBack).IsFalse();
        await Assert.That(result.Warning).IsNull();
    }

    [Test]
    public async Task Serialize_SameTitleDocumentsHaveDistinctSessionKeys()
    {
        var result = new ActionResultData
        {
            DocumentReceipts =
            [
                new CodeDocumentReceipt { Document = "Project", DocumentKey = "00000001" },
                new CodeDocumentReceipt { Document = "Project", DocumentKey = "00000002", OpenedByCall = true }
            ]
        };
        var json = CommandResponseJsonSerializer.Serialize(CommandResponse<ActionResultData>.Ok("execute-code", result, 1));
        await Assert.That(json).Contains("\"documentKey\":\"00000001\"");
        await Assert.That(json).Contains("\"documentKey\":\"00000002\"");
        await Assert.That(json).Contains("\"openedByCall\":true");
    }

    [Test]
    [Arguments("auto", false, false, false, true)]
    [Arguments("auto", false, false, true, false)]
    [Arguments("auto", true, false, false, false)]
    [Arguments("auto", true, false, true, false)]
    [Arguments("auto", false, true, false, false)]
    [Arguments("auto", false, true, true, false)]
    [Arguments("auto", true, true, false, false)]
    [Arguments("auto", true, true, true, false)]
    [Arguments("group", false, false, false, true)]
    [Arguments("group", false, false, true, false)]
    [Arguments("group", true, false, false, false)]
    [Arguments("group", true, false, true, false)]
    [Arguments("group", false, true, false, false)]
    [Arguments("group", false, true, true, false)]
    [Arguments("group", true, true, false, false)]
    [Arguments("group", true, true, true, false)]
    [Arguments("none", false, false, false, false)]
    [Arguments("none", false, false, true, false)]
    [Arguments("none", true, true, false, false)]
    public async Task ProtectionPlan_RefusesUnprotectedWritableDocumentsOnlyInManagedModes(
        string mode, bool isLinked, bool isReadOnly, bool protectionStarted, bool expected)
    {
        await Assert.That(CodeDocumentReceipt.RefusesUnprotectedDocument(mode, isLinked, isReadOnly, protectionStarted))
            .IsEqualTo(expected);
    }
}
