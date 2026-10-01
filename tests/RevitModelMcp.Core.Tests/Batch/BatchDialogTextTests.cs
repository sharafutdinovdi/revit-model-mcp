using System.Runtime.Serialization.Json;
using System.Text;
using RevitModelMcp.Core.Batch;

namespace RevitModelMcp.Core.Tests.Batch;

public sealed class BatchDialogTextTests
{
    [Test]
    public async Task Cap_HandlesBlankLengthAndSurrogateBoundary()
    {
        await Assert.That(BatchDialogText.Cap(null)).IsNull();
        await Assert.That(BatchDialogText.Cap(" \r\n ")).IsNull();
        await Assert.That(BatchDialogText.Cap(" short ")).IsEqualTo("short");
        await Assert.That(BatchDialogText.Cap(new string('a', 2000))!.Length).IsEqualTo(2000);
        await Assert.That(BatchDialogText.Cap(new string('a', 2001))!.Length).IsEqualTo(2000);
        await Assert.That(BatchDialogText.Cap(new string('a', 1999) + "😀")!.Length).IsEqualTo(1999);
        await Assert.That(BatchDialogText.Cap("A\r\nB\rC")).IsEqualTo("A\nB\nC");
    }

    [Test]
    public async Task JoinMessage_DropsBlankAndConsecutiveDuplicatePartsAndCaps()
    {
        await Assert.That(BatchDialogText.JoinMessage([" A ", null, "A", "B", "A"])).IsEqualTo("A\nB\nA");
        await Assert.That(BatchDialogText.JoinMessage([new string('x', 2100)])!.Length).IsEqualTo(2000);
    }

    [Test]
    public async Task Parse_ReadsTextAndPushButtonsInOrder()
    {
        var controls = new List<NativeDialogControl>
        {
            new("Static", "First", 0, false),
            new("Edit", "Second", 0, false),
            new("RICHEDIT50W", "Third", 0, false),
            new("Other", "Ignored", 0, false),
            new("Button", "&OK", 1, true),
            new("button", "Save && Close", 2, true),
            new("Button", "&OK", 1, true),
            new("Button", "Check", 3, false)
        };
        controls.AddRange(Enumerable.Range(4, 12).Select(index => new NativeDialogControl("Button", $"Choice {index}", index, true)));
        var (message, buttons) = BatchDialogText.Parse(controls);
        await Assert.That(message).IsEqualTo("First\nSecond\nThird");
        await Assert.That(buttons.Count).IsEqualTo(10);
        await Assert.That(buttons[0].Caption).IsEqualTo("OK");
        await Assert.That(buttons[0].Result).IsEqualTo(1);
        await Assert.That(buttons[1].Caption).IsEqualTo("Save & Close");
        await Assert.That(buttons[1].Result).IsEqualTo(2);
    }

    [Test]
    public async Task DescribeUnknown_IncludesAvailableMessageAndButtons()
    {
        var record = Record() with { Message = "Review model.", Buttons = [new BatchDialogButton { Caption = "OK", Result = 1 }] };
        await Assert.That(BatchDialogText.DescribeUnknown(record)).IsEqualTo("Unknown dialog 'ID': Review model. Buttons: 1=OK.");
        await Assert.That(BatchDialogText.DescribeUnknown(Record())).IsEqualTo("Unknown dialog 'ID': No message available.");
    }

    [Test]
    public async Task BatchDialogRecord_RoundTripsButtonsAndReadsOlderJson()
    {
        var serializer = new DataContractJsonSerializer(typeof(BatchDialogRecord));
        foreach (var record in new[] { Record(), Record() with { Buttons = [new BatchDialogButton { Caption = "Cancel", Result = 2 }] } })
        {
            using var output = new MemoryStream();
            serializer.WriteObject(output, record);
            output.Position = 0;
            var read = (BatchDialogRecord)serializer.ReadObject(output)!;
            await Assert.That(read.Buttons?.Count).IsEqualTo(record.Buttons?.Count);
            await Assert.That(read.Buttons?.FirstOrDefault()?.Result).IsEqualTo(record.Buttons?.FirstOrDefault()?.Result);
        }
        using var oldJson = new MemoryStream(Encoding.UTF8.GetBytes("""{"dialogId":"ID","type":"DialogBoxShowingEventArgs","decision":"unknown","modelPath":"A.rvt","phase":"open","timeUtc":"2026-09-30T00:00:00Z"}"""));
        var older = (BatchDialogRecord)serializer.ReadObject(oldJson)!;
        await Assert.That(older.Buttons).IsNull();
    }

    [Test]
    public async Task FromFields_ReadsButtonsFromList()
    {
        var fields = Fields();
        fields["message"] = "Review model.";
        fields["buttons"] = new List<object>
        {
            new Dictionary<string, object> { ["caption"] = "OK", ["result"] = 1 },
            new Dictionary<string, object> { ["caption"] = "Cancel", ["result"] = 2 }
        };

        var record = BatchDialogText.FromFields(fields);

        await Assert.That(record.DialogId).IsEqualTo("ID");
        await Assert.That(record.Type).IsEqualTo("DialogBoxShowingEventArgs");
        await Assert.That(record.Message).IsEqualTo("Review model.");
        await Assert.That(record.Buttons!.Count).IsEqualTo(2);
        await Assert.That(record.Buttons[0].Caption).IsEqualTo("OK");
        await Assert.That(record.Buttons[0].Result).IsEqualTo(1);
        await Assert.That(record.Buttons[1].Caption).IsEqualTo("Cancel");
        await Assert.That(record.Buttons[1].Result).IsEqualTo(2);
        await Assert.That(record.Decision).IsEqualTo("unknown");
        await Assert.That(record.Result).IsNull();
        await Assert.That(record.ModelPath).IsEqualTo("A.rvt");
        await Assert.That(record.Phase).IsEqualTo("open");
        await Assert.That(record.TimeUtc).IsEqualTo("2026-09-30T00:00:00Z");
    }

    [Test]
    public async Task FromFields_ReadsButtonsFromArray()
    {
        var fields = Fields();
        fields["buttons"] = new object[]
        {
            new Dictionary<string, object> { ["caption"] = "Yes", ["result"] = 6 }
        };

        var record = BatchDialogText.FromFields(fields);

        await Assert.That(record.Buttons!.Count).IsEqualTo(1);
        await Assert.That(record.Buttons[0].Caption).IsEqualTo("Yes");
        await Assert.That(record.Buttons[0].Result).IsEqualTo(6);
    }

    [Test]
    public async Task FromFields_LeavesMissingAndNullButtonsUnset()
    {
        var fields = Fields();
        await Assert.That(BatchDialogText.FromFields(fields).Buttons).IsNull();
        fields["buttons"] = null!;
        fields["message"] = null!;
        var record = BatchDialogText.FromFields(fields);
        await Assert.That(record.Buttons).IsNull();
        await Assert.That(record.Message).IsNull();
        fields["buttons"] = new List<object>();
        await Assert.That(BatchDialogText.FromFields(fields).Buttons).IsNull();
    }

    [Test]
    public async Task FromFields_SkipsMalformedButtons()
    {
        var fields = Fields();
        fields["buttons"] = new object[]
        {
            new Dictionary<string, object> { ["caption"] = "Missing result" },
            new Dictionary<string, object> { ["result"] = 3 },
            "not a button",
            new Dictionary<string, object> { ["caption"] = "Retry", ["result"] = 4 }
        };

        var record = BatchDialogText.FromFields(fields);

        await Assert.That(record.Buttons!.Count).IsEqualTo(1);
        await Assert.That(record.Buttons[0].Caption).IsEqualTo("Retry");
        await Assert.That(record.Buttons[0].Result).IsEqualTo(4);
    }

    [Test]
    public async Task FromFields_CapsButtonsAtMaximum()
    {
        var fields = Fields();
        fields["buttons"] = Enumerable.Range(0, BatchDialogText.MaximumButtons + 2)
            .Select(index => (object)new Dictionary<string, object> { ["caption"] = $"Choice {index}", ["result"] = index })
            .ToList();

        var record = BatchDialogText.FromFields(fields);

        await Assert.That(record.Buttons!.Count).IsEqualTo(BatchDialogText.MaximumButtons);
        await Assert.That(record.Buttons[^1].Caption).IsEqualTo("Choice 9");
    }

    private static Dictionary<string, object> Fields() => new()
    {
        ["dialogId"] = "ID",
        ["type"] = "DialogBoxShowingEventArgs",
        ["decision"] = "unknown",
        ["modelPath"] = "A.rvt",
        ["phase"] = "open",
        ["timeUtc"] = "2026-09-30T00:00:00Z"
    };

    private static BatchDialogRecord Record() => new()
    {
        DialogId = "ID",
        Type = "DialogBoxShowingEventArgs",
        Decision = "unknown",
        ModelPath = "A.rvt",
        Phase = "open",
        TimeUtc = "2026-09-30T00:00:00Z"
    };
}
