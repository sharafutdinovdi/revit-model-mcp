using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Runtime.Serialization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace RevitModelMcp.Core.Control;

public static class CodeSource
{
    private static readonly Regex ScriptClass = new(@"\bpublic\s+static\s+class\s+Script\b", RegexOptions.Compiled);

    internal sealed record AssemblyReference(string Identity, string Location, bool DefinesRevitExtensions = false);

    internal static IReadOnlyList<AssemblyReference> SelectReferences(
        IEnumerable<AssemblyReference> candidates, IReadOnlyList<AssemblyReference> boundAssemblies)
    {
        if (candidates is null) throw new ArgumentNullException(nameof(candidates));
        if (boundAssemblies is null) throw new ArgumentNullException(nameof(boundAssemblies));
        var extensionOwner = boundAssemblies.Single(reference => reference.DefinesRevitExtensions);
        var preferred = boundAssemblies.ToDictionary(reference => new AssemblyName(reference.Identity).Name!,
            StringComparer.OrdinalIgnoreCase);
        return boundAssemblies.Concat(candidates)
            .Where(reference => !reference.DefinesRevitExtensions ||
                string.Equals(reference.Location, extensionOwner.Location, StringComparison.OrdinalIgnoreCase))
            .GroupBy(reference => new AssemblyName(reference.Identity).Name!, StringComparer.OrdinalIgnoreCase)
            .Select(group => preferred.TryGetValue(group.Key, out var bound) ? bound : group.First())
            .ToList();
    }

    public static bool IsCompilationUnit(string code) => ScriptClass.IsMatch(code);

    public static string CacheKey(string code, string transaction)
    {
        using var sha = SHA256.Create();
        return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(transaction + "\0" + code)))
            .Replace("-", "").ToLowerInvariant();
    }

    public static string CodeHash(string code)
    {
        using var sha = SHA256.Create();
        return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(code)))
            .Replace("-", "").ToLowerInvariant();
    }

    public static string FirstLine(string code) => code.Split(['\r', '\n'])
        .Select(line => line.Trim()).FirstOrDefault(line => line.Length > 0) ?? string.Empty;

    public static string BuildSource(string code)
    {
        const string common = "using RevitModelMcp.Control;\n";
        if (IsCompilationUnit(code)) return common + "#line 1 \"submitted.cs\"\n" + code;

        const string imports = "using System;\nusing System.Collections.Generic;\nusing System.Linq;\nusing System.Text;\n" +
                               "using Autodesk.Revit.DB;\nusing Autodesk.Revit.DB.Architecture;\n" +
                               "using Autodesk.Revit.DB.Structure;\nusing Autodesk.Revit.DB.Mechanical;\n" +
                               "using Autodesk.Revit.DB.Plumbing;\nusing Autodesk.Revit.DB.Electrical;\n" +
                               "using Autodesk.Revit.UI;\nusing Autodesk.Revit.UI.Selection;\n";
        return common + imports + "public static class Script { public static object Execute(ScriptContext ctx) {\n" +
               "#line 1 \"submitted.cs\"\n" + code + "\n#line default\n} }";
    }
}

[DataContract]
public sealed class CodeDiagnostic
{
    [DataMember(Name = "line")] public int Line { get; init; }
    [DataMember(Name = "column")] public int Column { get; init; }
    [DataMember(Name = "id")] public string Id { get; init; } = string.Empty;
    [DataMember(Name = "message")] public string Message { get; init; } = string.Empty;
}

public static class CodeResultLimiter
{
    public static string ToJson(object? value)
    {
        var builder = new StringBuilder();
        WriteJson(builder, value);
        return builder.ToString();
    }

    private static void WriteJson(StringBuilder builder, object? value)
    {
        switch (value)
        {
            case null:
                builder.Append("null");
                break;
            case string text:
                builder.Append('"');
                foreach (var character in text)
                {
                    switch (character)
                    {
                        case '"': builder.Append("\\\""); break;
                        case '\\': builder.Append("\\\\"); break;
                        case '\n': builder.Append("\\n"); break;
                        case '\r': builder.Append("\\r"); break;
                        case '\t': builder.Append("\\t"); break;
                        default:
                            if (character < ' ') builder.Append("\\u").Append(((int)character).ToString("x4"));
                            else builder.Append(character);
                            break;
                    }
                }
                builder.Append('"');
                break;
            case bool boolean:
                builder.Append(boolean ? "true" : "false");
                break;
            case double nonFiniteDouble when double.IsNaN(nonFiniteDouble) || double.IsInfinity(nonFiniteDouble):
            case float nonFiniteFloat when float.IsNaN(nonFiniteFloat) || float.IsInfinity(nonFiniteFloat):
                builder.Append("null");
                break;
            case IFormattable number:
                builder.Append(number.ToString(null, CultureInfo.InvariantCulture));
                break;
            case IDictionary dictionary:
                builder.Append('{');
                var firstProperty = true;
                foreach (DictionaryEntry entry in dictionary)
                {
                    if (!firstProperty) builder.Append(',');
                    WriteJson(builder, FormatKey(entry.Key));
                    builder.Append(':');
                    WriteJson(builder, entry.Value);
                    firstProperty = false;
                }
                builder.Append('}');
                break;
            case IEnumerable items:
                builder.Append('[');
                var firstItem = true;
                foreach (var item in items)
                {
                    if (!firstItem) builder.Append(',');
                    WriteJson(builder, item);
                    firstItem = false;
                }
                builder.Append(']');
                break;
            default:
                WriteJson(builder, value.ToString());
                break;
        }
    }

    public static CodeResult Limit(object? value, Func<object, object?> convertSpecial)
    {
        var fullValue = Normalize(value, convertSpecial, [], 0);
        var fullJson = ToJson(fullValue);
        var reasons = new HashSet<string>();
        var retained = 0;
        var total = CountEntries(fullValue);
        var inline = Visit(fullValue, 0, ref retained, reasons);
        if (total == 0)
        {
            total = 1;
            retained = 1;
        }
        if (Encoding.UTF8.GetByteCount(ToJson(inline)) > 262144)
        {
            reasons.Add("inlineSize");
            inline = null;
            retained = 0;
        }
        return new CodeResult
        {
            Value = inline,
            Truncated = reasons.Count > 0,
            RetainedCount = retained,
            OmittedCount = total - retained,
            Reasons = reasons.OrderBy(reason => reason, StringComparer.Ordinal).ToList(),
            FullJson = reasons.Count > 0 ? fullJson : null
        };
    }

    private static object? Normalize(object? value, Func<object, object?> convertSpecial, List<object> ancestors, int depth)
    {
        if (value is null or string or bool or byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal)
            return value;
        if (depth >= 128 || ancestors.Any(ancestor => ReferenceEquals(ancestor, value)))
            throw new InvalidOperationException("Result serialization does not support cycles or nesting of 128 levels.");
        ancestors.Add(value);
        try
        {
            var converted = convertSpecial(value);
            if (!ReferenceEquals(converted, value))
                return Normalize(converted, convertSpecial, ancestors, depth + 1);
            if (value is IDictionary dictionary)
            {
                var result = new Dictionary<string, object?>();
                foreach (DictionaryEntry entry in dictionary)
                {
                    var key = FormatKey(entry.Key);
                    if (result.ContainsKey(key))
                        throw new InvalidOperationException("Result dictionary keys collide after string conversion.");
                    result.Add(key, Normalize(entry.Value, convertSpecial, ancestors, depth + 1));
                }
                return result;
            }
            if (value is IEnumerable sequence)
            {
                var result = new List<object?>();
                foreach (var item in sequence) result.Add(Normalize(item, convertSpecial, ancestors, depth + 1));
                return result;
            }
            var type = value.GetType();
            if (type.IsClass && type.Assembly.GetName().Name is not ("RevitAPI" or "RevitAPIUI") &&
                !(type.Namespace?.StartsWith("Autodesk.Revit.", StringComparison.Ordinal) ?? false) &&
                !(type.Namespace?.StartsWith("System.", StringComparison.Ordinal) ?? false) &&
                type.Namespace != "System")
            {
                var properties = type.GetProperties(BindingFlags.Instance | BindingFlags.Public)
                    .Where(property => property.GetMethod?.IsPublic == true && property.GetIndexParameters().Length == 0);
                var result = new Dictionary<string, object?>();
                foreach (var property in properties)
                    result.Add(property.Name, Normalize(property.GetValue(value), convertSpecial, ancestors, depth + 1));
                if (result.Count > 0) return result;
            }
            return value.ToString();
        }
        finally
        {
            ancestors.RemoveAt(ancestors.Count - 1);
        }
    }

    private static int CountEntries(object? value) => value switch
    {
        Dictionary<string, object?> dictionary => dictionary.Sum(entry => 1 + CountEntries(entry.Value)),
        List<object?> sequence => sequence.Sum(item => 1 + CountEntries(item)),
        _ => 0
    };

    private static object? Visit(object? value, int depth, ref int retained, HashSet<string> reasons)
    {
        if (value is not Dictionary<string, object?> && value is not List<object?>) return value;
        if (depth >= 6 && CountEntries(value) > 0)
        {
            reasons.Add("depth");
            return null;
        }
        if (value is Dictionary<string, object?> dictionary)
        {
            var result = new Dictionary<string, object?>();
            foreach (var entry in dictionary)
            {
                if (retained >= 5000) { reasons.Add("itemBudget"); break; }
                retained++;
                result.Add(entry.Key, Visit(entry.Value, depth + 1, ref retained, reasons));
            }
            return result;
        }
        var items = new List<object?>();
        foreach (var item in (List<object?>)value!)
        {
            if (retained >= 5000) { reasons.Add("itemBudget"); break; }
            retained++;
            items.Add(Visit(item, depth + 1, ref retained, reasons));
        }
        return items;
    }

    private static string FormatKey(object? key) => key is IFormattable formattable
        ? formattable.ToString(null, CultureInfo.InvariantCulture) ?? "null"
        : key?.ToString() ?? "null";
}

[DataContract]
public sealed record CodeResult
{
    [IgnoreDataMember] public object? Value { get; init; }
    [IgnoreDataMember] public string? FullJson { get; init; }
    [DataMember(Name = "truncated")] public bool Truncated { get; init; }
    [DataMember(Name = "retainedCount")] public int RetainedCount { get; init; }
    [DataMember(Name = "omittedCount")] public int OmittedCount { get; init; }
    [DataMember(Name = "reasons")] public List<string> Reasons { get; init; } = [];
}
