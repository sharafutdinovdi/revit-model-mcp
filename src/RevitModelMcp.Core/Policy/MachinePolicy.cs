using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;

namespace RevitModelMcp.Core.Policy;

/// <summary>
/// Machine-wide administrator policy. Every value is optional: null means the administrator did not set it.
/// Sources are merged with <see cref="Merge"/>: the registry policy wins over policy.json, and the whole
/// result wins over user settings.
/// </summary>
public sealed class MachinePolicy
{
    public const string RefusalPrefix = "Blocked by organization policy:";
    public const string RegistryKey = @"Software\Policies\RevitModelMcp";

    public static readonly MachinePolicy Empty = new();

    private static readonly string[] CodeCommands = ["execute-code"];
    private static readonly string[] ExemptCommands = ["ping", "jobs", "jobs/persisted"];

    public bool? ReadOnly { get; init; }
    public IReadOnlyList<string>? ToolDeny { get; init; }
    public IReadOnlyList<string>? ToolAllow { get; init; }

    /// <summary>"off" or "confirm". Null means unset (confirm).</summary>
    public string? ExecuteCode { get; init; }

    /// <summary>Lowercased as written: "off", "notify" or "auto" are valid, any other text is kept so the consumer can fail closed. Null means unset.</summary>
    public string? UpdateMode { get; init; }

    public IReadOnlyList<string>? TrustedNetworkRoots { get; init; }
    public string? LogDir { get; init; }
    public int? LogRetentionDays { get; init; }
    public bool? RedactPaths { get; init; }
    public bool? HttpEnabled { get; init; }

    public bool IsReadOnly => ReadOnly == true;
    public bool CodeExecutionOff => string.Equals(ExecuteCode, "off", StringComparison.OrdinalIgnoreCase);
    public bool IsEmpty => this == Empty || (ReadOnly is null && ToolDeny is null && ToolAllow is null && ExecuteCode is null
        && UpdateMode is null && TrustedNetworkRoots is null && LogDir is null && LogRetentionDays is null
        && RedactPaths is null && HttpEnabled is null);

    /// <summary>Builds a policy from raw registry or file values. Unknown names and malformed values are ignored.</summary>
    public static MachinePolicy Parse(IReadOnlyDictionary<string, object> values)
    {
        object? Get(string name)
        {
            foreach (var pair in values)
                if (string.Equals(pair.Key, name, StringComparison.OrdinalIgnoreCase)) return pair.Value;
            return null;
        }
        var executeCode = Text(Get("ExecuteCode"))?.ToLowerInvariant();
        var updateMode = Text(Get("UpdateMode"))?.ToLowerInvariant();
        var retention = Number(Get("LogRetentionDays"));
        return new MachinePolicy
        {
            ReadOnly = Flag(Get("ReadOnly")),
            ToolDeny = List(Get("ToolDeny")),
            ToolAllow = List(Get("ToolAllow")),
            ExecuteCode = executeCode is "off" or "confirm" ? executeCode : null,
            UpdateMode = updateMode,
            TrustedNetworkRoots = List(Get("TrustedNetworkRoots")),
            LogDir = Text(Get("LogDir")),
            LogRetentionDays = retention is >= 1 and <= 3650 ? retention : null,
            RedactPaths = Flag(Get("RedactPaths")),
            HttpEnabled = Flag(Get("HttpEnabled"))
        };
    }

    /// <summary>Parses the policy.json file text (PascalCase names, same as the registry values). Invalid JSON yields an empty policy.</summary>
    public static MachinePolicy ParseJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return Empty;
        try
        {
            using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
            var serializer = new DataContractJsonSerializer(typeof(PolicyFile), new DataContractJsonSerializerSettings
            {
                UseSimpleDictionaryFormat = true
            });
            var file = (PolicyFile?)serializer.ReadObject(stream);
            if (file is null) return Empty;
            var values = new Dictionary<string, object>();
            if (file.ReadOnly is { } readOnly) values["ReadOnly"] = readOnly;
            if (file.ToolDeny is not null) values["ToolDeny"] = file.ToolDeny;
            if (file.ToolAllow is not null) values["ToolAllow"] = file.ToolAllow;
            if (file.ExecuteCode is not null) values["ExecuteCode"] = file.ExecuteCode;
            if (file.UpdateMode is not null) values["UpdateMode"] = file.UpdateMode;
            if (file.TrustedNetworkRoots is not null) values["TrustedNetworkRoots"] = file.TrustedNetworkRoots;
            if (file.LogDir is not null) values["LogDir"] = file.LogDir;
            if (file.LogRetentionDays is { } days) values["LogRetentionDays"] = days;
            if (file.RedactPaths is { } redact) values["RedactPaths"] = redact;
            if (file.HttpEnabled is { } http) values["HttpEnabled"] = http;
            return Parse(values);
        }
        catch (Exception exception) when (exception is SerializationException or InvalidCastException or ArgumentException or FormatException)
        {
            return Empty;
        }
    }

    /// <summary>Per value, the winner overrides the fallback. Used as registry over policy.json.</summary>
    public static MachinePolicy Merge(MachinePolicy winner, MachinePolicy fallback) => new()
    {
        ReadOnly = winner.ReadOnly ?? fallback.ReadOnly,
        ToolDeny = winner.ToolDeny ?? fallback.ToolDeny,
        ToolAllow = winner.ToolAllow ?? fallback.ToolAllow,
        ExecuteCode = winner.ExecuteCode ?? fallback.ExecuteCode,
        UpdateMode = winner.UpdateMode ?? fallback.UpdateMode,
        TrustedNetworkRoots = winner.TrustedNetworkRoots ?? fallback.TrustedNetworkRoots,
        LogDir = winner.LogDir ?? fallback.LogDir,
        LogRetentionDays = winner.LogRetentionDays ?? fallback.LogRetentionDays,
        RedactPaths = winner.RedactPaths ?? fallback.RedactPaths,
        HttpEnabled = winner.HttpEnabled ?? fallback.HttpEnabled
    };

    /// <summary>Normalizes an MCP tool name or a command name to the command form: revit_execute_code and execute-code both become execute-code.</summary>
    public static string NormalizeToolName(string name)
    {
        var value = name.Trim().ToLowerInvariant().Replace('_', '-');
        return value.StartsWith("revit-", StringComparison.Ordinal) ? value.Substring(6) : value;
    }

    /// <summary>Returns the refusal text when the policy blocks the command, otherwise null. ToolDeny wins over ToolAllow. ping and jobs are never blocked.</summary>
    public string? Refusal(string? command)
    {
        if (string.IsNullOrEmpty(command)) return null;
        var name = NormalizeToolName(command!);
        if (ExemptCommands.Contains(name, StringComparer.Ordinal)) return null;
        if (ToolDeny is not null && ToolDeny.Any(entry => NormalizeToolName(entry) == name))
            return $"{RefusalPrefix} the tool '{name}' is denied.";
        if (ToolAllow is { Count: > 0 } && ToolAllow.All(entry => NormalizeToolName(entry) != name))
            return $"{RefusalPrefix} the tool '{name}' is not in the allowed list.";
        if (CodeExecutionOff && CodeCommands.Contains(name, StringComparer.Ordinal))
            return $"{RefusalPrefix} running C# code is disabled.";
        return null;
    }

    /// <summary>The refusal for process-models requests that carry code steps while code execution is off.</summary>
    public string? CodeStepRefusal() =>
        CodeExecutionOff ? $"{RefusalPrefix} running C# code is disabled." : null;

    /// <summary>The refusal text for a read-only policy, or null when the policy does not enforce read-only.</summary>
    public string? ReadOnlyRefusal() =>
        IsReadOnly ? $"{RefusalPrefix} the workstation is read-only." : null;

    /// <summary>Values safe to show to clients: no paths, no secrets. Lists are comma-joined strings.</summary>
    public Dictionary<string, string> ToSummary()
    {
        var summary = new Dictionary<string, string>();
        if (ReadOnly is { } readOnly) summary["readOnly"] = Bool(readOnly);
        if (ToolDeny is { Count: > 0 }) summary["toolDeny"] = string.Join(",", ToolDeny.Select(NormalizeToolName));
        if (ToolAllow is { Count: > 0 }) summary["toolAllow"] = string.Join(",", ToolAllow.Select(NormalizeToolName));
        if (ExecuteCode is not null) summary["executeCode"] = ExecuteCode;
        if (UpdateMode is not null) summary["updateMode"] = UpdateMode;
        if (TrustedNetworkRoots is not null) summary["trustedNetworkRootCount"] = TrustedNetworkRoots.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (RedactPaths is { } redact) summary["redactPaths"] = Bool(redact);
        if (HttpEnabled is { } http) summary["httpEnabled"] = Bool(http);
        if (LogRetentionDays is { } days) summary["logRetentionDays"] = days.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return summary;
    }

    private static string Bool(bool value) => value ? "true" : "false";

    private static bool? Flag(object? value) => value switch
    {
        bool flag => flag,
        int number => number != 0,
        long number => number != 0,
        string text when text.Trim() is "1" or "true" or "True" => true,
        string text when text.Trim() is "0" or "false" or "False" => false,
        _ => null
    };

    private static int? Number(object? value) => value switch
    {
        int number => number,
        long number when number is >= int.MinValue and <= int.MaxValue => (int)number,
        string text when int.TryParse(text, out var number) => number,
        _ => null
    };

    private static string? Text(object? value) =>
        value is string text && !string.IsNullOrWhiteSpace(text) ? text.Trim() : null;

    private static IReadOnlyList<string>? List(object? value)
    {
        IEnumerable<string>? items = value switch
        {
            string[] array => array,
            IEnumerable<string> sequence => sequence,
            string text => [text],
            _ => null
        };
        return items?.Where(item => !string.IsNullOrWhiteSpace(item)).Select(item => item.Trim()).ToArray();
    }

    [DataContract]
    private sealed class PolicyFile
    {
        [DataMember(Name = "ReadOnly")] public bool? ReadOnly { get; set; }
        [DataMember(Name = "ToolDeny")] public string[]? ToolDeny { get; set; }
        [DataMember(Name = "ToolAllow")] public string[]? ToolAllow { get; set; }
        [DataMember(Name = "ExecuteCode")] public string? ExecuteCode { get; set; }
        [DataMember(Name = "UpdateMode")] public string? UpdateMode { get; set; }
        [DataMember(Name = "TrustedNetworkRoots")] public string[]? TrustedNetworkRoots { get; set; }
        [DataMember(Name = "LogDir")] public string? LogDir { get; set; }
        [DataMember(Name = "LogRetentionDays")] public int? LogRetentionDays { get; set; }
        [DataMember(Name = "RedactPaths")] public bool? RedactPaths { get; set; }
        [DataMember(Name = "HttpEnabled")] public bool? HttpEnabled { get; set; }
    }
}
