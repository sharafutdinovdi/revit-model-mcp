using System.Text;

namespace RevitModelMcp.Core.Connect;

/// <summary>
///     Adds, removes and detects the <c>[mcp_servers.revit-model-mcp]</c> tables of a Codex <c>config.toml</c> by
///     editing lines in place. Comments, other tables and unknown keys are not touched.
/// </summary>
public static class TomlConfigEditor
{
    private const string TablesKey = "mcp_servers";

    /// <summary>
    ///     Reads whether the entry exists. With a <paramref name="spec" />, an entry that starts another command than the
    ///     spec is reported as <see cref="McpClientState.ConfiguredDifferentCommand" />.
    /// </summary>
    public static McpClientStatus Inspect(McpClientTarget target, string text, McpServerSpec? spec = null)
    {
        if (string.IsNullOrWhiteSpace(text)) return new McpClientStatus(target, McpClientState.NotConfigured);
        var scan = Scan(text);
        if (scan.Error is not null) return new McpClientStatus(target, McpClientState.Unreadable, scan.Error);
        if (scan.Sections.Count == 0) return new McpClientStatus(target, McpClientState.NotConfigured);
        return new McpClientStatus(target,
            spec is null || StartsSpec(scan, spec) ? McpClientState.Configured : McpClientState.ConfiguredDifferentCommand);
    }

    // Looks only at the entry's own table; its [..env] sub-table is not part of the launch.
    private static bool StartsSpec(ScanResult scan, McpServerSpec spec)
    {
        foreach (var section in scan.Sections.Where(section => section.PathLength == 2))
        {
            string? command = null;
            List<string>? args = null;
            var end = Math.Min(section.EndLine, scan.Lines.Count);
            for (var index = section.HeaderLine + 1; index < end; index++)
            {
                var line = scan.Lines[index].Text.TrimStart();
                if (TryKeyValue(line, "command", out var rest)) command = ReadString(rest);
                else if (TryKeyValue(line, "args", out rest))
                {
                    var value = new StringBuilder(rest);
                    var state = new ValueState();
                    ScanValue(rest, 0, state);
                    while ((state.Depth > 0 || state.Multiline is not null) && ++index < end)
                    {
                        value.Append('\n').Append(scan.Lines[index].Text);
                        ScanValue(scan.Lines[index].Text, 0, state);
                    }
                    args = ReadStrings(value.ToString());
                }
            }
            return command is not null && args is not null && McpServerSpec.SameLaunch(command, args, spec);
        }
        return false;
    }

    private static bool TryKeyValue(string line, string key, out string rest)
    {
        rest = string.Empty;
        if (!line.StartsWith(key, StringComparison.Ordinal)) return false;
        var position = key.Length;
        while (position < line.Length && (line[position] == ' ' || line[position] == '\t')) position++;
        if (position >= line.Length || line[position] != '=') return false;
        rest = line.Substring(position + 1).TrimStart();
        return true;
    }

    private static string? ReadString(string text)
    {
        var position = 0;
        return TryReadString(text, ref position, out var value) ? value : null;
    }

    private static List<string>? ReadStrings(string text)
    {
        var items = new List<string>();
        var position = 0;
        SkipSpaceAndComments(text, ref position);
        if (position >= text.Length || text[position] != '[') return null;
        position++;
        while (true)
        {
            SkipSpaceAndComments(text, ref position);
            if (position >= text.Length) return null;
            if (text[position] == ']') return items;
            if (!TryReadString(text, ref position, out var value)) return null;
            items.Add(value);
            SkipSpaceAndComments(text, ref position);
            if (position < text.Length && text[position] == ',') position++;
        }
    }

    private static void SkipSpaceAndComments(string text, ref int position)
    {
        while (position < text.Length)
        {
            if (char.IsWhiteSpace(text[position])) position++;
            else if (text[position] == '#') while (position < text.Length && text[position] != '\n') position++;
            else return;
        }
    }

    // Single-line basic ("...") and literal ('...') strings; anything else makes the entry count as different.
    private static bool TryReadString(string text, ref int position, out string value)
    {
        value = string.Empty;
        if (position >= text.Length) return false;
        var quote = text[position];
        if (quote == '\'')
        {
            var close = text.IndexOf('\'', position + 1);
            if (close < 0 || text.IndexOf('\n', position, close - position) >= 0) return false;
            value = text.Substring(position + 1, close - position - 1);
            position = close + 1;
            return true;
        }
        if (quote != '"') return false;
        var builder = new StringBuilder();
        var cursor = position + 1;
        while (cursor < text.Length)
        {
            var character = text[cursor++];
            if (character == '"')
            {
                value = builder.ToString();
                position = cursor;
                return true;
            }
            if (character == '\n') return false;
            if (character != '\\') { builder.Append(character); continue; }
            if (cursor >= text.Length) return false;
            var escape = text[cursor++];
            switch (escape)
            {
                case '"': builder.Append('"'); break;
                case '\\': builder.Append('\\'); break;
                case 'n': builder.Append('\n'); break;
                case 't': builder.Append('\t'); break;
                case 'r': builder.Append('\r'); break;
                case 'b': builder.Append('\b'); break;
                case 'f': builder.Append('\f'); break;
                case 'u' or 'U':
                    var length = escape == 'u' ? 4 : 8;
                    if (cursor + length > text.Length ||
                        !int.TryParse(text.Substring(cursor, length), System.Globalization.NumberStyles.HexNumber,
                            System.Globalization.CultureInfo.InvariantCulture, out var code) || code > 0x10FFFF)
                        return false;
                    builder.Append(char.ConvertFromUtf32(code));
                    cursor += length;
                    break;
                default: return false;
            }
        }
        return false;
    }

    /// <summary>Returns the text with the entry appended, or null when it is already there.</summary>
    /// <exception cref="McpConfigException">The text is not a usable configuration.</exception>
    public static string? Add(string text, McpServerSpec? spec = null)
    {
        spec ??= McpServerSpec.Uvx;
        var newline = text.Contains("\r\n") ? "\r\n" : "\n";
        var scan = Require(text);
        if (scan.Sections.Count > 0) return null;
        var builder = new StringBuilder(text);
        if (builder.Length > 0)
        {
            if (!text.EndsWith("\n", StringComparison.Ordinal)) builder.Append(newline);
            var lastLine = scan.Lines.Count == 0 ? string.Empty : scan.Lines[scan.Lines.Count - 1].Text;
            if (lastLine.Trim().Length > 0 || !text.EndsWith("\n", StringComparison.Ordinal)) builder.Append(newline);
        }
        var table = TablesKey + "." + McpServerSpec.Name;
        builder.Append('[').Append(table).Append(']').Append(newline);
        builder.Append("command = ").Append(Quote(spec.Command)).Append(newline);
        builder.Append("args = [").Append(string.Join(", ", spec.Args.Select(Quote))).Append(']').Append(newline);
        builder.Append(newline);
        builder.Append('[').Append(table).Append(".env]").Append(newline);
        foreach (var pair in McpServerSpec.Env)
            builder.Append(pair.Key).Append(" = ").Append(Quote(pair.Value)).Append(newline);
        return builder.ToString();
    }

    /// <summary>Returns the text with the entry removed, or null when it is not there.</summary>
    /// <exception cref="McpConfigException">The text is not a usable configuration.</exception>
    public static string? Remove(string text)
    {
        var scan = Require(text);
        if (scan.Sections.Count == 0) return null;
        var lines = scan.Lines;
        var keep = new bool[lines.Count];
        for (var index = 0; index < keep.Length; index++) keep[index] = true;
        var reachesEnd = false;
        foreach (var section in scan.Sections)
        {
            var end = section.EndLine;
            if (end < lines.Count)
                while (end > section.HeaderLine + 1 && IsComment(lines[end - 1].Text)) end--;
            for (var index = section.HeaderLine; index < end; index++) keep[index] = false;
            reachesEnd |= end >= lines.Count;
        }
        if (reachesEnd)
        {
            // The block was appended at the end behind a blank separator line; take the separator with it.
            var last = lines.Count - 1;
            while (last >= 0 && !keep[last]) last--;
            while (last > 0 && lines[last].Text.Trim().Length == 0) keep[last--] = false;
        }
        var builder = new StringBuilder();
        for (var index = 0; index < lines.Count; index++)
            if (keep[index]) builder.Append(lines[index].Text).Append(lines[index].Eol);
        return builder.ToString();
    }

    private static ScanResult Require(string text)
    {
        var scan = Scan(text);
        if (scan.Error is not null) throw new McpConfigException(scan.Error);
        return scan;
    }

    private static bool IsComment(string line) => line.TrimStart().StartsWith("#", StringComparison.Ordinal);

    private static string Quote(string value) =>
        "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    private readonly record struct Line(string Text, string Eol);

    private readonly record struct Section(int HeaderLine, int EndLine, int PathLength);

    private sealed class ScanResult
    {
        public List<Line> Lines { get; } = [];
        public List<Section> Sections { get; } = [];
        public string? Error { get; set; }
    }

    private static ScanResult Scan(string text)
    {
        var scan = new ScanResult();
        var start = 0;
        while (start < text.Length)
        {
            var end = start;
            while (end < text.Length && text[end] != '\n' && text[end] != '\r') end++;
            var eolEnd = end;
            if (eolEnd < text.Length && text[eolEnd] == '\r') eolEnd++;
            if (eolEnd < text.Length && text[eolEnd] == '\n') eolEnd++;
            scan.Lines.Add(new Line(text.Substring(start, end - start), text.Substring(end, eolEnd - end)));
            start = eolEnd;
        }

        var state = new ValueState();
        List<string>? currentTable = null;
        var headers = new List<(int Line, List<string> Path)>();
        for (var index = 0; index < scan.Lines.Count; index++)
        {
            var line = scan.Lines[index].Text;
            if (state.Multiline is not null || state.Depth > 0)
            {
                ScanValue(line, 0, state);
                continue;
            }
            var trimmed = line.TrimStart();
            if (trimmed.Length == 0 || trimmed[0] == '#') continue;
            if (trimmed[0] == '[')
            {
                var array = trimmed.StartsWith("[[", StringComparison.Ordinal);
                var position = array ? 2 : 1;
                if (!TryParseKeyPath(trimmed, ref position, out var path, ']') ||
                    !ClosesHeader(trimmed, position, array))
                {
                    scan.Error = $"Line {index + 1} is not a valid table header.";
                    return scan;
                }
                currentTable = array ? [] : path;
                headers.Add((index, array ? [.. path, "[]"] : path));
                continue;
            }
            var keyPosition = 0;
            if (!TryParseKeyPath(trimmed, ref keyPosition, out var key, '='))
            {
                scan.Error = $"Line {index + 1} is not a valid key.";
                return scan;
            }
            if (currentTable is null && key[0] == TablesKey)
            {
                scan.Error = "The mcp_servers table uses an inline or dotted layout the add-in does not edit.";
                return scan;
            }
            if (currentTable is { Count: 1 } && currentTable[0] == TablesKey && key[0] == McpServerSpec.Name)
            {
                scan.Error = "The revit-model-mcp entry uses an inline or dotted layout the add-in does not edit.";
                return scan;
            }
            ScanValue(trimmed, keyPosition, state);
        }
        if (state.Multiline is not null || state.Depth > 0)
        {
            scan.Error = "A multi-line value is not closed.";
            return scan;
        }
        for (var index = 0; index < headers.Count; index++)
        {
            var path = headers[index].Path;
            if (path.Count < 2 || path[0] != TablesKey || path[1] != McpServerSpec.Name || path.Contains("[]")) continue;
            var endLine = index + 1 < headers.Count ? headers[index + 1].Line : scan.Lines.Count;
            scan.Sections.Add(new Section(headers[index].Line, endLine, path.Count));
        }
        return scan;
    }

    private sealed class ValueState
    {
        public string? Multiline { get; set; }
        public int Depth { get; set; }
    }

    /// <summary>Follows strings and brackets so values that span lines are not mistaken for headers.</summary>
    private static void ScanValue(string text, int position, ValueState state)
    {
        while (position < text.Length)
        {
            if (state.Multiline is not null)
            {
                var close = text.IndexOf(state.Multiline, position, StringComparison.Ordinal);
                if (close < 0) return;
                position = close + 3;
                state.Multiline = null;
                continue;
            }
            var character = text[position];
            if (character == '#') return;
            if (string.CompareOrdinal(text, position, "\"\"\"", 0, 3) == 0 || string.CompareOrdinal(text, position, "'''", 0, 3) == 0)
            {
                state.Multiline = text.Substring(position, 3);
                position += 3;
                continue;
            }
            if (character == '"')
            {
                position++;
                while (position < text.Length && text[position] != '"') position += text[position] == '\\' ? 2 : 1;
            }
            else if (character == '\'')
            {
                position = text.IndexOf('\'', position + 1);
                if (position < 0) return;
            }
            else if (character is '[' or '{') state.Depth++;
            else if (character is ']' or '}') state.Depth--;
            position++;
        }
    }

    private static bool ClosesHeader(string text, int position, bool array)
    {
        if (array)
        {
            if (position >= text.Length || text[position] != ']') return false;
            position++;
        }
        var rest = text.Substring(position).Trim();
        return rest.Length == 0 || rest[0] == '#';
    }

    /// <summary>
    ///     Parses a dotted key; stops after it and leaves <paramref name="position" /> just past the
    ///     <paramref name="terminator" /> (for headers the closing bracket is consumed).
    /// </summary>
    private static bool TryParseKeyPath(string text, ref int position, out List<string> path, char terminator)
    {
        path = [];
        while (true)
        {
            while (position < text.Length && (text[position] == ' ' || text[position] == '\t')) position++;
            if (position >= text.Length) return false;
            string part;
            if (text[position] is '"' or '\'')
            {
                var quote = text[position];
                var close = text.IndexOf(quote, position + 1);
                if (close < 0) return false;
                part = text.Substring(position + 1, close - position - 1);
                position = close + 1;
            }
            else
            {
                var begin = position;
                while (position < text.Length && (char.IsLetterOrDigit(text[position]) || text[position] is '_' or '-')) position++;
                if (position == begin) return false;
                part = text.Substring(begin, position - begin);
            }
            path.Add(part);
            while (position < text.Length && (text[position] == ' ' || text[position] == '\t')) position++;
            if (position >= text.Length) return false;
            if (text[position] == '.') { position++; continue; }
            if (text[position] != terminator) return false;
            position++;
            return true;
        }
    }
}
