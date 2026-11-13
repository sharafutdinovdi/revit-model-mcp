using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Microsoft.Win32;
using RevitModelMcp.Activity;
using RevitModelMcp.Core.Control;
using RevitModelMcp.Policy;
using TextBox = System.Windows.Controls.TextBox;

namespace RevitModelMcp.Control;

internal static class ToolShareDialogs
{
    internal static string Export(UIApplication application, Window owner, string name)
    {
        if (PolicyProvider.Refusal("get-tool") is string refusal) throw new InvalidOperationException(refusal);
        var definition = MyTools.Store.Get(name);
        var share = ToolShare.From(definition, MyTools.Presentation.Get(name, ToolArguments.Title(definition.Parameters)));
        var terms = new List<(string Kind, string Term)>
        {
            ("user or computer name", Environment.UserName), ("user or computer name", Environment.MachineName),
            ("user or computer name", application.Application.Username ?? "")
        };
        foreach (Document document in application.Application.Documents)
        {
            terms.Add(("model or project name", document.Title ?? ""));
            terms.Add(("model or project name", Path.GetFileNameWithoutExtension(document.PathName) ?? ""));
            if (!document.IsFamilyDocument && document.ProjectInformation is ProjectInfo information)
            {
                terms.Add(("model or project name", information.Name ?? ""));
                terms.Add(("model or project name", information.Number ?? ""));
            }
        }
        var findings = share.FindPrivateData(terms);
        if ((findings.TotalCount != 0 || findings.Incomplete) && !ConfirmExport(owner, findings)) return "Export canceled.";
        var picker = new SaveFileDialog
        {
            FileName = name + ".mcptool.json",
            Filter = "Model MCP tool (*.mcptool.json)|*.mcptool.json",
            OverwritePrompt = true
        };
        if (picker.ShowDialog(owner) != true) return "Export canceled.";
        ToolStore.AtomicWrite(picker.FileName, share.ToFileText());
        ActivityRecorder.RecordSystemNotice($"Exported saved tool {share.Label}.");
        return $"Exported {share.Label} ({share.Fingerprint()[..12]}). Arguments, history and approval data are not in the file.";
    }

    private static string FindingsText(ToolShare.PrivateData data)
    {
        var text = string.Join("\n", data.Findings.Take(20).Select(finding => $"{finding.Kind} ({finding.Field}): {finding.Excerpt}"));
        if (data.TotalCount != 0) text = "The tool text mentions:\n" + text;
        if (data.TotalCount > 20) text += $"\nand {data.TotalCount - 20} more";
        if (data.Incomplete) text += (text.Length == 0 ? "" : "\n\n") + "The text scan did not finish. Read the tool text yourself.";
        return text;
    }

    private static Window Dialog(Window owner, string title, double width = 850) => new()
    {
        Owner = owner,
        Title = title,
        Icon = AppDialog.LoadAppIcon(),
        Width = width,
        Height = 750,
        MinWidth = 460,
        MinHeight = 320,
        WindowStartupLocation = WindowStartupLocation.CenterOwner
    };

    private static void Show(Window dialog)
    {
        MyToolsWindow.FollowTheme(dialog);
        dialog.PreviewKeyDown += (_, args) => { if (args.Key == Key.Escape) dialog.Close(); };
        dialog.ShowDialog();
    }

    private static bool ConfirmExport(Window owner, ToolShare.PrivateData findings)
    {
        var dialog = Dialog(owner, "Review tool export", 650);
        var root = new DockPanel { Margin = new Thickness(16) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var export = new Button { Content = "Export anyway", Margin = new Thickness(0, 12, 8, 0), MinWidth = 110 };
        var cancel = new Button { Content = "Cancel", IsDefault = true, IsCancel = true, Margin = new Thickness(0, 12, 0, 0), MinWidth = 80 };
        export.Click += (_, _) => dialog.DialogResult = true;
        buttons.Children.Add(export);
        buttons.Children.Add(cancel);
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(buttons);
        root.Children.Add(new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                Text = "The file contains the tool name, label, description, mode, tested years, transaction mode, parameters, source and icons.\n\nIt never contains remembered arguments, presentation order or visibility, author, dates, revision history, approvals or the approval key.\n\n" +
                    FindingsText(findings) + "\n\nThis scan is a heuristic. Read the tool text before sharing it."
            }
        });
        dialog.Content = root;
        dialog.Loaded += (_, _) => cancel.Focus();
        Show(dialog);
        return dialog.DialogResult == true;
    }

    private static void ImportGate()
    {
        ActionCommandExecutor.ReadOnlyLock.Refresh();
        if (ActionCommandExecutor.ReadOnlyMode) throw new InvalidOperationException(PolicyProvider.ReadOnlyMessage);
        if (PolicyProvider.Refusal("save-tool") is string refusal) throw new InvalidOperationException(refusal);
    }

    private static ToolDefinition? Installed(string name)
    {
        try { return MyTools.Store.Get(name); }
        catch (InvalidOperationException) { return null; }
    }

    internal static (string? Name, string Status) Import(UIApplication application, Window owner)
    {
        ImportGate();
        var picker = new OpenFileDialog { Filter = "Model MCP tool (*.mcptool.json;*.json)|*.mcptool.json;*.json" };
        if (picker.ShowDialog(owner) != true) return (null, "Import canceled.");
        if (new FileInfo(picker.FileName).Length > ToolShare.MaxFileBytes) throw new ArgumentException("Tool file exceeds 4 MB.");
        var share = ToolShare.Parse(File.ReadAllBytes(picker.FileName));
        if (share.Icon16.Length != 0)
        {
            ToolIcons.Decode(share.Icon16, 16);
            ToolIcons.Decode(share.Icon32, 32);
        }
        var installed = Installed(share.Name);
        if (installed is not null && ToolShare.From(installed, MyTools.Presentation.Get(installed.Name, ToolArguments.Title(installed.Parameters))).Fingerprint() == share.Fingerprint())
            return (null, $"{share.Label} is already installed with the same content.");
        var dialog = Dialog(owner, "Review shared tool");
        var root = new DockPanel { Margin = new Thickness(16) };
        var footer = new StackPanel();
        var error = new TextBlock { TextWrapping = TextWrapping.Wrap };
        footer.Children.Add(error);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var import = new Button { Content = "Import", MinWidth = 80, Margin = new Thickness(0, 8, 8, 0) };
        var cancel = new Button { Content = "Cancel", MinWidth = 80, IsDefault = true, IsCancel = true, Margin = new Thickness(0, 8, 0, 0) };
        buttons.Children.Add(import);
        buttons.Children.Add(cancel);
        footer.Children.Add(buttons);
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(footer);
        var heading = new StackPanel();
        heading.Children.Add(new TextBlock { Text = "Tool name" });
        var nameInput = new TextBox { Text = share.Name, MaxLength = 48 };
        AutomationProperties.SetName(nameInput, "Tool name");
        heading.Children.Add(nameInput);
        var collision = new TextBlock { TextWrapping = TextWrapping.Wrap };
        heading.Children.Add(collision);
        string? reviewedRevision = null;
        void UpdateCollision()
        {
            try
            {
                ToolStore.ValidateName(nameInput.Text);
                var previous = Installed(nameInput.Text);
                reviewedRevision = previous?.Revision;
                collision.Text = previous is null ? "No installed tool has this name." : $"Importing replaces the active revision {previous.Revision[..12]}.";
            }
            catch (ArgumentException exception) { reviewedRevision = null; collision.Text = exception.Message; }
        }
        nameInput.TextChanged += (_, _) => UpdateCollision();
        UpdateCollision();
        var year = int.Parse(application.Application.VersionNumber, CultureInfo.InvariantCulture);
        var warning = share.TestedRevitYears.Contains(year) ? "" : $"\n\nWARNING: this tool has not been tested in Revit {year}.";
        var findings = share.FindPrivateData();
        heading.Children.Add(new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 8, 0, 8),
            Text = $"Label: {share.Label}\nDescription: {share.Description}\nMode: {share.Mode}\nTested Revit years: {(share.TestedRevitYears.Length == 0 ? "None" : string.Join(", ", share.TestedRevitYears))}\nTransaction mode: {share.Transaction}\nFingerprint: {share.Fingerprint()}{warning}\n\nImported C# runs with your Windows file, network and Revit API rights. Import only code you have read or that comes from a trusted author." +
                (findings.TotalCount == 0 && !findings.Incomplete ? "" : "\n\n" + FindingsText(findings))
        });
        var headerScroll = new ScrollViewer { Content = heading, MaxHeight = 330, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        DockPanel.SetDock(headerScroll, Dock.Top);
        root.Children.Add(headerScroll);
        var source = new TextBox
        {
            Text = $"Parameters: {share.Parameters}\n\n{share.Code}",
            IsReadOnly = true,
            AcceptsReturn = true,
            FontFamily = new System.Windows.Media.FontFamily("Consolas"),
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        };
        AutomationProperties.SetName(source, "Parameters and source");
        root.Children.Add(source);
        string? importedName = null;
        string? revision = null;
        import.Click += (_, _) =>
        {
            try
            {
                var name = nameInput.Text;
                ToolStore.ValidateName(name);
                ImportGate();
                var previous = Installed(name);
                if (previous?.Revision != reviewedRevision) throw new InvalidOperationException("The installed tool changed during review.");
                var definition = share.ToDefinition(name, "import", DateTimeOffset.UtcNow.ToString("O"), previous?.Revision ?? "");
                var job = ActionJobParser.Parse("save-tool", new ControlJobContract { Name = name, Code = definition.Code, Transaction = share.Transaction });
                var action = job.Action ?? throw new InvalidOperationException("Missing tool arguments.");
                action.Code = definition.Code;
                action.ToolRevision = definition.Revision;
                var compilation = CodeExecution.Execute(application, null, null, action,
                    new ActionCommandExecutor.ActionFailures(), "import", job.JobId, compileOnly: true);
                if (compilation.CodeError is not null)
                    throw new ArgumentException(compilation.CodeError + "\n" + string.Join("\n",
                        compilation.Diagnostics?.Select(diagnostic => $"Line {diagnostic.Line}: {diagnostic.Message}") ?? []));
                MyTools.Store.Save(definition);
                MyTools.Presentation.Save(name, share.ApplyTo(MyTools.Presentation.Get(name, ToolArguments.Title(share.Parameters))));
                MyToolsRibbon.Refresh();
                ActivityRecorder.RecordSystemNotice($"Imported saved tool {share.Label} ({definition.Revision[..12]}).");
                importedName = name;
                revision = definition.Revision;
                dialog.DialogResult = true;
            }
            catch (Exception exception) { error.Text = exception.Message; }
        };
        dialog.Content = root;
        dialog.Loaded += (_, _) => cancel.Focus();
        Show(dialog);
        return importedName is null ? (null, "Import canceled.") : (importedName, $"Imported {share.Label} ({revision![..12]}). The first run asks for review.");
    }
}
