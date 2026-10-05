namespace RevitModelMcp.Core.Control;

public static class ViewTemplateRefusal
{
    public static string Message(string template, IReadOnlyList<string> mismatchedViews) =>
        $"Template '{template}' was not applied: it does not match the type of {string.Join(", ", mismatchedViews.Select(view => $"'{view}'"))}.";
}
