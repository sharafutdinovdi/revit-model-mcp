namespace RevitModelMcp.Core.Batch;

public sealed record BatchDialogDecision(bool Allowed, int? OverrideResult, bool Recycle);

public static class BatchDialogPolicy
{
    private static readonly IReadOnlyDictionary<(string Id, string Type), int> SafeChoices =
        new Dictionary<(string, string), int>();

    public static BatchDialogDecision Decide(string? dialogId, string? runtimeType,
        IReadOnlyDictionary<(string Id, string Type), int>? allowlist = null) =>
        dialogId is not null && runtimeType is not null &&
        (allowlist ?? SafeChoices).TryGetValue((dialogId, runtimeType), out var choice)
            ? new(true, choice, false) : new(false, null, true);
}
