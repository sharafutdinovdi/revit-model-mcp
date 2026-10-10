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

    /// <summary>The refusal for every request the fail-closed state blocks. It names the problem without leaking the file contents.</summary>
    public const string UnreadableRefusal = RefusalPrefix + " the policy file could not be read.";

    private static readonly string[] KnownNames =
    [
        "ReadOnly", "ToolDeny", "ToolAllow", "ExecuteCode", "UpdateMode", "TrustedNetworkRoots",
        "LogDir", "LogRetentionDays", "RedactPaths", "HttpEnabled"
    ];

    /// <summary>Tool names whose add-in command differs: the name an administrator reads in the tool list resolves to the command the server sends.</summary>
    private static readonly Dictionary<string, string> CommandAliases = new(StringComparer.Ordinal)
    {
        ["run-actions"] = "batch",
        ["batch-start"] = "batch-supervisor-start"
    };

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

    /// <summary>True when the policy could not be read or understood. The values above then describe the fail-closed state.</summary>
    public bool Unreadable { get; init; }

    /// <summary>What went wrong, for the log only. It can name settings, so it is never sent to clients.</summary>
    public string? Problem { get; init; }

    public bool IsReadOnly => ReadOnly == true;
    public bool CodeExecutionOff => string.Equals(ExecuteCode, "off", StringComparison.OrdinalIgnoreCase);
    public bool IsEmpty => this == Empty || (ReadOnly is null && ToolDeny is null && ToolAllow is null && ExecuteCode is null
        && UpdateMode is null && TrustedNetworkRoots is null && LogDir is null && LogRetentionDays is null
        && RedactPaths is null && HttpEnabled is null && !Unreadable);

    /// <summary>
    /// Builds a policy from raw registry or file values. Names match case-insensitively (camelCase and PascalCase both work),
    /// a single string is accepted for the list values, and anything else that is wrong (unknown name, wrong type, unknown
    /// enum value, out-of-range number, duplicate name) yields <see cref="FailClosed"/> instead of a partial policy.
    /// </summary>
    public static MachinePolicy Parse(IReadOnlyDictionary<string, object> values)
    {
        var problem = TryParse(values, out var policy);
        return problem is null ? policy : FailClosed(problem);
    }

    /// <summary>The state used when the policy cannot be trusted: read-only, no C# code, no updates, no HTTP listener, with a refusal that names the problem.</summary>
    public static MachinePolicy FailClosed(string problem) => new()
    {
        Unreadable = true,
        Problem = problem,
        ReadOnly = true,
        ExecuteCode = "off",
        UpdateMode = "off",
        HttpEnabled = false
    };

    private static string? TryParse(IReadOnlyDictionary<string, object> values, out MachinePolicy policy)
    {
        policy = Empty;
        var seen = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in values)
        {
            if (string.IsNullOrWhiteSpace(pair.Key)) continue;
            var known = KnownNames.FirstOrDefault(name => string.Equals(name, pair.Key.Trim(), StringComparison.OrdinalIgnoreCase));
            if (known is null) return $"unknown setting '{pair.Key}'";
            if (pair.Value is null) return $"{known} has no value";
            if (seen.ContainsKey(known)) return $"{known} is set more than once";
            seen[known] = pair.Value;
        }

        bool? readOnly = null, redactPaths = null, httpEnabled = null;
        IReadOnlyList<string>? toolDeny = null, toolAllow = null, trustedRoots = null;
        string? executeCode = null, updateMode = null, logDir = null;
        int? retention = null;
        foreach (var pair in seen)
        {
            string? problem = pair.Key switch
            {
                "ReadOnly" => Flag(pair.Value, out readOnly),
                "RedactPaths" => Flag(pair.Value, out redactPaths),
                "HttpEnabled" => Flag(pair.Value, out httpEnabled),
                "ToolDeny" => List(pair.Value, out toolDeny),
                "ToolAllow" => List(pair.Value, out toolAllow),
                "TrustedNetworkRoots" => List(pair.Value, out trustedRoots),
                "ExecuteCode" => Choice(pair.Value, ["off", "confirm"], out executeCode),
                "UpdateMode" => Choice(pair.Value, ["off", "notify", "auto"], out updateMode),
                "LogDir" => Text(pair.Value, out logDir),
                "LogRetentionDays" => Retention(pair.Value, out retention),
                _ => $"unknown setting '{pair.Key}'"
            };
            if (problem is not null) return $"{pair.Key}: {problem}";
        }

        policy = new MachinePolicy
        {
            ReadOnly = readOnly,
            ToolDeny = toolDeny,
            ToolAllow = toolAllow,
            ExecuteCode = executeCode,
            UpdateMode = updateMode,
            TrustedNetworkRoots = trustedRoots,
            LogDir = logDir,
            LogRetentionDays = retention,
            RedactPaths = redactPaths,
            HttpEnabled = httpEnabled
        };
        return null;
    }

    /// <summary>
    /// Parses the policy.json file text. Names are case-insensitive, so the camelCase names shown by ping work too.
    /// Empty text, invalid JSON, a non-object root or any invalid value yields <see cref="FailClosed"/>.
    /// </summary>
    public static MachinePolicy ParseJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return FailClosed("the policy file is empty");
        var text = json!.TrimStart('\uFEFF').Trim();
        if (!text.StartsWith("{", StringComparison.Ordinal)) return FailClosed("the policy file is not a JSON object");
        try
        {
            using var stream = new MemoryStream(Encoding.UTF8.GetBytes(text));
            var serializer = new DataContractJsonSerializer(typeof(Dictionary<string, object>), new DataContractJsonSerializerSettings
            {
                UseSimpleDictionaryFormat = true
            });
            if (serializer.ReadObject(stream) is not Dictionary<string, object> values) return FailClosed("the policy file is not a JSON object");
            return Parse(values);
        }
        catch (Exception exception)
        {
            return FailClosed($"the policy file is not valid JSON ({exception.GetType().Name})");
        }
    }

    /// <summary>Per value, the winner overrides the fallback. Used as registry over policy.json.</summary>
    public static MachinePolicy Merge(MachinePolicy winner, MachinePolicy fallback) =>
        winner.Unreadable || fallback.Unreadable
            ? FailClosed(string.Join("; ", new[] { winner.Problem, fallback.Problem }.Where(text => text is not null)))
            : new()
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

    /// <summary>Normalizes an MCP tool name or a command name to the command form: revit_execute_code and execute-code both become execute-code; revit_run_actions becomes batch and revit_batch_start becomes batch-supervisor-start.</summary>
    public static string NormalizeToolName(string name)
    {
        var value = name.Trim().ToLowerInvariant().Replace('_', '-');
        if (value.StartsWith("revit-", StringComparison.Ordinal)) value = value.Substring(6);
        return CommandAliases.TryGetValue(value, out var command) ? command : value;
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
            return CodeStepRefusal();
        return null;
    }

    /// <summary>The refusal for process-models requests that carry code steps while code execution is off.</summary>
    public string? CodeStepRefusal() =>
        Unreadable ? UnreadableRefusal : CodeExecutionOff ? $"{RefusalPrefix} running C# code is disabled." : null;

    /// <summary>The refusal text for a read-only policy, or null when the policy does not enforce read-only.</summary>
    public string? ReadOnlyRefusal() =>
        Unreadable ? UnreadableRefusal : IsReadOnly ? $"{RefusalPrefix} the workstation is read-only." : null;

    /// <summary>The HTTP listener switch after the policy has the last word: a policy value beats the settings file and the REVIT_MCP_HTTP_ENABLED variable.</summary>
    public bool EffectiveHttpEnabled(bool requested) => HttpEnabled ?? requested;

    /// <summary>Values safe to show to clients: no paths, no secrets. Lists are comma-joined strings.</summary>
    public Dictionary<string, string> ToSummary()
    {
        var summary = new Dictionary<string, string>();
        if (Unreadable) summary["policyUnreadable"] = "true";
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

    private static string? Flag(object value, out bool? result)
    {
        result = value switch
        {
            bool flag => flag,
            int number when number is 0 or 1 => number == 1,
            long number when number is 0 or 1 => number == 1,
            string text when text.Trim().Equals("1", StringComparison.Ordinal) || text.Trim().Equals("true", StringComparison.OrdinalIgnoreCase) => true,
            string text when text.Trim().Equals("0", StringComparison.Ordinal) || text.Trim().Equals("false", StringComparison.OrdinalIgnoreCase) => false,
            _ => null
        };
        return result is null ? "expected true/false or 1/0" : null;
    }

    private static string? Retention(object value, out int? result)
    {
        result = value switch
        {
            int number => number,
            long number when number is >= int.MinValue and <= int.MaxValue => (int)number,
            decimal number when number == Math.Truncate(number) && number is >= int.MinValue and <= int.MaxValue => (int)number,
            double number when number == Math.Truncate(number) && number is >= int.MinValue and <= int.MaxValue => (int)number,
            string text when int.TryParse(text.Trim(), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var number) => number,
            _ => null
        };
        if (result is null) return "expected a whole number";
        if (result is < 1 or > 3650) { result = null; return "expected a number from 1 to 3650"; }
        return null;
    }

    /// <summary>A blank text means the administrator left the value unset.</summary>
    private static string? Text(object value, out string? result)
    {
        result = null;
        if (value is not string text) return "expected text";
        result = string.IsNullOrWhiteSpace(text) ? null : text.Trim();
        return null;
    }

    private static string? Choice(object value, string[] allowed, out string? result)
    {
        var problem = Text(value, out result);
        if (problem is not null || result is null) return problem;
        result = result.ToLowerInvariant();
        if (allowed.Contains(result, StringComparer.Ordinal)) return null;
        var rejected = result;
        result = null;
        return $"'{rejected}' is not one of {string.Join(", ", allowed)}";
    }

    private static string? List(object value, out IReadOnlyList<string>? result)
    {
        result = null;
        IEnumerable<object?>? items = value switch
        {
            string text => [text],
            string[] array => array,
            IEnumerable<object?> sequence => sequence,
            _ => null
        };
        if (items is null) return "expected text or a list of text";
        var list = new List<string>();
        foreach (var item in items)
        {
            if (item is not string text) return "every list entry must be text";
            if (!string.IsNullOrWhiteSpace(text)) list.Add(text.Trim());
        }
        result = list.ToArray();
        return null;
    }
}
