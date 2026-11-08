using System.Text.Json;
using RevitModelMcp.Core.Control;
using RevitModelMcp.Core.Models;
using RevitModelMcp.Core.Serialization;

namespace RevitModelMcp.Core.Tests.Control;

public sealed class OwnedDocumentCleanupTests
{
    [Test]
    [Arguments("new", true)]
    [Arguments("existing", false)]
    [Arguments("owned", false)]
    [Arguments("closed", false)]
    [Arguments("refused", false)]
    [Arguments("unrelated", false)]
    public async Task FindUntrackedActivationDocuments_TracksOnlyNewUnaccountedMatchingDocuments(
        string candidate, bool expected)
    {
        var comparer = new DocumentIdentityComparer<string>((first, second) => first == second);
        var documents = OwnedDocumentCleanup.FindUntrackedActivationDocuments(
            [new string(candidate.ToCharArray())], ["existing"], ["owned"], comparer,
            [
                new DocumentClosureResult
                {
                    Identity = "closed",
                    Closed = true
                },
                new DocumentClosureResult
                {
                    Identity = "refused",
                    Closed = false
                }
            ], document => document, document => document != "unrelated").ToList();

        await Assert.That(documents.Count).IsEqualTo(expected ? 1 : 0);
        if (expected) await Assert.That(documents.Single()).IsEqualTo(candidate);
    }

    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task Close_ReportsClosureResult(bool closed)
    {
        var document = new object();
        var calls = 0;
        var result = OwnedDocumentCleanup.Close(document, [], EqualityComparer<object>.Default, "Family [42]", candidate =>
        {
            if (!ReferenceEquals(candidate, document)) throw new InvalidOperationException("Unexpected document.");
            calls++;
            return closed;
        });

        await Assert.That(calls).IsEqualTo(1);
        await Assert.That(result!.Closed).IsEqualTo(closed);
        await Assert.That(result.Identity).IsEqualTo("Family [42]");
        await Assert.That(result.Error).IsNull();
        if (!closed) await Assert.That(result.ToString()).Contains("Retained document 'Family [42]'");
    }

    [Test]
    public async Task Close_PreExistingDocumentDoesNotInvokeClosure()
    {
        var document = new object();
        var result = OwnedDocumentCleanup.Close(document, [document], EqualityComparer<object>.Default, "Family [42]",
            _ => throw new InvalidOperationException("Must remain open."));

        await Assert.That(result).IsNull();
    }

    [Test]
    public async Task Close_UsesDocumentIdentityAcrossWrappers()
    {
        var comparer = new DocumentIdentityComparer<string>((first, second) => first == second);
        var result = OwnedDocumentCleanup.Close(new string('a', 3), [new string('a', 3)], comparer, "Family [42]",
            _ => throw new InvalidOperationException("Must remain open."));

        await Assert.That(result).IsNull();
    }

    [Test]
    public async Task Close_ExceptionReportsRetainedIdentityWithoutExceptionDetails()
    {
        var result = OwnedDocumentCleanup.Close(new object(), [], EqualityComparer<object>.Default, "Family [42]",
            _ => throw new InvalidOperationException("confidential path"));

        await Assert.That(result!.Closed).IsFalse();
        await Assert.That(result.Identity).IsEqualTo("Family [42]");
        await Assert.That(result.Error).IsEqualTo(nameof(InvalidOperationException));
        await Assert.That(result.ToString()).Contains("closure failed (InvalidOperationException)");
        await Assert.That(result.ToString()).DoesNotContain("confidential path");
    }

    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task ApplyClosure_OnlyRetainedDocumentsRequireBatchStop(bool closed)
    {
        var family = new FamilyEditFamily
        {
            Name = "Family",
            Status = "changed",
            Reason = "Original failure."
        };
        family.ApplyClosure(new DocumentClosureResult
        {
            Identity = "Family [42]",
            Closed = closed
        });
        var batch = new FamilyEditData { Families = [family] };

        await Assert.That(batch.HasRetainedDocument).IsEqualTo(!closed);
        await Assert.That(family.Status).IsEqualTo(closed ? "changed" : "failed");
        await Assert.That(family.Reason).Contains("Original failure.");
        if (!closed) await Assert.That(family.Reason).Contains("Retained document 'Family [42]'");
    }

    [Test]
    public async Task ApplyClosure_PreExistingDocumentDoesNotRequireBatchStop()
    {
        var family = new FamilyEditFamily
        {
            Name = "Family",
            Status = "changed"
        };
        family.ApplyClosure(null);
        var batch = new FamilyEditData { Families = [family] };

        await Assert.That(batch.HasRetainedDocument).IsFalse();
        await Assert.That(family.Status).IsEqualTo("changed");
        await Assert.That(family.Reason).IsNull();
    }

    [Test]
    public async Task FamilyReceipt_SerializesRefusedClosureAndRollback()
    {
        var closure = OwnedDocumentCleanup.Close(new object(), [], EqualityComparer<object>.Default, "Family [42]", _ => false);
        var data = new FamilyEditData
        {
            Mode = "project",
            RolledBack = true,
            FailedFamily = "Family",
            Families = [new FamilyEditFamily
            {
                Name = "Family",
                Status = "failed",
                Closure = closure,
                RolledBack = true
            }]
        };
        var response = CommandResponse<FamilyEditData>.Fail("edit-families", closure!.ToString(), 0);
        response.Data = data;
        using var json = JsonDocument.Parse(CommandResponseJsonSerializer.Serialize(response));
        var receipt = json.RootElement;
        var family = receipt.GetProperty("data").GetProperty("families")[0];

        await Assert.That(receipt.GetProperty("success").GetBoolean()).IsFalse();
        await Assert.That(receipt.GetProperty("data").GetProperty("committed").GetBoolean()).IsFalse();
        await Assert.That(family.GetProperty("rolledBack").GetBoolean()).IsTrue();
        await Assert.That(family.GetProperty("closure").GetProperty("closed").GetBoolean()).IsFalse();
        await Assert.That(family.GetProperty("closure").GetProperty("identity").GetString()).IsEqualTo("Family [42]");
    }
}
