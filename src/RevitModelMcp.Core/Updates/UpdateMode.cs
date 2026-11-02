namespace RevitModelMcp.Core.Updates;

/// <summary>How the add-in handles a newer release. Higher values do more.</summary>
public enum UpdateMode
{
    Off = 0,
    Notify = 1,
    Auto = 2
}

/// <summary>Where the build gets its updates from. Fixed at build time.</summary>
public enum UpdateChannel
{
    Release,
    Managed,
    Store,
    Dev
}

/// <summary>Raw inputs for <see cref="UpdateDecision.Decide"/>. Null means the layer is absent.</summary>
public sealed record UpdateInputs(
    UpdateChannel Channel,
    bool PerUserInstall,
    string? PolicyMode = null,
    string? MachineSettingsMode = null,
    string? UserSettingsMode = null,
    bool? MachineUpdateCheck = null,
    bool? UserUpdateCheck = null,
    string? EnvironmentOptOut = null);

public static class UpdateDecision
{
    /// <summary>Parses a mode name. Empty means absent; an unknown value fails closed to Off.</summary>
    public static UpdateMode? ParseMode(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        return value!.Trim().ToLowerInvariant() switch
        {
            "notify" => UpdateMode.Notify,
            "auto" => UpdateMode.Auto,
            _ => UpdateMode.Off
        };
    }

    /// <summary>Resolves the channel from build metadata, the legacy ManagedInstall flag and the installed version.</summary>
    public static UpdateChannel ResolveChannel(string? channelMetadata, bool managedInstall, string installedVersion)
    {
        var channel = string.IsNullOrWhiteSpace(channelMetadata)
            ? UpdateChannel.Release
            : channelMetadata!.Trim().ToLowerInvariant() switch
            {
                "release" or "github" => UpdateChannel.Release,
                "dev" => UpdateChannel.Dev,
                "store" => UpdateChannel.Store,
                _ => UpdateChannel.Managed
            };
        if (channel == UpdateChannel.Release && managedInstall) return UpdateChannel.Managed;
        if (channel == UpdateChannel.Release && installedVersion.Contains("-dev", StringComparison.OrdinalIgnoreCase))
            return UpdateChannel.Dev;
        return channel;
    }

    /// <summary>
    /// Precedence: store and managed channels are always Off. Otherwise machine policy wins and is final.
    /// Without policy the installer or settings mode applies (machine settings cap user settings), then the
    /// channel default (Notify for release, Off for dev). The opt-outs (environment, updateCheck=false) and
    /// the per-machine and dev caps can only lower the result. Unknown values fail closed to Off.
    /// </summary>
    public static UpdateMode Decide(UpdateInputs inputs)
    {
        if (inputs.Channel is UpdateChannel.Store or UpdateChannel.Managed) return UpdateMode.Off;
        var mode = ParseMode(inputs.PolicyMode);
        if (mode is null)
        {
            var machine = ParseMode(inputs.MachineSettingsMode);
            var user = ParseMode(inputs.UserSettingsMode);
            mode = machine is not null && user is not null ? (UpdateMode)Math.Min((int)machine, (int)user) :
                machine ?? user ?? (inputs.Channel == UpdateChannel.Dev ? UpdateMode.Off : UpdateMode.Notify);
        }
        if (inputs.EnvironmentOptOut == "1" || inputs.MachineUpdateCheck == false || inputs.UserUpdateCheck == false)
            return UpdateMode.Off;
        if (!inputs.PerUserInstall || inputs.Channel == UpdateChannel.Dev)
            mode = (UpdateMode)Math.Min((int)mode.Value, (int)UpdateMode.Notify);
        return mode.Value;
    }
}
