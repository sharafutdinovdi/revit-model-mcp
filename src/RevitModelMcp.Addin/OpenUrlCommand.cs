using System.Diagnostics;
using Autodesk.Revit.Attributes;
using JetBrains.Annotations;
using Nice3point.Revit.Toolkit.External;
using RevitModelMcp.Core.Help;

namespace RevitModelMcp;

public abstract class OpenUrlCommand : ExternalCommand
{
    protected abstract string Url { get; }

    public override void Execute() => Process.Start(new ProcessStartInfo(Url) { UseShellExecute = true });
}

[Transaction(TransactionMode.Manual)]
[UsedImplicitly]
public sealed class HelpCommand : OpenUrlCommand
{
    protected override string Url => HelpLinks.Documentation;
}

[Transaction(TransactionMode.Manual)]
[UsedImplicitly]
public sealed class ReportIssueCommand : OpenUrlCommand
{
    protected override string Url => HelpLinks.Issues;
}

[Transaction(TransactionMode.Manual)]
[UsedImplicitly]
public sealed class SupportDeveloperCommand : OpenUrlCommand
{
    protected override string Url => HelpLinks.Support;
}
