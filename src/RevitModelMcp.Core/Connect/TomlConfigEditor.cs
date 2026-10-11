using System.Text;

namespace RevitModelMcp.Core.Connect;

/// <summary>
///     Adds, removes and detects the <c>[mcp_servers.revit-model-mcp]</c> tables of a Codex <c>config.toml</c> by
///     editing lines in place. Comments, other tables and unknown keys are not touched.
/// </summary>
public static class TomlConfigEditor
{
    private const string TablesKey = "mcp_servers";

    /// <summary>Reads whether the entry exists.</summary>
    public static McpClientStatus Inspect(McpClientTarget target, string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return new McpClientStatus(target, McpClientState.NotConfigured);
        var scan = Scan(text);
        if (scan.Error is not null) return new McpClientStatus(target, McpClientState.Unreadable, scan.Error);
        return new McpClientStatus(target, scan.Sections.Count == 0 ? McpClientState.NotConfigured : McpClientState.Configured);
    }

    /// <summary>Returns the text with the entry appended, or null when it is already there.</summary>
    /// <exception cref="McpConfigException">The text is not a usable configuration.</exception>
    public static string? Add(string text)
    {
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
        builder.Append("command = ").Append(Quote(McpServerSpec.Command)).Append(newline);
        builder.Append("args = [").Append(string.Join(", ", McpServerSpec.Args.Select(Quote))).Append(']').Append(newline);
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

    private readonly record struct Section(int HeaderLine, int EndLine);

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
            scan.Sections.Add(new Section(headers[index].Line, endLine));
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
