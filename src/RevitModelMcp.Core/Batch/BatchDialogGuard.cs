using System.Text.RegularExpressions;

namespace RevitModelMcp.Core.Batch;

public static class BatchDialogGuard
{
    private static readonly Regex PathPattern = new(
        @"(?:[A-Za-z]:[\\/]|\\\\)(?:[^\r\n\\/:*?""<>|]+[\\/])*[^\r\n\\/:*?""<>|]+?\.[A-Za-z0-9]{1,10}\b",
        RegexOptions.None, TimeSpan.FromMilliseconds(200));

    public static void Run(Action handler, Action<Exception> reportFailure)
    {
        if (handler is null) throw new ArgumentNullException(nameof(handler));
        if (reportFailure is null) throw new ArgumentNullException(nameof(reportFailure));
        try
        {
            handler();
        }
        catch (Exception exception)
        {
            try { reportFailure(exception); }
            catch (Exception) { /* Reporting must not escape the Revit event callback. */ }
        }
    }

    public static string RedactPaths(string description)
    {
        if (description is null) throw new ArgumentNullException(nameof(description));
        return PathPattern.Replace(description, match =>
            match.Value[(Math.Max(match.Value.LastIndexOf('/'), match.Value.LastIndexOf('\\')) + 1)..]);
    }
}
