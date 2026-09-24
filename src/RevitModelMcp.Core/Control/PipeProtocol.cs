using System.Globalization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using RevitModelMcp.Core.Models;

namespace RevitModelMcp.Core.Control;

public sealed class PipeProtocolException(string message) : Exception(message);

public static class PipeProtocol
{
    public const string Version = "pipe/1";
    public const int MaxMessageBytes = 1024 * 1024;

    public static string PipeName(int processId) =>
        "RevitModelMcp." + processId.ToString(CultureInfo.InvariantCulture);

    public static PipeMessage Parse(string line)
    {
        if (Encoding.UTF8.GetByteCount(line) > MaxMessageBytes)
            throw new PipeProtocolException("The message exceeds 1 MiB.");
        XElement root;
        try
        {
            using var reader = JsonReaderWriterFactory.CreateJsonReader(
                Encoding.UTF8.GetBytes(line), XmlDictionaryReaderQuotas.Max);
            root = XElement.Load(reader);
        }
        catch (XmlException)
        {
            throw new PipeProtocolException("The message is not valid JSON.");
        }
        if (JsonType(root) != "object") throw new PipeProtocolException("The message must be a JSON object.");
        return new PipeMessage
        {
            Type = Text(root, "type") ?? throw new PipeProtocolException("Field type is required."),
            Id = Text(root, "id"),
            Protocol = Text(root, "protocol"),
            ClientId = Text(root, "clientId"),
            ClientName = Text(root, "clientName"),
            InstanceId = Text(root, "instanceId"),
            Pid = Number(root, "pid"),
            RevitVersion = Text(root, "revitVersion"),
            Documents = Documents(root),
            JobId = Text(root, "jobId"),
            State = Text(root, "state"),
            Position = Number(root, "position"),
            Cancelled = Boolean(root, "cancelled"),
            Error = Text(root, "error"),
            Message = Text(root, "message"),
            RetryAfterMs = Number(root, "retryAfterMs"),
            Job = RawObject(root, "job"),
            Result = RawObject(root, "result")
        };
    }

    /// <summary>Writes one message as a single JSON line without the trailing newline.</summary>
    public static string Serialize(PipeMessage message)
    {
        var builder = new StringBuilder(256);
        builder.Append('{');
        String(builder, "type", message.Type);
        String(builder, "id", message.Id);
        String(builder, "protocol", message.Protocol);
        String(builder, "clientId", message.ClientId);
        String(builder, "clientName", message.ClientName);
        String(builder, "instanceId", message.InstanceId);
        Literal(builder, "pid", message.Pid?.ToString(CultureInfo.InvariantCulture));
        String(builder, "revitVersion", message.RevitVersion);
        if (message.Documents is { } documents)
        {
            Name(builder, "documents");
            builder.Append('[');
            for (var index = 0; index < documents.Count; index++)
            {
                if (index > 0) builder.Append(',');
                builder.Append('{');
                String(builder, "title", documents[index].Title);
                String(builder, "path", documents[index].Path);
                Literal(builder, "isActive", documents[index].IsActive ? "true" : "false");
                Literal(builder, "isFamilyDocument", documents[index].IsFamilyDocument ? "true" : "false");
                builder.Append('}');
            }
            builder.Append(']');
        }
        String(builder, "jobId", message.JobId);
        String(builder, "state", message.State);
        Literal(builder, "position", message.Position?.ToString(CultureInfo.InvariantCulture));
        Literal(builder, "cancelled", message.Cancelled is null ? null : message.Cancelled.Value ? "true" : "false");
        String(builder, "error", message.Error);
        String(builder, "message", message.Message);
        Literal(builder, "retryAfterMs", message.RetryAfterMs?.ToString(CultureInfo.InvariantCulture));
        Literal(builder, "job", SingleLine(message.Job));
        Literal(builder, "result", SingleLine(message.Result));
        builder.Append('}');
        return builder.ToString();
    }

    private static string? JsonType(XElement element) => (string?)element.Attribute("type");

    private static XElement? Member(XElement root, string name)
    {
        var element = root.Element(name);
        return element is null || JsonType(element) == "null" ? null : element;
    }

    private static string? Text(XElement root, string name)
    {
        var element = Member(root, name);
        if (element is null) return null;
        if (JsonType(element) != "string") throw new PipeProtocolException($"Field {name} must be a string.");
        return element.Value;
    }

    private static int? Number(XElement root, string name)
    {
        var element = Member(root, name);
        if (element is null) return null;
        if (JsonType(element) != "number" ||
            !int.TryParse(element.Value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value))
            throw new PipeProtocolException($"Field {name} must be an integer.");
        return value;
    }

    private static bool? Boolean(XElement root, string name)
    {
        var element = Member(root, name);
        if (element is null) return null;
        if (JsonType(element) != "boolean") throw new PipeProtocolException($"Field {name} must be a boolean.");
        return element.Value == "true";
    }

    private static List<InstanceDocument>? Documents(XElement root)
    {
        var element = Member(root, "documents");
        if (element is null) return null;
        if (JsonType(element) != "array") throw new PipeProtocolException("Field documents must be an array.");
        return element.Elements().Select(item =>
        {
            if (JsonType(item) != "object") throw new PipeProtocolException("Each document must be an object.");
            return new InstanceDocument
            {
                Title = Text(item, "title") ?? string.Empty,
                Path = Text(item, "path") ?? string.Empty,
                IsActive = Boolean(item, "isActive") ?? false,
                IsFamilyDocument = Boolean(item, "isFamilyDocument") ?? false
            };
        }).ToList();
    }

    private static string? RawObject(XElement root, string name)
    {
        var element = Member(root, name);
        if (element is null) return null;
        if (JsonType(element) != "object") throw new PipeProtocolException($"Field {name} must be a JSON object.");
        var copy = new XElement("root", element.Attributes(), element.Nodes());
        // The reader keeps an empty text node for null, {} and []; the writer accepts text only on scalars.
        foreach (var item in copy.DescendantsAndSelf().Where(item => JsonType(item) != "string" && !item.HasElements))
            if (item.Value.Length == 0) item.RemoveNodes();
        // With that fixed, the reader's XML mapping is lossless and writing it back reproduces the JSON value.
        using var stream = new MemoryStream();
        using (var writer = JsonReaderWriterFactory.CreateJsonWriter(stream, Encoding.UTF8, false))
        {
            copy.WriteTo(writer);
            writer.Flush();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static string? SingleLine(string? json)
    {
        // Literal line breaks in valid JSON are insignificant whitespace; strings escape theirs.
        if (json is null || (json.IndexOf('\n') < 0 && json.IndexOf('\r') < 0)) return json;
        return json.Replace('\r', ' ').Replace('\n', ' ');
    }

    private static void Name(StringBuilder builder, string name)
    {
        if (builder[builder.Length - 1] != '{') builder.Append(',');
        builder.Append('"').Append(name).Append("\":");
    }

    private static void Literal(StringBuilder builder, string name, string? value)
    {
        if (value is null) return;
        Name(builder, name);
        builder.Append(value);
    }

    private static void String(StringBuilder builder, string name, string? value)
    {
        if (value is null) return;
        Name(builder, name);
        builder.Append('"');
        foreach (var character in value)
        {
            switch (character)
            {
                case '"': builder.Append("\\\""); break;
                case '\\': builder.Append("\\\\"); break;
                case '\n': builder.Append("\\n"); break;
                case '\r': builder.Append("\\r"); break;
                case '\t': builder.Append("\\t"); break;
                default:
                    if (character < 0x20 || character is '\u2028' or '\u2029')
                        builder.Append("\\u").Append(((int)character).ToString("x4", CultureInfo.InvariantCulture));
                    else builder.Append(character);
                    break;
            }
        }
        builder.Append('"');
    }
}

/// <summary>Reads newline-delimited messages and rejects any line longer than the protocol limit.</summary>
public sealed class PipeLineReader(Stream stream, int maxBytes = PipeProtocol.MaxMessageBytes)
{
    private readonly byte[] _buffer = new byte[8192];
    private readonly MemoryStream _line = new();
    private int _start;
    private int _end;

    public async Task<string?> ReadLineAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            var newline = Array.IndexOf(_buffer, (byte)'\n', _start, _end - _start);
            if (newline >= 0)
            {
                Append(_start, newline - _start);
                _start = newline + 1;
                var bytes = _line.ToArray();
                _line.SetLength(0);
                var count = bytes.Length > 0 && bytes[bytes.Length - 1] == '\r' ? bytes.Length - 1 : bytes.Length;
                return Encoding.UTF8.GetString(bytes, 0, count);
            }
            Append(_start, _end - _start);
            _start = _end = 0;
            var read = await stream.ReadAsync(_buffer, 0, _buffer.Length, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                if (_line.Length == 0) return null;
                throw new PipeProtocolException("The connection closed inside a message.");
            }
            _end = read;
        }
    }

    private void Append(int offset, int count)
    {
        if (_line.Length + count > maxBytes) throw new PipeProtocolException("The message exceeds 1 MiB.");
        _line.Write(_buffer, offset, count);
    }
}
