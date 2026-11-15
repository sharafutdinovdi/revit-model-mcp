using System.Globalization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace RevitModelMcp.Core.Control;

internal sealed record ToolShare
{
    internal const string Format = "revit-model-mcp/tool";
    internal const int FormatVersion = 1;
    internal const int MaxFileBytes = 4 * 1024 * 1024;
    internal const string PackFormat = "revit-model-mcp/tool-pack";
    internal const int PackFormatVersion = 1;
    internal const int MaxPackBytes = 16 * 1024 * 1024;
    internal const int MaxPackTools = 100;
    internal string Name { get; init; } = "";
    internal string Label { get; init; } = "";
    internal string Description { get; init; } = "";
    internal string Mode { get; init; } = "read";
    internal int[] TestedRevitYears { get; init; } = [];
    internal string Transaction { get; init; } = "auto";
    internal string Parameters { get; init; } = "";
    internal string Code { get; init; } = "";
    internal string Icon16 { get; init; } = "";
    internal string Icon32 { get; init; } = "";

    internal static ToolShare From(ToolDefinition definition, ToolPresentation presentation) => new()
    {
        Name = definition.Name,
        Label = presentation.Label,
        Description = definition.Description,
        Mode = definition.Mode,
        TestedRevitYears = definition.TestedRevitYears.Distinct().OrderBy(year => year).ToArray(),
        Transaction = presentation.TransactionMode,
        Parameters = definition.Parameters,
        Code = ToolDefinition.Normalize(definition.Code),
        Icon16 = presentation.SmallPng,
        Icon32 = presentation.LargePng
    };

    private KeyValuePair<string, object>[] Members() =>
    [
        new("format", Format), new("formatVersion", FormatVersion), new("name", Name), new("label", Label),
        new("description", Description), new("mode", Mode), new("testedRevitYears", TestedRevitYears),
        new("transaction", Transaction), new("parameters", Parameters), new("code", Code),
        new("icon16", Icon16), new("icon32", Icon32)
    ];

    internal string Fingerprint()
    {
        var builder = new StringBuilder("{");
        foreach (var member in Members().OrderBy(member => member.Key, StringComparer.Ordinal))
        {
            if (builder.Length > 1) builder.Append(',');
            CodeResultLimiter.WriteJson(builder, member.Key);
            builder.Append(':');
            CodeResultLimiter.WriteJson(builder, member.Value);
        }
        return CodeSource.CodeHash(builder.Append('}').ToString());
    }

    internal string ToFileText()
    {
        var builder = new StringBuilder("{\n");
        foreach (var member in Members().Concat([new KeyValuePair<string, object>("fingerprint", Fingerprint())]))
        {
            if (builder.Length > 2) builder.Append(",\n");
            builder.Append("  ");
            CodeResultLimiter.WriteJson(builder, member.Key);
            builder.Append(": ");
            CodeResultLimiter.WriteJson(builder, member.Value);
        }
        return builder.Append("\n}\n").ToString();
    }

    internal static string ToPackText(IReadOnlyList<ToolShare> tools)
    {
        if (tools.Count is 0 or > MaxPackTools) throw new ArgumentException("A tool pack must contain 1 to 100 tools.");
        if (tools.Select(tool => tool.Name).Distinct(StringComparer.Ordinal).Count() != tools.Count)
            throw new ArgumentException("Duplicate tool name in the file.");
        var text = "{\n  \"format\": \"" + PackFormat + "\",\n  \"formatVersion\": " + PackFormatVersion.ToString(CultureInfo.InvariantCulture) + ",\n  \"tools\": [\n" +
            string.Join(",\n", tools.Select(tool => string.Join("\n", tool.ToFileText().TrimEnd('\n').Split('\n').Select(line => "    " + line)))) +
            "\n  ]\n}\n";
        if (Encoding.UTF8.GetByteCount(text) > MaxPackBytes) throw new ArgumentException("The pack is larger than 16 MB. Export fewer tools at once.");
        return text;
    }

    internal sealed record ShareEntry(ToolShare? Share, string Name, string? Refusal);

    internal static string? ImportCountRefusal(int count) => count > ToolStore.MaxTools
        ? "Too many tools in one import. Select fewer files."
        : null;

    internal string ImportState(ToolDefinition? installed, ToolPresentation? presentation, bool invalid) =>
        installed is null ? invalid ? "Replaces existing" : "New" :
        From(installed, presentation!).Fingerprint() == Fingerprint() ? "Already installed" : "Replaces existing";

    internal static IReadOnlyList<ShareEntry> ParseFile(byte[] bytes)
    {
        if (bytes.Length > MaxPackBytes) throw new ArgumentException("Tool pack exceeds 16 MB.");
        if (!StrictUtf8.TryDecode(Utf8Bom.Strip(bytes), out var text)) throw new ArgumentException("Tool file must contain valid UTF-8.");
        XElement root;
        Dictionary<string, XElement> members;
        try
        {
            var quotas = new XmlDictionaryReaderQuotas { MaxDepth = 6, MaxStringContentLength = MaxPackBytes, MaxArrayLength = MaxPackBytes };
            using var reader = JsonReaderWriterFactory.CreateJsonReader(Encoding.UTF8.GetBytes(text), quotas);
            root = XElement.Load(reader);
            if (Kind(root) != "object") throw new ArgumentException();
            members = root.Elements().ToDictionary(Key, StringComparer.Ordinal);
        }
        catch (Exception exception) when (exception is XmlException or ArgumentException or InvalidOperationException)
        {
            throw new ArgumentException("Tool file must contain a JSON object with unique members.");
        }
        if (members.TryGetValue("format", out var format) && Kind(format) == "string" && format.Value == Format)
        {
            var share = Parse(bytes);
            return [new ShareEntry(share, share.Name, null)];
        }
        if (format is null || Kind(format) != "string" || format.Value != PackFormat)
            throw new ArgumentException("This is not a Model MCP tool file.");
        if (members.Keys.Except(["format", "formatVersion", "tools"], StringComparer.Ordinal).Any())
            throw new ArgumentException("Tool pack contains an unknown member.");
        if (!members.TryGetValue("formatVersion", out var version) || Kind(version) != "number" ||
            version.Value != PackFormatVersion.ToString(CultureInfo.InvariantCulture))
            throw new ArgumentException("Unsupported tool pack formatVersion.");
        if (!members.TryGetValue("tools", out var tools) || Kind(tools) != "array")
            throw new ArgumentException("Tool pack tools must be an array.");
        var entries = tools.Elements().ToArray();
        if (entries.Length is 0 or > MaxPackTools) throw new ArgumentException("A tool pack must contain 1 to 100 tools.");
        var result = new List<ShareEntry>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            var name = entry.Elements().FirstOrDefault(member => Key(member) == "name" && Kind(member) == "string")?.Value;
            var displayName = $"entry {result.Count + 1}";
            try
            {
                ToolStore.ValidateName(name!);
                displayName = name!;
            }
            catch (ArgumentException) { }
            if (name is not null && !names.Add(name))
            {
                result.Add(new ShareEntry(null, displayName, "Duplicate tool name in the file."));
                continue;
            }
            try
            {
                using var stream = new MemoryStream();
                using (var writer = JsonReaderWriterFactory.CreateJsonWriter(stream, Encoding.UTF8, false))
                {
                    var element = new XElement(entry) { Name = "root" };
                    element.WriteTo(writer);
                    writer.Flush();
                }
                var share = Parse(stream.ToArray());
                result.Add(new ShareEntry(share, share.Name, null));
            }
            catch (Exception exception) when (exception is ArgumentException or XmlException or InvalidOperationException)
            {
                result.Add(new ShareEntry(null, displayName, exception is ArgumentException ? exception.Message : "Invalid tool entry."));
            }
        }
        return result;
    }

    internal ToolDefinition ToDefinition(string name, string author, string created, string replacedRevision) => new()
    {
        Name = name,
        Description = Description,
        Parameters = Parameters,
        Code = ToolDefinition.Normalize(Code),
        Mode = Mode,
        TestedRevitYears = TestedRevitYears.ToArray(),
        Author = author,
        Created = created,
        ReplacedRevision = replacedRevision,
        Revision = ToolDefinition.Hash(Code)
    };

    internal ToolPresentation ApplyTo(ToolPresentation existing) => existing with
    {
        Label = Label,
        SmallPng = Icon16,
        LargePng = Icon32,
        TransactionMode = Transaction
    };

    internal static ToolShare Parse(byte[] bytes)
    {
        if (bytes.Length > MaxFileBytes) throw new ArgumentException("Tool file exceeds 4 MB.");
        if (!StrictUtf8.TryDecode(Utf8Bom.Strip(bytes), out var text)) throw new ArgumentException("Tool file must contain valid UTF-8.");
        Dictionary<string, XElement> members;
        try
        {
            var quotas = new XmlDictionaryReaderQuotas { MaxDepth = 4, MaxStringContentLength = MaxFileBytes, MaxArrayLength = MaxFileBytes };
            using var reader = JsonReaderWriterFactory.CreateJsonReader(Encoding.UTF8.GetBytes(text), quotas);
            var root = XElement.Load(reader);
            if (Kind(root) != "object") throw new ArgumentException();
            members = root.Elements().ToDictionary(Key, StringComparer.Ordinal);
        }
        catch (Exception exception) when (exception is XmlException or ArgumentException or InvalidOperationException)
        {
            throw new ArgumentException("Tool file must contain one flat JSON object with unique members.");
        }
        var expected = new ToolShare().Members().Select(member => member.Key).Concat(["fingerprint"]).ToArray();
        if (members.Keys.Except(expected, StringComparer.Ordinal).Any()) throw new ArgumentException("Tool file contains an unknown member.");
        foreach (var name in expected)
            if (!members.ContainsKey(name)) throw new ArgumentException($"Tool file is missing {name}.");
        string ReadString(string name)
        {
            var member = members[name];
            if (Kind(member) != "string" || member.HasElements) throw new ArgumentException($"Invalid {name} member.");
            return member.Value;
        }
        if (ReadString("format") != Format) throw new ArgumentException("This is not a Model MCP tool file.");
        var version = members["formatVersion"];
        if (Kind(version) != "number" || !int.TryParse(version.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var number))
            throw new ArgumentException("Invalid formatVersion member.");
        if (number > FormatVersion) throw new ArgumentException("A newer Model MCP version is needed to import this file.");
        if (number != FormatVersion) throw new ArgumentException("Unsupported formatVersion.");
        var yearsMember = members["testedRevitYears"];
        if (Kind(yearsMember) != "array") throw new ArgumentException("Invalid testedRevitYears member.");
        var years = new List<int>();
        foreach (var item in yearsMember.Elements())
        {
            if (Kind(item) != "number" || !int.TryParse(item.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var year) ||
                (years.Count != 0 && year <= years[years.Count - 1])) throw new ArgumentException("testedRevitYears must contain distinct ascending integers.");
            years.Add(year);
        }
        var share = new ToolShare
        {
            Name = ReadString("name"),
            Label = ReadString("label"),
            Description = ReadString("description"),
            Mode = ReadString("mode"),
            TestedRevitYears = years.ToArray(),
            Transaction = ReadString("transaction"),
            Parameters = ReadString("parameters"),
            Code = ReadString("code"),
            Icon16 = ReadString("icon16"),
            Icon32 = ReadString("icon32")
        };
        try { share.ToDefinition(share.Name, "", "", "").Validate(); }
        catch (Exception exception) when (exception is ArgumentException or XmlException or InvalidOperationException)
        {
            throw new ArgumentException("Tool definition is invalid. Check name, description, mode, code, parameters and testedRevitYears.");
        }
        try { ToolPresentationStore.Validate(share.ApplyTo(new ToolPresentation())); }
        catch (Exception exception) when (exception is ArgumentException or FormatException)
        {
            throw new ArgumentException("Tool presentation is invalid. Check label, transaction, icon16 and icon32.");
        }
        if ((share.Icon16.Length == 0) != (share.Icon32.Length == 0)) throw new ArgumentException("Both icon16 and icon32 must be present or empty.");
        if (share.Icon16.Length != 0)
        {
            StoredPng(share.Icon16, 16);
            StoredPng(share.Icon32, 32);
        }
        if (share.Code != ToolDefinition.Normalize(share.Code)) throw new ArgumentException("Tool code must be normalized before export.");
        if (ReadString("fingerprint") != share.Fingerprint()) throw new ArgumentException("The file does not match its fingerprint and should be exported again.");
        return share;
    }

    private static string Kind(XElement element) => (string?)element.Attribute("type") ?? "string";
    private static string Key(XElement element) => (string?)element.Attribute("item") ?? element.Name.LocalName;

    internal static byte[] StoredPng(string png, int size)
    {
        if (png.Length > 400000) throw new ArgumentException("Stored icon is too large.");
        byte[] bytes;
        try { bytes = Convert.FromBase64String(png); }
        catch (FormatException) { throw new ArgumentException("Stored icon is not valid base64."); }
        byte[] signature = [137, 80, 78, 71, 13, 10, 26, 10];
        if (bytes.Length < 33 || !bytes.Take(8).SequenceEqual(signature) ||
            bytes[8] != 0 || bytes[9] != 0 || bytes[10] != 0 || bytes[11] != 13 ||
            bytes[12] != 'I' || bytes[13] != 'H' || bytes[14] != 'D' || bytes[15] != 'R' ||
            !Dimension(16) || !Dimension(20)) throw new ArgumentException("Stored PNG dimensions are invalid.");
        return bytes;

        bool Dimension(int offset) => bytes[offset] == 0 && bytes[offset + 1] == 0 && bytes[offset + 2] == 0 && bytes[offset + 3] == size;
    }

    internal sealed record Finding(string Kind, string Field, string Excerpt);
    internal sealed record PrivateData(IReadOnlyList<Finding> Findings, int TotalCount, bool Incomplete = false);

    internal PrivateData FindPrivateData(IEnumerable<(string Kind, string Term)>? localTerms = null) =>
        FindPrivateData(localTerms, TimeSpan.FromSeconds(1));

    internal sealed record BatchPrivateData(ToolShare Share, PrivateData? Data)
    {
        internal bool NotScanned => Data is null;
        internal bool Incomplete => Data is null || Data.Incomplete;
    }

    internal static IReadOnlyList<BatchPrivateData> FindPrivateDataBatch(
        IReadOnlyList<ToolShare> tools, IEnumerable<(string Kind, string Term)>? localTerms = null,
        Func<TimeSpan>? elapsed = null)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        elapsed ??= () => stopwatch.Elapsed;
        var terms = localTerms?.ToArray();
        var result = new List<BatchPrivateData>();
        foreach (var tool in tools)
        {
            var remaining = TimeSpan.FromSeconds(3) - elapsed();
            result.Add(new BatchPrivateData(tool, remaining <= TimeSpan.Zero ? null :
                tool.FindPrivateData(terms, remaining < TimeSpan.FromSeconds(1) ? remaining : TimeSpan.FromSeconds(1))));
        }
        return result;
    }

    private PrivateData FindPrivateData(IEnumerable<(string Kind, string Term)>? localTerms, TimeSpan budget)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var findings = new List<Finding>();
        var total = 0;
        var patterns = new List<(string Kind, string Pattern)>
        {
            ("file path", @"(?<![\w])[a-z]:[\\/]{1,16}[^\s""<>]{1,2048}|(?<!\\)\\{2,16}[^\s\\""<>]{1,255}\\{1,16}[^\s""<>]{1,2048}|RSN://[^\s""<>]{1,2048}"),
            ("web address", @"https?://[^\s""<>]{1,2048}"),
            ("email address", @"(?<![a-z0-9.!#$%&'*+/=?^_`{|}~-])[a-z0-9.!#$%&'*+/=?^_`{|}~-]{1,64}@[a-z0-9-]{1,63}(?:\.[a-z0-9-]{1,63}){1,10}")
        };
        var distinctTerms = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (kind, term) in localTerms ?? [])
        {
            if (stopwatch.Elapsed >= budget) return new PrivateData(findings, total, Incomplete: true);
            var trimmed = term.Trim();
            if (trimmed.Length >= 4 && distinctTerms.Add(trimmed))
                patterns.Add((kind, @"(?<![\p{L}\p{N}_])" + Regex.Escape(trimmed) + @"(?![\p{L}\p{N}_])"));
        }
        try
        {
            foreach (var (field, value) in new[] { ("name", Name), ("label", Label), ("description", Description), ("parameters", Parameters), ("code", Code) })
                foreach (var (kind, pattern) in patterns)
                {
                    var remaining = budget - stopwatch.Elapsed;
                    if (remaining <= TimeSpan.Zero) return new PrivateData(findings, total, Incomplete: true);
                    foreach (Match match in Regex.Matches(value, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, remaining))
                    {
                        if (stopwatch.Elapsed >= budget) return new PrivateData(findings, total, Incomplete: true);
                        total++;
                        if (findings.Count < 50)
                            findings.Add(new Finding(kind, field, string.Concat(match.Value.Take(60).Select(character => char.IsControl(character) ? ' ' : character))));
                    }
                }
        }
        catch (RegexMatchTimeoutException)
        {
            return new PrivateData(findings, total, Incomplete: true);
        }
        return new PrivateData(findings, total);
    }
}
