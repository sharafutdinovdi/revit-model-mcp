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

    public static object? Limit(object? value, Func<object, object?> convertSpecial)
    {
        var itemCount = 0;
        return Visit(value, convertSpecial, 0, ref itemCount);
    }

    private static object? Visit(object? value, Func<object, object?> convertSpecial, int depth, ref int itemCount)
    {
        if (value is null) return null;
        if (value is string or bool or byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal)
            return value;
        if (depth >= 6) return value.ToString();
        var converted = convertSpecial(value);
        if (!ReferenceEquals(converted, value))
            return Visit(converted, convertSpecial, depth + 1, ref itemCount);
        if (value is IDictionary dictionary)
        {
            var result = new Dictionary<string, object?>();
            foreach (DictionaryEntry entry in dictionary)
            {
                if (itemCount++ >= 5000) break;
                result[FormatKey(entry.Key)] = Visit(entry.Value, convertSpecial, depth + 1, ref itemCount);
            }
            return result;
        }
        if (value is IEnumerable sequence)
        {
            var result = new List<object?>();
            foreach (var item in sequence)
            {
                if (itemCount++ >= 5000) break;
                result.Add(Visit(item, convertSpecial, depth + 1, ref itemCount));
            }
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
            {
                if (itemCount++ >= 5000) break;
                try
                {
                    result[property.Name] = Visit(property.GetValue(value), convertSpecial, depth + 1, ref itemCount);
                }
                catch (TargetInvocationException)
                {
                    result.Remove(property.Name);
                }
            }
            if (result.Count > 0) return result;
        }
        return value.ToString();
    }

    private static string FormatKey(object? key) => key is IFormattable formattable
        ? formattable.ToString(null, CultureInfo.InvariantCulture) ?? "null"
        : key?.ToString() ?? "null";
}
