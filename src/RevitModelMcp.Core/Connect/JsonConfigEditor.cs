using System.Globalization;
using System.Text;

namespace RevitModelMcp.Core.Connect;

/// <summary>
///     Adds, removes and detects the server entry in a JSON client configuration by editing the text in place.
///     Comments, trailing commas, key order, unknown keys and indentation survive; only the entry's own lines change.
/// </summary>
public static class JsonConfigEditor
{
    /// <summary>Reads whether the entry exists.</summary>
    public static McpClientStatus Inspect(McpClientTarget target, string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return new McpClientStatus(target, McpClientState.NotConfigured);
        if (!TryParse(text, out var root, out var error)) return Unreadable(target, error);
        if (root.Kind != NodeKind.Object) return Unreadable(target, "The top level is not a JSON object.");
        var servers = Find(root, target.RootKey);
        if (servers is null) return new McpClientStatus(target, McpClientState.NotConfigured);
        if (servers.Value.Kind != NodeKind.Object) return Unreadable(target, $"\"{target.RootKey}\" is not an object.");
        return new McpClientStatus(target,
            Find(servers.Value, McpServerSpec.Name) is null ? McpClientState.NotConfigured : McpClientState.Configured);
    }

    /// <summary>Returns the text with the entry added, or null when it is already there.</summary>
    /// <exception cref="McpConfigException">The text is not a usable configuration.</exception>
    public static string? Add(McpClientTarget target, string text)
    {
        var newline = text.Contains("\r\n") ? "\r\n" : "\n";
        if (string.IsNullOrWhiteSpace(text))
            return "{" + newline + "  " + RootMember(target, "  ", "  ", newline) + newline + "}" + newline;
        var root = ParseObject(text);
        var unit = IndentUnit(text, root);
        var servers = Find(root, target.RootKey);
        if (servers is null)
            return InsertMember(text, root, indent => RootMember(target, unit, indent, newline), unit, newline, 0);
        if (servers.Value.Kind != NodeKind.Object) throw new McpConfigException($"\"{target.RootKey}\" is not an object.");
        if (Find(servers.Value, McpServerSpec.Name) is not null) return null;
        return InsertMember(text, servers.Value, indent => Entry(target, unit, indent, newline), unit, newline, 1);
    }

    /// <summary>Returns the text with the entry removed, or null when it is not there.</summary>
    /// <exception cref="McpConfigException">The text is not a usable configuration.</exception>
    public static string? Remove(McpClientTarget target, string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var root = ParseObject(text);
        var servers = Find(root, target.RootKey);
        if (servers is null) return null;
        if (servers.Value.Kind != NodeKind.Object) throw new McpConfigException($"\"{target.RootKey}\" is not an object.");
        var members = servers.Value.Members!;
        var index = members.FindIndex(member => member.Key == McpServerSpec.Name);
        if (index < 0) return null;
        var entry = members[index];
        var start = entry.Start;
        var lineStart = LineStart(text, start);
        if (IsBlank(text, lineStart, start)) start = lineStart;
        var end = entry.End;
        var cursor = SkipTrivia(text, end, stopAtNewline: true);
        var hasComma = cursor < text.Length && text[cursor] == ',';
        if (hasComma) cursor++;
        var edits = new List<(int Start, int End)>();
        var tail = SkipSpaces(text, cursor);
        if (tail >= text.Length || text[tail] == '\r' || text[tail] == '\n')
        {
            end = tail;
            if (end < text.Length && text[end] == '\r') end++;
            if (end < text.Length && text[end] == '\n') end++;
        }
        else
        {
            end = cursor;
            if (start == lineStart) start = entry.Start;
        }
        edits.Add((start, end));
        if (!hasComma && index > 0)
        {
            var previous = members[index - 1];
            var comma = SkipTrivia(text, previous.End, stopAtNewline: false);
            if (comma < text.Length && text[comma] == ',') edits.Add((comma, comma + 1));
        }
        var result = text;
        foreach (var (editStart, editEnd) in edits.OrderByDescending(edit => edit.Start))
            result = result.Remove(editStart, editEnd - editStart);
        return result;
    }

    private static McpClientStatus Unreadable(McpClientTarget target, string detail) =>
        new(target, McpClientState.Unreadable, detail);

    private static Node ParseObject(string text)
    {
        if (!TryParse(text, out var root, out var error)) throw new McpConfigException(error);
        if (root.Kind != NodeKind.Object) throw new McpConfigException("The top level is not a JSON object.");
        return root;
    }

    private static Node? Find(Node objectNode, string key)
    {
        foreach (var member in objectNode.Members!)
            if (member.Key == key) return member.Value;
        return null;
    }

    private static string InsertMember(string text, Node target, Func<string, string> render, string unit, string newline, int depth)
    {
        var indent = Repeat(unit, depth + 1);
        var closeIndent = Repeat(unit, depth);
        var members = target.Members!;
        if (members.Count == 0)
        {
            var member = render(indent);
            var inner = text.Substring(target.Start + 1, target.End - target.Start - 2);
            if (inner.Trim().Length == 0)
                return text.Substring(0, target.Start) + "{" + newline + indent + member + newline + closeIndent + "}" +
                       text.Substring(target.End);
            var close = target.End - 1;
            return text.Insert(close, indent + member + newline + closeIndent);
        }
        var last = members[members.Count - 1];
        var afterLast = SkipTrivia(text, last.End, stopAtNewline: false);
        var trailingComma = afterLast < text.Length && text[afterLast] == ',';
        var position = trailingComma ? afterLast + 1 : last.End;
        var scan = SkipSpaces(text, position);
        var lineEnd = scan;
        if (scan + 1 < text.Length && text[scan] == '/' && text[scan + 1] == '/')
            while (lineEnd < text.Length && text[lineEnd] != '\n' && text[lineEnd] != '\r') lineEnd++;
        var onlyComment = scan >= text.Length || text[scan] == '\r' || text[scan] == '\n' || lineEnd > scan;
        var insertAt = onlyComment ? lineEnd : position;
        var lastIndent = LeadingIndent(text, last.Start);
        if (lastIndent is not null) indent = lastIndent;
        var insertion = newline + indent + render(indent) + (trailingComma ? "," : string.Empty);
        var result = text.Insert(insertAt, insertion);
        if (!trailingComma) result = result.Insert(last.End, ",");
        return result;
    }

    private static string? LeadingIndent(string text, int position)
    {
        var lineStart = LineStart(text, position);
        return IsBlank(text, lineStart, position) ? text.Substring(lineStart, position - lineStart) : null;
    }

    private static string IndentUnit(string text, Node root)
    {
        if (root.Members!.Count == 0) return "  ";
        var indent = LeadingIndent(text, root.Members[0].Start);
        return string.IsNullOrEmpty(indent) ? "  " : indent!;
    }

    private static int LineStart(string text, int position)
    {
        while (position > 0 && text[position - 1] != '\n') position--;
        return position;
    }

    private static bool IsBlank(string text, int start, int end)
    {
        for (var index = start; index < end; index++)
            if (text[index] != ' ' && text[index] != '\t') return false;
        return true;
    }

    private static int SkipSpaces(string text, int position)
    {
        while (position < text.Length && (text[position] == ' ' || text[position] == '\t')) position++;
        return position;
    }

    private static string Repeat(string unit, int count) =>
        count <= 0 ? string.Empty : new StringBuilder().Insert(0, unit, count).ToString();

    private static string RootMember(McpClientTarget target, string unit, string indent, string newline) =>
        Quote(target.RootKey) + ": {" + newline + indent + unit + Entry(target, unit, indent + unit, newline) + newline +
        indent + "}";

    /// <summary>Renders the entry for a line that starts at <paramref name="indent" />.</summary>
    private static string Entry(McpClientTarget target, string unit, string indent, string newline)
    {
        var inner = indent + unit;
        var envInner = inner + unit;
        var properties = new List<string>();
        if (target.IncludeType) properties.Add(Member("type", Quote("stdio")));
        properties.Add(Member("command", Quote(McpServerSpec.Command)));
        properties.Add(Member("args", "[" + string.Join(", ", McpServerSpec.Args.Select(Quote)) + "]"));
        var env = "{" + newline +
                  string.Join("," + newline, McpServerSpec.Env.Select(pair => envInner + Member(pair.Key, Quote(pair.Value)))) +
                  newline + inner + "}";
        properties.Add(Member("env", env));
        return Quote(McpServerSpec.Name) + ": {" + newline +
               string.Join("," + newline, properties.Select(property => inner + property)) + newline + indent + "}";
    }

    private static string Member(string key, string value) => Quote(key) + ": " + value;

    private static string Quote(string value)
    {
        var builder = new StringBuilder("\"");
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
                    if (character < ' ') builder.Append("\\u").Append(((int)character).ToString("x4", CultureInfo.InvariantCulture));
                    else builder.Append(character);
                    break;
            }
        }
        return builder.Append('"').ToString();
    }

    // ---- Scanner ----

    private enum NodeKind { Object, Other }

    private readonly record struct MemberSpan(string Key, int Start, int End, Node Value);

    private readonly record struct Node(NodeKind Kind, int Start, int End, List<MemberSpan>? Members);

    private static bool TryParse(string text, out Node root, out string error)
    {
        root = default;
        try
        {
            var position = SkipTrivia(text, 0, stopAtNewline: false, strict: true);
            root = ParseValue(text, ref position, 0);
            position = SkipTrivia(text, position, stopAtNewline: false, strict: true);
            if (position != text.Length) throw new FormatException("Unexpected text after the top-level value.");
            error = string.Empty;
            return true;
        }
        catch (FormatException exception)
        {
            error = exception.Message;
            return false;
        }
    }

    private static Node ParseValue(string text, ref int position, int depth)
    {
        if (depth > 64) throw new FormatException("The JSON is nested too deeply.");
        if (position >= text.Length) throw new FormatException("The JSON ends unexpectedly.");
        var start = position;
        switch (text[position])
        {
            case '{':
                return ParseObjectNode(text, ref position, depth);
            case '[':
                position++;
                position = SkipTrivia(text, position, false, true);
                while (position < text.Length && text[position] != ']')
                {
                    ParseValue(text, ref position, depth + 1);
                    position = SkipTrivia(text, position, false, true);
                    if (position < text.Length && text[position] == ',')
                        position = SkipTrivia(text, position + 1, false, true);
                    else break;
                }
                Expect(text, ref position, ']');
                return new Node(NodeKind.Other, start, position, null);
            case '"':
                ParseString(text, ref position);
                return new Node(NodeKind.Other, start, position, null);
            default:
                while (position < text.Length && IsLiteralCharacter(text[position])) position++;
                if (position == start) throw new FormatException($"Unexpected character at offset {start}.");
                ValidateLiteral(text.Substring(start, position - start));
                return new Node(NodeKind.Other, start, position, null);
        }
    }

    private static Node ParseObjectNode(string text, ref int position, int depth)
    {
        var start = position;
        position++;
        var members = new List<MemberSpan>();
        position = SkipTrivia(text, position, false, true);
        while (position < text.Length && text[position] != '}')
        {
            if (text[position] != '"') throw new FormatException($"Expected a property name at offset {position}.");
            var memberStart = position;
            var key = ParseString(text, ref position);
            position = SkipTrivia(text, position, false, true);
            Expect(text, ref position, ':');
            position = SkipTrivia(text, position, false, true);
            var value = ParseValue(text, ref position, depth + 1);
            members.Add(new MemberSpan(key, memberStart, position, value));
            position = SkipTrivia(text, position, false, true);
            if (position < text.Length && text[position] == ',')
                position = SkipTrivia(text, position + 1, false, true);
            else break;
        }
        Expect(text, ref position, '}');
        return new Node(NodeKind.Object, start, position, members);
    }

    private static void Expect(string text, ref int position, char expected)
    {
        if (position >= text.Length || text[position] != expected)
            throw new FormatException($"Expected '{expected}' at offset {position}.");
        position++;
    }

    private static string ParseString(string text, ref int position)
    {
        position++;
        var builder = new StringBuilder();
        while (position < text.Length)
        {
            var character = text[position++];
            if (character == '"') return builder.ToString();
            if (character == '\n' || character == '\r') break;
            if (character != '\\') { builder.Append(character); continue; }
            if (position >= text.Length) break;
            var escape = text[position++];
            switch (escape)
            {
                case '"': builder.Append('"'); break;
                case '\\': builder.Append('\\'); break;
                case '/': builder.Append('/'); break;
                case 'b': builder.Append('\b'); break;
                case 'f': builder.Append('\f'); break;
                case 'n': builder.Append('\n'); break;
                case 'r': builder.Append('\r'); break;
                case 't': builder.Append('\t'); break;
                case 'u':
                    if (position + 4 > text.Length ||
                        !int.TryParse(text.Substring(position, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var code))
                        throw new FormatException("A string has a bad unicode escape.");
                    builder.Append((char)code);
                    position += 4;
                    break;
                default:
                    throw new FormatException("A string has a bad escape.");
            }
        }
        throw new FormatException("A string is not closed.");
    }

    private static bool IsLiteralCharacter(char character) =>
        char.IsLetterOrDigit(character) || character is '-' or '+' or '.';

    private static void ValidateLiteral(string literal)
    {
        if (literal is "true" or "false" or "null") return;
        if (!double.TryParse(literal, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
            throw new FormatException($"'{literal}' is not a JSON value.");
    }

    /// <summary>Skips whitespace and comments; with <paramref name="stopAtNewline" /> only up to the end of the line.</summary>
    private static int SkipTrivia(string text, int position, bool stopAtNewline, bool strict = false)
    {
        while (position < text.Length)
        {
            var character = text[position];
            if (character == ' ' || character == '\t') { position++; continue; }
            if (character == '\r' || character == '\n')
            {
                if (stopAtNewline) return position;
                position++;
                continue;
            }
            if (character == '/' && position + 1 < text.Length && text[position + 1] == '/')
            {
                while (position < text.Length && text[position] != '\n') position++;
                continue;
            }
            if (character == '/' && position + 1 < text.Length && text[position + 1] == '*')
            {
                var close = text.IndexOf("*/", position + 2, StringComparison.Ordinal);
                if (close < 0)
                {
                    if (strict) throw new FormatException("A comment is not closed.");
                    return text.Length;
                }
                position = close + 2;
                continue;
            }
            break;
        }
        return position;
    }
}
