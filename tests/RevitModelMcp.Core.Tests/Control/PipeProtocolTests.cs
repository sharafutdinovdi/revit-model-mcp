using System.Text;
using System.Text.Json;
using RevitModelMcp.Core.Control;
using RevitModelMcp.Core.Models;

namespace RevitModelMcp.Core.Tests.Control;

public sealed class PipeProtocolTests
{
    [Test]
    public async Task SubmitMessageRoundTripsItsJobObject()
    {
        const string job = "{\"command\":\"query-elements\",\"jobId\":\"j1\",\"clientId\":\"c1\",\"limit\":25," +
                           "\"categories\":[\"Walls\",\"Двери\"],\"parameterFilters\":[{\"name\":\"Mark\",\"value\":\"A \\\"1\\\"\"}]," +
                           "\"weird key\":{\"nested\":null,\"flag\":true,\"ratio\":1.5e3}}";
        var line = "{\"type\":\"submit\",\"id\":\"r1\",\"job\":" + job + "}";

        var parsed = PipeProtocol.Parse(line);
        var reparsed = PipeProtocol.Parse(PipeProtocol.Serialize(parsed));

        await Assert.That(reparsed.Type).IsEqualTo("submit");
        await Assert.That(reparsed.Id).IsEqualTo("r1");
        using var expected = JsonDocument.Parse(job);
        using var actual = JsonDocument.Parse(reparsed.Job!);
        await Assert.That(SameJson(expected.RootElement, actual.RootElement)).IsTrue();
    }

    private static bool SameJson(JsonElement left, JsonElement right) =>
        left.ValueKind == right.ValueKind && left.ValueKind switch
        {
            JsonValueKind.Object => left.EnumerateObject().Count() == right.EnumerateObject().Count() &&
                                    left.EnumerateObject().All(property =>
                                        right.TryGetProperty(property.Name, out var value) &&
                                        SameJson(property.Value, value)),
            JsonValueKind.Array => left.GetArrayLength() == right.GetArrayLength() &&
                                   left.EnumerateArray().Zip(right.EnumerateArray())
                                       .All(pair => SameJson(pair.First, pair.Second)),
            JsonValueKind.String => left.GetString() == right.GetString(),
            JsonValueKind.Number => left.GetDouble() == right.GetDouble(),
            _ => true
        };

    [Test]
    public async Task HelloReplyAndJobPushRoundTrip()
    {
        var hello = new PipeMessage
        {
            Type = "hello",
            Id = "r0",
            Protocol = PipeProtocol.Version,
            InstanceId = "0f8fad5bd9cb469fa16570867728950e",
            Pid = 4242,
            RevitVersion = "2026",
            Documents =
            [
                new InstanceDocument { Title = "Tower \"A\"", Path = @"C:\Models\Tower.rvt", IsActive = true },
                new InstanceDocument { Title = "Door", Path = "", IsFamilyDocument = true }
            ]
        };
        var push = new PipeMessage
        {
            Type = "job",
            JobId = "j1",
            State = "done",
            Position = 0,
            Cancelled = false,
            Result = "{\n  \"command\": \"ping\",\n  \"success\": true\n}"
        };

        var helloLine = PipeProtocol.Serialize(hello);
        var pushLine = PipeProtocol.Serialize(push);
        var helloBack = PipeProtocol.Parse(helloLine);
        var pushBack = PipeProtocol.Parse(pushLine);

        await Assert.That(helloLine.Contains('\n')).IsFalse();
        await Assert.That(pushLine.Contains('\n')).IsFalse();
        await Assert.That(helloBack.Pid).IsEqualTo(4242);
        await Assert.That(helloBack.InstanceId).IsEqualTo(hello.InstanceId);
        await Assert.That(helloBack.Documents!.Count).IsEqualTo(2);
        await Assert.That(helloBack.Documents[0].Title).IsEqualTo("Tower \"A\"");
        await Assert.That(helloBack.Documents[0].Path).IsEqualTo(@"C:\Models\Tower.rvt");
        await Assert.That(helloBack.Documents[0].IsActive).IsTrue();
        await Assert.That(helloBack.Documents[1].IsFamilyDocument).IsTrue();
        await Assert.That(pushBack.State).IsEqualTo("done");
        await Assert.That(pushBack.Cancelled!.Value).IsFalse();
        using var result = JsonDocument.Parse(pushBack.Result!);
        await Assert.That(result.RootElement.GetProperty("success").GetBoolean()).IsTrue();
    }

    [Test]
    [Arguments("[1,2]")]
    [Arguments("{\"id\":\"r1\"}")]
    [Arguments("{\"type\":\"submit\",\"job\":\"{}\"}")]
    [Arguments("{\"type\":7}")]
    [Arguments("{\"type\":")]
    public async Task InvalidMessagesAreRejected(string line)
    {
        await Assert.That(() => PipeProtocol.Parse(line)).Throws<PipeProtocolException>();
    }

    [Test]
    public async Task OversizeMessageIsRejectedByParserAndReader()
    {
        var padding = new string('x', PipeProtocol.MaxMessageBytes);
        var line = "{\"type\":\"submit\",\"job\":{\"command\":\"ping\",\"pad\":\"" + padding + "\"}}";
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(line + "\n"));

        await Assert.That(() => PipeProtocol.Parse(line)).Throws<PipeProtocolException>();
        await Assert.That(async () => await new PipeLineReader(stream).ReadLineAsync(CancellationToken.None))
            .Throws<PipeProtocolException>();
    }

    [Test]
    public async Task ReaderSplitsLinesAtTheLimitAndReportsEndOfStream()
    {
        var atLimit = new string('a', PipeProtocol.MaxMessageBytes);
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("{\"type\":\"hello\"}\r\n" + atLimit + "\n"));
        var reader = new PipeLineReader(stream);

        await Assert.That(await reader.ReadLineAsync(CancellationToken.None)).IsEqualTo("{\"type\":\"hello\"}");
        await Assert.That((await reader.ReadLineAsync(CancellationToken.None))!.Length).IsEqualTo(atLimit.Length);
        await Assert.That(await reader.ReadLineAsync(CancellationToken.None)).IsNull();
    }
}
