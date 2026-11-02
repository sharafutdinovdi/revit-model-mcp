using System.Collections.Concurrent;
using System.Globalization;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using RevitModelMcp.Core.Models;

namespace RevitModelMcp.Core.Control;

public sealed record ApiLookupRequest(string Query, string Kind = "search", int Limit = 25,
    bool IncludeInherited = false, bool IncludeObsolete = true);

public static class ApiLookup
{
    private static readonly ConcurrentDictionary<Assembly, Lazy<Dictionary<string, string>?>> Documentation = new();

    public static ApiLookupResult Lookup(IReadOnlyList<Assembly> assemblies, ApiLookupRequest request,
        Func<Assembly, string?> xmlDocPathResolver)
    {
        if (assemblies is null) throw new ArgumentNullException(nameof(assemblies));
        if (request is null) throw new ArgumentNullException(nameof(request));
        if (xmlDocPathResolver is null) throw new ArgumentNullException(nameof(xmlDocPathResolver));
        if (string.IsNullOrWhiteSpace(request.Query) || request.Query.Length > 200)
            throw new ArgumentException("query must contain 1 to 200 characters.", nameof(request));
        if (request.Kind is not ("type" or "member" or "search"))
            throw new ArgumentException("kind must be type, member, or search.", nameof(request));
        if (request.Limit is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(request));
        var result = new ApiLookupResult();
        var types = new List<Type>();
        var summaries = new Dictionary<Assembly, Dictionary<string, string>?>();
        foreach (var assembly in assemblies.Distinct())
        {
            var identity = assembly.GetName();
            result.AssemblyVersions.Add(new ApiAssemblyVersion { Name = identity.Name ?? "", Version = identity.Version?.ToString() ?? "" });
            try { types.AddRange(assembly.GetTypes().Where(type => type.IsVisible)); }
            catch (ReflectionTypeLoadException exception)
            {
                types.AddRange(exception.Types.OfType<Type>().Where(type => type.IsVisible));
                foreach (var failed in exception.Types.Where(type => type is null))
                    result.Skipped.Add(new SkippedRead { What = identity.Name ?? "Assembly", Reason = "A type could not be loaded." });
            }
            try
            {
                summaries[assembly] = Documentation.GetOrAdd(assembly,
                    item => new Lazy<Dictionary<string, string>?>(() => ReadDocumentation(xmlDocPathResolver(item)))).Value;
                if (summaries[assembly] is null)
                    result.Skipped.Add(new SkippedRead { What = identity.Name ?? "Assembly", Reason = $"XML documentation not found next to {identity.Name}" });
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Xml.XmlException)
            {
                result.Skipped.Add(new SkippedRead { What = identity.Name ?? "Assembly", Reason = "XML documentation could not be read." });
            }
        }
        types = types.Distinct().OrderBy(type => type.FullName, StringComparer.Ordinal).ToList();
        var query = request.Query.Trim();
        var matched = Resolve(types, query);
        string? memberName = null;
        if (matched.Count == 0 && request.Kind != "type" && query.LastIndexOf('.') is var separator && separator > 0)
        {
            matched = Resolve(types, query[..separator]);
            if (matched.Count > 0) memberName = query[(separator + 1)..];
        }
        if (matched.Count > 1)
        {
            result.Candidates.AddRange(matched.Take(20).Select(type => type.FullName!));
            result.Truncated = matched.Count > 20;
        }
        else
        {
            var flags = BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static;
            flags |= request.IncludeInherited ? BindingFlags.FlattenHierarchy : BindingFlags.DeclaredOnly;
            var count = 0;
            foreach (var type in matched.Count == 1 ? matched : types)
            {
                if (!request.IncludeObsolete && Obsolete(type).Present) continue;
                var typeMatches = matched.Count == 1 || Contains(type.FullName!, query);
                if (request.Kind != "member" && typeMatches)
                {
                    if (count++ < request.Limit)
                    {
                        var obsolete = Obsolete(type);
                        result.Types.Add(new ApiTypeInfo
                        {
                            FullName = type.FullName!,
                            Kind = type.IsEnum ? "enum" : type.IsInterface ? "interface" : type.IsValueType ? "struct" : "class",
                            BaseType = type.BaseType?.FullName,
                            IsObsolete = obsolete.Present,
                            ObsoleteMessage = obsolete.Message,
                            Summary = Summary(type)
                        });
                    }
                }
                if (request.Kind == "type") continue;
                foreach (var member in type.GetMembers(flags).Where(IsSupported).OrderBy(member => member.Name, StringComparer.Ordinal).ThenBy(member => member.ToString(), StringComparer.Ordinal))
                {
                    if (memberName is not null ? !Contains(member.Name, memberName) : !typeMatches && !Contains(member.Name, query)) continue;
                    var obsolete = Obsolete(member);
                    if (!request.IncludeObsolete && obsolete.Present) continue;
                    if (count++ >= request.Limit) continue;
                    var parameters = member is MethodBase method ? method.GetParameters() : member is PropertyInfo property ? property.GetIndexParameters() : [];
                    var returnType = member switch { MethodInfo methodInfo => FormatType(methodInfo.ReturnType), PropertyInfo propertyInfo => FormatType(propertyInfo.PropertyType), FieldInfo field => FormatType(field.FieldType), EventInfo eventInfo => FormatType(eventInfo.EventHandlerType!), _ => null };
                    var isStatic = member switch { MethodBase methodBase => methodBase.IsStatic, PropertyInfo propertyInfo => (propertyInfo.GetMethod ?? propertyInfo.SetMethod)!.IsStatic, FieldInfo field => field.IsStatic, EventInfo eventInfo => eventInfo.AddMethod!.IsStatic, _ => false };
                    var name = member is ConstructorInfo ? FormatType(type) : member.Name;
                    if (member is MethodInfo { IsGenericMethod: true } generic) name += "<" + string.Join(", ", generic.GetGenericArguments().Select(FormatType)) + ">";
                    var signature = "public " + (isStatic ? "static " : "") + (returnType is null ? "" : returnType + " ") + name;
                    if (member is MethodBase) signature += "(" + string.Join(", ", parameters.Select(parameter => FormatParameter(parameter))) + ")";
                    if (member is PropertyInfo propertyMember)
                        signature += (parameters.Length > 0 ? "[" + string.Join(", ", parameters.Select(FormatParameter)) + "]" : "") + " { " + (propertyMember.GetMethod?.IsPublic == true ? "get; " : "") + (propertyMember.SetMethod?.IsPublic == true ? "set; " : "") + "}";
                    result.Members.Add(new ApiMemberInfo
                    {
                        DeclaringType = member.DeclaringType!.FullName!,
                        Name = member.Name,
                        MemberKind = member switch { ConstructorInfo => "constructor", MethodInfo => "method", PropertyInfo => "property", EventInfo => "event", FieldInfo when type.IsEnum => "enumValue", _ => "field" },
                        Signature = signature,
                        Parameters = parameters.Select(parameter => new ApiParameterInfo { Name = parameter.Name ?? "", Type = FormatType(parameter.ParameterType), IsOptional = parameter.IsOptional }).ToList(),
                        ReturnType = returnType,
                        IsStatic = isStatic,
                        IsObsolete = obsolete.Present,
                        ObsoleteMessage = obsolete.Message,
                        Summary = Summary(member)
                    });
                }
            }
            result.Truncated = count > request.Limit;
        }
        result.SkippedCount = result.Skipped.Count;
        return result;

        string? Summary(MemberInfo member)
        {
            return summaries.TryGetValue(member.Module.Assembly, out var map) && map is not null && map.TryGetValue(DocumentationId(member), out var summary) ? summary : null;
        }
    }

    private static List<Type> Resolve(List<Type> types, string query)
    {
        var exact = types.Where(type => type.FullName == query).ToList();
        return exact.Count > 0 ? exact : types.Where(type => type.Namespace?.StartsWith("Autodesk.Revit.", StringComparison.Ordinal) == true && string.Equals(type.Name, query, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    private static bool Contains(string text, string query) => text.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0;
    private static bool IsSupported(MemberInfo member) => member is ConstructorInfo or PropertyInfo or EventInfo || member is MethodInfo { IsSpecialName: false } || member is FieldInfo { IsSpecialName: false };
    private static (bool Present, string? Message) Obsolete(MemberInfo member)
    {
        var attribute = member.GetCustomAttributesData().FirstOrDefault(item => item.AttributeType == typeof(ObsoleteAttribute));
        return (attribute is not null, attribute?.ConstructorArguments.FirstOrDefault().Value as string);
    }

    private static string FormatParameter(ParameterInfo parameter) =>
        (parameter.IsOut ? "out " : parameter.ParameterType.IsByRef ? "ref " : "") + FormatType(parameter.ParameterType) + " " + parameter.Name + (parameter.IsOptional ? " = " + FormatDefault(parameter) : "");

    private static string FormatDefault(ParameterInfo parameter)
    {
        var value = parameter.RawDefaultValue;
        if (value is null) return "null";
        if (value is string text) return "\"" + EscapeLiteral(text) + "\"";
        if (value is char character) return "'" + EscapeLiteral(character.ToString()).Replace("'", "\\'") + "'";
        if (value is bool boolean) return boolean ? "true" : "false";
        if (value is Missing or DBNull) return "default";
        var literal = Convert.ToString(value, CultureInfo.InvariantCulture) ?? "default";
        literal += value switch { float => "f", decimal => "m", uint => "U", long => "L", ulong => "UL", _ => "" };
        return parameter.ParameterType.IsEnum ? "(" + FormatType(parameter.ParameterType) + ")" + literal : literal;
    }

    private static string EscapeLiteral(string text) => text.Replace("\\", "\\\\").Replace("\"", "\\\"")
        .Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", "\\t").Replace("\0", "\\0");

    private static string FormatType(Type type)
    {
        if (type.IsByRef) return FormatType(type.GetElementType()!);
        if (type.IsArray) return FormatType(type.GetElementType()!) + "[" + new string(',', type.GetArrayRank() - 1) + "]";
        var alias = type.FullName switch { "System.Void" => "void", "System.String" => "string", "System.Boolean" => "bool", "System.Int32" => "int", "System.Int64" => "long", "System.Double" => "double", "System.Object" => "object", "System.Char" => "char", "System.Byte" => "byte", "System.SByte" => "sbyte", "System.Int16" => "short", "System.UInt16" => "ushort", "System.UInt32" => "uint", "System.UInt64" => "ulong", "System.Single" => "float", "System.Decimal" => "decimal", _ => null };
        if (alias is not null) return alias;
        return type.IsGenericType ? type.Name.Split('`')[0] + "<" + string.Join(", ", type.GetGenericArguments().Select(FormatType)) + ">" : type.Name;
    }

    private static string DocumentationId(MemberInfo member)
    {
        if (member is Type type) return "T:" + type.FullName!.Replace('+', '.');
        var prefix = member switch { MethodBase => "M:", PropertyInfo => "P:", FieldInfo => "F:", _ => "E:" };
        var name = member is ConstructorInfo ? "#ctor" : member.Name;
        if (member is MethodInfo { IsGenericMethod: true } generic) name += "``" + generic.GetGenericArguments().Length;
        var parameters = member is MethodBase method ? method.GetParameters() : member is PropertyInfo property ? property.GetIndexParameters() : [];
        return prefix + member.DeclaringType!.FullName!.Replace('+', '.') + "." + name +
            (parameters.Length == 0 ? "" : "(" + string.Join(",", parameters.Select(parameter => DocumentationType(parameter.ParameterType))) + ")") +
            (member is MethodInfo { Name: "op_Implicit" or "op_Explicit" } conversion ? "~" + DocumentationType(conversion.ReturnType) : "");
    }

    private static string DocumentationType(Type type)
    {
        if (type.IsByRef) return DocumentationType(type.GetElementType()!) + "@";
        if (type.IsArray) return DocumentationType(type.GetElementType()!) + (type.GetArrayRank() == 1 ? "[]" : "[" + string.Join(",", Enumerable.Repeat("0:", type.GetArrayRank())) + "]");
        if (type.IsGenericParameter) return (type.DeclaringMethod is null ? "`" : "``") + type.GenericParameterPosition;
        if (type.IsGenericType) return type.GetGenericTypeDefinition().FullName!.Split('`')[0].Replace('+', '.') + "{" + string.Join(",", type.GetGenericArguments().Select(DocumentationType)) + "}";
        return type.FullName!.Replace('+', '.');
    }

    private static string? ReferenceText(XElement element)
    {
        var reference = (string?)element.Attribute("cref");
        return reference?.Length > 2 && reference[1] == ':' ? reference[2..] : reference;
    }

    private static Dictionary<string, string>? ReadDocumentation(string? path)
    {
        if (path is null || !File.Exists(path)) return null;
        var result = new Dictionary<string, string>();
        foreach (var member in XDocument.Load(path).Descendants("member"))
        {
            var name = (string?)member.Attribute("name");
            var summary = member.Element("summary");
            if (name is null || summary is null) continue;
            var text = Regex.Replace(string.Concat(summary.DescendantNodes().Select(node => node is XText plain ? plain.Value : node is XElement element && element.Name.LocalName == "see" ? ReferenceText(element) : "")), @"\s+", " ").Trim();
            var sentence = Regex.Match(text, @"^.*?[.!?](?=\s|$)");
            if (sentence.Success) text = sentence.Value;
            result[name] = text.Length > 300 ? text[..300] : text;
        }
        return result;
    }
}
