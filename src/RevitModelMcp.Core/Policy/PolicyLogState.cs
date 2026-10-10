namespace RevitModelMcp.Core.Policy;

/// <summary>
/// Decides which policy warnings are new, so each distinct problem is logged once. It only returns text: the caller logs it
/// after releasing its own locks, because the logger itself reads the policy.
/// </summary>
public sealed class PolicyLogState
{
    private string? _problem;
    private string? _ignored;

    public IReadOnlyList<string> NewMessages(MachinePolicy policy)
    {
        var messages = new List<string>();
        var ignored = policy.IgnoredKeys is null ? null : string.Join(",", policy.IgnoredKeys);
        if (ignored != _ignored)
        {
            _ignored = ignored;
            if (ignored is not null) messages.Add($"Organization policy has settings this version does not know and ignores: {ignored}.");
        }
        if (!policy.Unreadable)
        {
            _problem = null;
        }
        else if (_problem != policy.Problem)
        {
            _problem = policy.Problem;
            messages.Add($"Organization policy could not be read, so the add-in is read-only, code execution is off and updates are off: {policy.Problem}.");
        }
        return messages;
    }
}
