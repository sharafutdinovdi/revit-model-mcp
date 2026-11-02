using System.Globalization;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace RevitModelMcp.Core.Control;

[DataContract]
internal sealed record ToolDefinition
{
    [DataMember(Name = "name")] public string Name { get; init; } = "";
    [DataMember(Name = "description")] public string Description { get; init; } = "";
    [DataMember(Name = "parameters")] public string Parameters { get; init; } = "";
    [DataMember(Name = "code")] public string Code { get; init; } = "";
    [DataMember(Name = "mode")] public string Mode { get; init; } = "action";
    [DataMember(Name = "testedRevitYears")] public int[] TestedRevitYears { get; init; } = [];
    [DataMember(Name = "created")] public string Created { get; init; } = "";
    [DataMember(Name = "author")] public string Author { get; init; } = "";
    [DataMember(Name = "revision")] public string Revision { get; init; } = "";
    [DataMember(Name = "replacedRevision")] public string ReplacedRevision { get; init; } = "";

    public static string Normalize(string source) => source.Replace("\r\n", "\n").Replace('\r', '\n').Trim() + "\n";
    public static string Hash(string source) => CodeSource.CodeHash(Normalize(source));

    public void Validate()
    {
        RejectFormat(Name);
        RejectFormat(Description);
        RejectFormat(Code);
        ToolStore.ValidateName(Name);
        if (string.IsNullOrWhiteSpace(Description) || Description.Length > 4000)
            throw new ArgumentException("description must contain 1 to 4000 characters.");
        if (Mode is not ("read" or "action")) throw new ArgumentException("mode must be read or action.");
        if (string.IsNullOrWhiteSpace(Code) || Encoding.UTF8.GetByteCount(Code) > ToolStore.MaxSourceBytes)
            throw new ArgumentException("source must contain 1 to 256 KB of UTF-8 text.");
        if (TestedRevitYears is null || TestedRevitYears.Any(year => year is < 2022 or > 2027))
            throw new ArgumentException("tested Revit years must be between 2022 and 2027.");
        ToolArguments.ValidateSchema(Parameters);
    }

    internal static void RejectFormat(string? text)
    {
        if (text is null) throw new ArgumentException("Tool definition text is required.");
        if (Enumerable.Range(0, text.Length).Any(index => CharUnicodeInfo.GetUnicodeCategory(text, index) == UnicodeCategory.Format))
            throw new ArgumentException("Unicode Format characters are not allowed in tool definitions.");
    }

    public string ApprovalBinding() => CodeSource.CodeHash(ToolJson.Serialize(this with { Created = "", Author = "" }));
}

public static class ToolArguments
{
    private static XElement Parse(string json)
    {
        if (Encoding.UTF8.GetByteCount(json) > 65536) throw new ArgumentException("parameters or args exceed 64 KB.");
        using var reader = JsonReaderWriterFactory.CreateJsonReader(Encoding.UTF8.GetBytes(json), XmlDictionaryReaderQuotas.Max);
        return XElement.Load(reader);
    }

    private static string Kind(XElement element) => (string?)element.Attribute("type") ?? "string";
    private static string Key(XElement element) => (string?)element.Attribute("item") ?? element.Name.LocalName;
    private static Dictionary<string, XElement> Members(XElement element)
    {
        if (Kind(element) != "object") throw new ArgumentException("Expected a JSON object.");
        try { return element.Elements().ToDictionary(Key, StringComparer.Ordinal); }
        catch (ArgumentException) { throw new ArgumentException("Duplicate JSON properties are not allowed."); }
    }

    public static void ValidateSchema(string json)
    {
        var schema = Members(Parse(json));
        if (schema.Keys.Except(["type", "properties", "required", "additionalProperties"]).Any())
            throw new ArgumentException("parameters contains unsupported schema keywords.");
        if (!schema.TryGetValue("type", out var type) || type.Value != "object" || Kind(type) != "string")
            throw new ArgumentException("parameters.type must be object.");
        if (schema.TryGetValue("additionalProperties", out var additional) &&
            (Kind(additional) != "boolean" || additional.Value != "false"))
            throw new ArgumentException("additionalProperties must be false.");
        var properties = schema.TryGetValue("properties", out var propertyNode) ? Members(propertyNode) : [];
        foreach (var property in properties)
        {
            ToolDefinition.RejectFormat(property.Key);
            var rules = Members(property.Value);
            if (rules.Keys.Except(["type", "description", "enum", "minimum", "maximum", "maxLength"]).Any())
                throw new ArgumentException($"parameters.{property.Key}: unsupported schema keyword.");
            if (!rules.TryGetValue("type", out var primitive) || Kind(primitive) != "string" ||
                primitive.Value is not ("string" or "number" or "integer" or "boolean"))
                throw new ArgumentException($"parameters.{property.Key}: type must be string, number, integer or boolean.");
            if (rules.TryGetValue("description", out var description) && Kind(description) != "string")
                throw new ArgumentException($"parameters.{property.Key}.description must be text.");
            foreach (var bound in new[] { "minimum", "maximum", "maxLength" })
                if (rules.TryGetValue(bound, out var value))
                {
                    if (Kind(value) != "number" || !double.TryParse(value.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) ||
                        double.IsNaN(number) || double.IsInfinity(number) ||
                        (bound == "maxLength" ? primitive.Value != "string" || number < 0 || number != Math.Truncate(number) : primitive.Value is not ("number" or "integer")))
                        throw new ArgumentException($"parameters.{property.Key}.{bound} is invalid for its type.");
                }
            if (rules.TryGetValue("minimum", out var minimum) && rules.TryGetValue("maximum", out var maximum) && CompareNumeric(minimum, maximum) > 0)
                throw new ArgumentException($"parameters.{property.Key}: minimum exceeds maximum.");
            if (rules.TryGetValue("enum", out var enumeration))
            {
                if (Kind(enumeration) != "array" || !enumeration.HasElements)
                    throw new ArgumentException($"parameters.{property.Key}.enum must be a nonempty array.");
                foreach (var value in enumeration.Elements()) ValidateValue(property.Key, value, rules, false);
            }
        }
        if (schema.TryGetValue("required", out var required) &&
            (Kind(required) != "array" || required.Elements().Any(item => Kind(item) != "string" || !properties.ContainsKey(item.Value)) ||
             required.Elements().Select(item => item.Value).Distinct().Count() != required.Elements().Count()))
            throw new ArgumentException("required must contain distinct declared parameter names.");
    }

    private static double Numeric(XElement value) => double.Parse(value.Value, CultureInfo.InvariantCulture);

    private static int CompareNumeric(XElement left, XElement right) =>
        decimal.TryParse(left.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var leftDecimal) &&
        decimal.TryParse(right.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var rightDecimal)
            ? leftDecimal.CompareTo(rightDecimal)
            : Numeric(left).CompareTo(Numeric(right));

    private static object ValidateValue(string name, XElement value, Dictionary<string, XElement> rules, bool checkEnum)
    {
        var type = rules["type"].Value;
        object typed;
        if (type == "string" && Kind(value) == "string") typed = value.Value;
        else if (type == "boolean" && Kind(value) == "boolean") typed = value.Value == "true";
        else if (type is "number" or "integer" && Kind(value) == "number" &&
                 double.TryParse(value.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) &&
                 !double.IsNaN(number) && !double.IsInfinity(number) && (type != "integer" || number == Math.Truncate(number)))
        {
            if (type == "integer")
            {
                if (!decimal.TryParse(value.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var integer) || integer < long.MinValue || integer > long.MaxValue || integer != decimal.Truncate(integer))
                    throw new ArgumentException($"args.{name}: integer must fit Int64.");
                typed = (long)integer;
            }
            else typed = number;
            if (rules.TryGetValue("minimum", out var minimum) && CompareNumeric(value, minimum) < 0) throw new ArgumentException($"args.{name}: below minimum.");
            if (rules.TryGetValue("maximum", out var maximum) && CompareNumeric(value, maximum) > 0) throw new ArgumentException($"args.{name}: above maximum.");
        }
        else throw new ArgumentException($"args.{name}: expected {type}.");
        if (typed is string text && rules.TryGetValue("maxLength", out var length) && text.Where((character, index) => !char.IsLowSurrogate(character) || index == 0 || !char.IsHighSurrogate(text[index - 1])).Count() > Numeric(length))
            throw new ArgumentException($"args.{name}: exceeds maxLength.");
        if (checkEnum && rules.TryGetValue("enum", out var enumeration) && !enumeration.Elements().Any(item =>
                Kind(item) == Kind(value) && (Kind(value) == "number" ? CompareNumeric(item, value) == 0 : item.Value == value.Value)))
            throw new ArgumentException($"args.{name}: value is not in enum.");
        return typed;
    }

    public static IReadOnlyDictionary<string, object> Validate(string schemaJson, string argsJson)
    {
        ValidateSchema(schemaJson);
        var schema = Members(Parse(schemaJson));
        var properties = schema.TryGetValue("properties", out var propertyNode) ? Members(propertyNode) : [];
        var args = Members(Parse(argsJson));
        if (schema.TryGetValue("required", out var required))
            foreach (var item in required.Elements())
                if (!args.ContainsKey(item.Value)) throw new ArgumentException($"args.{item.Value}: required parameter is missing.");
        var result = new Dictionary<string, object>(StringComparer.Ordinal);
        foreach (var argument in args)
        {
            if (!properties.TryGetValue(argument.Key, out var rules)) throw new ArgumentException($"args.{argument.Key}: unknown parameter.");
            result[argument.Key] = ValidateValue(argument.Key, argument.Value, Members(rules), true);
        }
        return result;
    }
}

internal sealed class ToolStore(string directory)
{
    public const int MaxSourceBytes = 256 * 1024;
    public const int MaxTools = 200;
    private readonly string _directory = Path.GetFullPath(directory);
    public static string DefaultDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RevitModelMcp", "tools");

    public static void ValidateName(string name)
    {
        if (name is null || !Regex.IsMatch(name, @"\A[a-z][a-z0-9_]{2,47}\z"))
            throw new ArgumentException("name must match ^[a-z][a-z0-9_]{2,47}$.");
    }

    public IReadOnlyList<ToolDefinition> List() => List(out _);

    public IReadOnlyList<ToolDefinition> List(out List<InvalidTool> invalid)
    {
        invalid = [];
        var tools = new List<ToolDefinition>();
        if (!Directory.Exists(_directory)) return tools;
        foreach (var folder in Directory.GetDirectories(_directory).OrderBy(Path.GetFileName, StringComparer.Ordinal))
        {
            var name = Path.GetFileName(folder);
            try { tools.Add(Get(name)); }
            catch (Exception exception) when (exception is InvalidOperationException or ArgumentException)
            {
                invalid.Add(new InvalidTool { Name = name, Reason = "revision not approved" });
            }
        }
        return tools;
    }

    public string DeletionBinding(string name)
    {
        ValidateName(name);
        if (!Directory.Exists(Path.Combine(_directory, name))) throw new InvalidOperationException("Tool not found.");
        try { return Get(name).ApprovalBinding(); }
        catch (InvalidOperationException) { return "not approved"; }
    }

    public ToolDefinition Get(string name)
    {
        ValidateName(name);
        try { return ReadApproved(name); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or SerializationException or XmlException or ArgumentException or InvalidOperationException)
        {
            throw new InvalidOperationException("revision not approved");
        }
    }

    private ToolDefinition ReadApproved(string name)
    {
        var folder = Path.Combine(_directory, name);
        var activePath = Path.Combine(folder, "active");
        if (new FileInfo(activePath).Length > 128) throw new InvalidOperationException("revision not approved");
        var revision = File.ReadAllText(activePath).Trim();
        if (!Regex.IsMatch(revision, @"\A[a-f0-9]{64}\z")) throw new InvalidOperationException("revision not approved");
        var path = Path.Combine(folder, revision + ".json");
        if (new FileInfo(path).Length > MaxSourceBytes * 8) throw new InvalidOperationException("revision not approved");
        var definition = ToolJson.Deserialize<ToolDefinition>(File.ReadAllText(path));
        if (definition is null || definition.Code is null || definition.Name != name || definition.Revision != revision || ToolDefinition.Hash(definition.Code) != revision)
            throw new InvalidOperationException("revision not approved");
        var approvalPath = Path.Combine(folder, revision + ".approved");
        if (new FileInfo(approvalPath).Length != 44) throw new InvalidOperationException("revision not approved");
        var approval = File.ReadAllText(approvalPath);
        if (approval != ApprovalSignature(definition)) throw new InvalidOperationException("revision not approved");
        definition.Validate();
        return definition;
    }

    public void Save(ToolDefinition definition)
    {
        definition.Validate();
        if (definition.Revision != ToolDefinition.Hash(definition.Code)) throw new InvalidOperationException("revision not approved");
        Directory.CreateDirectory(_directory);
        using var storeLock = AcquireLock();
        var folder = Path.Combine(_directory, definition.Name);
        if (!File.Exists(Path.Combine(folder, "active")) && Directory.GetDirectories(_directory).Count(path => File.Exists(Path.Combine(path, "active"))) >= MaxTools)
            throw new InvalidOperationException("The saved tool limit is 200.");
        Directory.CreateDirectory(folder);
        var keyPath = Path.Combine(_directory, ".approval-key");
        if (!File.Exists(keyPath))
        {
            var key = new byte[32];
            using var random = RandomNumberGenerator.Create();
            random.GetBytes(key);
            using var keyFile = new FileStream(keyPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            keyFile.Write(key, 0, key.Length);
            keyFile.Flush(true);
        }
        AtomicWrite(Path.Combine(folder, definition.Revision + ".json"), ToolJson.Serialize(definition));
        AtomicWrite(Path.Combine(folder, definition.Revision + ".approved"), ApprovalSignature(definition));
        AtomicWrite(Path.Combine(folder, "active"), definition.Revision);
        foreach (var old in Directory.GetFiles(folder, "*.json")
                     .Where(path => Path.GetFileNameWithoutExtension(path) != definition.Revision)
                     .OrderByDescending(File.GetLastWriteTimeUtc).ThenBy(path => path, StringComparer.Ordinal).Skip(19))
        {
            File.Delete(old);
            File.Delete(Path.ChangeExtension(old, ".approved"));
        }
    }

    private string ApprovalSignature(ToolDefinition definition)
    {
        var keyPath = Path.Combine(_directory, ".approval-key");
        if (new FileInfo(keyPath).Length != 32) throw new InvalidOperationException("revision not approved");
        var key = File.ReadAllBytes(keyPath);
        if (key.Length != 32) throw new InvalidOperationException("revision not approved");
        using var signature = new HMACSHA256(key);
        return Convert.ToBase64String(signature.ComputeHash(Encoding.UTF8.GetBytes(ToolJson.Serialize(definition))));
    }

    public void Delete(string name, string? expectedApprovalBinding = null)
    {
        ValidateName(name);
        using var storeLock = AcquireLock();
        if (expectedApprovalBinding is not null && DeletionBinding(name) != expectedApprovalBinding)
            throw new InvalidOperationException("The tool changed. Review it again before deleting.");
        Directory.Delete(Path.Combine(_directory, name), true);
    }

    private FileStream AcquireLock()
    {
        var timer = System.Diagnostics.Stopwatch.StartNew();
        while (true)
        {
            try { return new FileStream(Path.Combine(_directory, ".lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                if (timer.ElapsedMilliseconds >= 2000) throw new InvalidOperationException("My tools store is busy");
                Thread.Sleep(25);
            }
        }
    }

    private static void AtomicWrite(string path, string text)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                var bytes = Encoding.UTF8.GetBytes(text);
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush(true);
            }
            if (File.Exists(path)) File.Replace(temporary, path, null);
            else File.Move(temporary, path);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}

public static class ToolJson
{
    public static string Serialize<T>(T value)
    {
        using var stream = new MemoryStream();
        new DataContractJsonSerializer(typeof(T)).WriteObject(stream, value);
        return Encoding.UTF8.GetString(stream.ToArray());
    }
    public static T Deserialize<T>(string json)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
        return (T)new DataContractJsonSerializer(typeof(T)).ReadObject(stream)!;
    }
}

[DataContract]
internal sealed record InvalidTool
{
    [DataMember(Name = "name")] public string Name { get; init; } = "";
    [DataMember(Name = "reason")] public string Reason { get; init; } = "";
}

internal sealed class ToolSession
{
    private readonly HashSet<string> _confirmed = new(StringComparer.Ordinal);

    public bool RequiresConfirmation(string name, string revision, string mode, bool dryRun) =>
        !_confirmed.Contains(name + ":" + revision) || mode == "action" && !dryRun;

    public void Confirm(string name, string revision) => _confirmed.Add(name + ":" + revision);

    public static string CacheKey(string revision) => "tool:" + revision;

    public static string AuditText(string name, string revision, IEnumerable<string> argumentNames) =>
        $"Tool run: name={name}; revision={revision}; arg names={ToolJson.Serialize(argumentNames.ToArray())}";

    public static string RunText(ToolDefinition tool, IReadOnlyDictionary<string, object> args)
    {
        var validated = CodeResultLimiter.ToJson(args);
        if (validated.Length > 300) validated = validated[..300];
        var replaced = string.IsNullOrEmpty(tool.ReplacedRevision) ? "no earlier revision" : "replaced " + tool.ReplacedRevision[..12];
        return $"Run {tool.Name}, mode {tool.Mode}, revision {tool.Revision[..12]}, saved {tool.Created}, {replaced}. Validated args: {validated}. C# has the user's file, network and Revit API rights.";
    }

    public static string SaveText(ToolDefinition tool, ToolDefinition? previous) =>
        $"Save {tool.Name}, mode {tool.Mode}, revision {tool.Revision[..12]}" +
        (previous is null ? "." : $", replaces {previous.Revision[..12]}, mode {previous.Mode} -> {tool.Mode}.") +
        " Review the full source and parameters. Saved C# has the user's file, network and Revit API rights.";
}
