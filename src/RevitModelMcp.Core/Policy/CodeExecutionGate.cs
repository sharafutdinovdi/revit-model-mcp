using RevitModelMcp.Core.Updates;

namespace RevitModelMcp.Core.Policy;

/// <summary>Where the effective C# code execution state comes from.</summary>
public enum CodeExecutionSource
{
    /// <summary>Not restricted: the confirmation flow of the tool applies.</summary>
    Default,

    /// <summary>The machine policy ExecuteCode value decided.</summary>
    Policy,

    /// <summary>The user set executeCode=confirm in settings.json (store channel only).</summary>
    UserSetting,

    /// <summary>The store channel keeps code execution off until the user or an administrator enables it.</summary>
    ChannelDefault
}

/// <summary>The effective state of revit_execute_code and process_models code steps.</summary>
public sealed record CodeExecutionState(bool Enabled, CodeExecutionSource Source)
{
    /// <summary>The value shown in ping: "confirm" (enabled, every run still needs a confirmation) or "off".</summary>
    public string Value => Enabled ? "confirm" : "off";
}

/// <summary>
/// Decides whether C# code may run. Machine policy always wins; without it the store channel defaults to off
/// and only the user's settings.json executeCode=confirm turns it on. Other channels are on unless policy says off.
/// </summary>
public static class CodeExecutionGate
{
    public const string SettingsPath = @"%LOCALAPPDATA%\RevitModelMcp\settings.json";

    public const string StoreRefusal =
        "Running C# code is off by default in the Autodesk App Store build. To enable it, set \"executeCode\": \"confirm\" in "
        + SettingsPath + " and retry, or ask your administrator to set the ExecuteCode policy to confirm.";

    /// <summary>True when the store channel needs the user setting, so the caller reads settings.json only when it matters.</summary>
    public static bool NeedsUserSetting(UpdateChannel channel, MachinePolicy policy) =>
        channel == UpdateChannel.Store && policy.ExecuteCode is null && !policy.Unreadable;

    public static CodeExecutionState Evaluate(UpdateChannel channel, MachinePolicy policy, string? userSetting)
    {
        if (policy.Unreadable) return new(false, CodeExecutionSource.Policy);
        if (policy.ExecuteCode is not null)
            return new(!policy.CodeExecutionOff, CodeExecutionSource.Policy);
        if (channel != UpdateChannel.Store) return new(true, CodeExecutionSource.Default);
        return string.Equals(userSetting?.Trim(), "confirm", StringComparison.OrdinalIgnoreCase)
            ? new(true, CodeExecutionSource.UserSetting)
            : new(false, CodeExecutionSource.ChannelDefault);
    }

    /// <summary>The refusal for a code command, or null. Policy refusals keep the policy text; the store default tells how to enable code.</summary>
    public static string? Refusal(string? command, UpdateChannel channel, MachinePolicy policy, string? userSetting)
    {
        if (!MachinePolicy.IsCodeCommand(command)) return null;
        var state = Evaluate(channel, policy, userSetting);
        if (state.Enabled) return null;
        return state.Source == CodeExecutionSource.ChannelDefault ? StoreRefusal : policy.CodeStepRefusal();
    }

    /// <summary>The ping policy summary with the effective code state. Outside the store channel the summary is unchanged.</summary>
    public static Dictionary<string, string> Summarize(UpdateChannel channel, MachinePolicy policy, string? userSetting)
    {
        var summary = policy.ToSummary();
        if (channel != UpdateChannel.Store) return summary;
        var state = Evaluate(channel, policy, userSetting);
        summary["updateChannel"] = "store";
        summary["executeCode"] = state.Value;
        summary["executeCodeSource"] = state.Source switch
        {
            CodeExecutionSource.Policy => "policy",
            CodeExecutionSource.UserSetting => "settings",
            _ => "store-default"
        };
        return summary;
    }
}
