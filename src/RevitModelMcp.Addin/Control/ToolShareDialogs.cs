using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Microsoft.Win32;
using RevitModelMcp.Activity;
using RevitModelMcp.Core.Control;
using RevitModelMcp.Policy;
using RevitModelMcp.Ui.Kit;
using Grid = System.Windows.Controls.Grid;
using TextBox = System.Windows.Controls.TextBox;
using Visibility = System.Windows.Visibility;

namespace RevitModelMcp.Control;

internal static class ToolShareDialogs
{
    private static Window Dialog(Window owner, string title, double width, double height)
    {
        var dialog = new Window
        {
            Owner = owner,
            Title = title,
            Icon = AppDialog.LoadAppIcon(),
            Width = width,
            Height = height,
            MinWidth = Math.Min(width, 500),
            MinHeight = 360,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
        KitTheme.Apply(dialog);
        return dialog;
    }

    private static void Identify(DependencyObject control, string id, string name)
    {
        AutomationProperties.SetAutomationId(control, id);
        AutomationProperties.SetName(control, name);
    }

    private static TextBlock Text(string text, bool muted = false)
    {
        var block = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
        block.SetResourceReference(TextBlock.ForegroundProperty, muted ? "Muted" : "Text");
        return block;
    }

    private static DockPanel SelectionHeader(CheckBox selectAll, TextBlock count)
    {
        var header = new DockPanel { Margin = new Thickness(0, 12, 0, 8) };
        DockPanel.SetDock(count, Dock.Right); header.Children.Add(count); header.Children.Add(selectAll);
        return header;
    }

    private static StackPanel Footer(Window dialog, DockPanel root, Button cancel, Button action, string? warning = null, string? hint = null)
    {
        var footer = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
        if (warning is not null) footer.Children.Add(Warning(dialog, warning));
        if (hint is not null) footer.Children.Add(Text(hint, true));
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        action.Margin = new Thickness(8, 0, 0, 0);
        buttons.Children.Add(cancel); buttons.Children.Add(action); footer.Children.Add(buttons);
        DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);
        return footer;
    }

    private static Grid Warning(Window dialog, string text)
    {
        var line = new Grid();
        line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
        line.ColumnDefinitions.Add(new ColumnDefinition());
        line.Children.Add(new System.Windows.Shapes.Path
        {
            Data = (Geometry)dialog.FindResource("Icon.Warning"),
            Style = (Style)dialog.FindResource("KitGlyph"),
            Stroke = (Brush)dialog.FindResource("Warning"),
            VerticalAlignment = VerticalAlignment.Top
        });
        var message = Text(text);
        Grid.SetColumn(message, 1); line.Children.Add(message);
        return line;
    }

    private static string CountAction(string action, int count, bool ellipsis = false) =>
        (count == 1 ? $"{action} tool" : $"{action} {count} tools") + (ellipsis ? "…" : "");

    internal static string Export(UIApplication application, Window owner, string? name = null)
    {
        if (PolicyProvider.Refusal("get-tool") is string refusal) throw new InvalidOperationException(refusal);
        var shares = MyTools.Store.List().Select(tool => ToolShare.From(tool, MyTools.Presentation.Get(tool.Name, ToolArguments.Title(tool.Parameters))))
            .OrderBy(share => share.Label, StringComparer.CurrentCultureIgnoreCase).ToArray();
        var dialog = Dialog(owner, "Export tools", 560, 560);
        var root = new DockPanel { Margin = new Thickness(16) };
        var action = new Button { Style = (Style)dialog.FindResource("PrimaryButton"), IsDefault = true };
        var cancel = new Button { Content = "Cancel", IsCancel = true };
        Identify(action, "Export.Confirm", "Export tools"); Identify(cancel, "Export.Cancel", "Cancel");
        Footer(dialog, root, cancel, action, hint: "Remembered inputs, ribbon order, author, dates, history and approval data are never in the file.");
        var all = new CheckBox { Content = "Select all", IsThreeState = false };
        Identify(all, "Export.SelectAll", "Select all");
        var count = Text("", true);
        var header = SelectionHeader(all, count); DockPanel.SetDock(header, Dock.Top); root.Children.Add(header);
        var rows = new StackPanel();
        var checks = new List<(ToolShare Share, CheckBox Check)>();
        var updating = false;
        var mixed = false;
        void Update()
        {
            if (updating) return;
            updating = true;
            var selected = checks.Count(entry => entry.Check.IsChecked == true);
            mixed = selected > 0 && selected < checks.Count;
            all.IsChecked = selected == 0 ? false : selected == checks.Count ? true : null;
            count.Text = $"{selected} selected";
            action.Content = CountAction("Export", selected, true); action.IsEnabled = selected != 0;
            AutomationProperties.SetName(action, action.Content.ToString());
            updating = false;
        }
        foreach (var share in shares)
        {
            var line = new Grid { Height = 36 };
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(240) });
            line.ColumnDefinitions.Add(new ColumnDefinition());
            var check = new CheckBox
            {
                IsChecked = name is null || name == share.Name,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Left
            };
            Identify(check, $"Export.Row.{share.Name}.Check", "Export " + share.Label);
            line.Children.Add(check);
            var label = Text(share.Label); label.FontSize = 13; label.FontWeight = FontWeights.SemiBold; label.TextWrapping = TextWrapping.NoWrap; label.TextTrimming = TextTrimming.CharacterEllipsis;
            Grid.SetColumn(label, 1); line.Children.Add(label);
            var description = Text(share.Description, true);
            description.TextWrapping = TextWrapping.NoWrap; description.TextTrimming = TextTrimming.CharacterEllipsis;
            description.Margin = new Thickness(8, 0, 8, 0);
            Grid.SetColumn(description, 2); line.Children.Add(description);
            checks.Add((share, check));
            check.Checked += (_, _) => Update();
            check.Unchecked += (_, _) => Update();
            rows.Children.Add(new Border { Style = (Style)dialog.FindResource("RowHover"), Child = line, ToolTip = share.Name });
        }
        void SelectAllChanged(object sender, RoutedEventArgs args)
        {
            if (updating) return;
            var selected = all.IsChecked == true || mixed;
            updating = true;
            try
            {
                foreach (var entry in checks) entry.Check.IsChecked = selected;
            }
            finally { updating = false; }
            Update();
        }
        all.Checked += SelectAllChanged;
        all.Unchecked += SelectAllChanged;
        action.Click += (_, _) => dialog.DialogResult = true;
        root.Children.Add(new ScrollViewer { Content = rows, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        dialog.Content = root; Update();
        if (dialog.ShowDialog() != true) return "Export canceled.";
        if (PolicyProvider.Refusal("get-tool") is string exportRefusal) throw new InvalidOperationException(exportRefusal);
        var selectedShares = checks.Where(entry => entry.Check.IsChecked == true).Select(entry => entry.Share).ToArray();
        var findings = ToolShare.FindPrivateDataBatch(selectedShares, LocalTerms(application));
        if (findings.Any(result => result.Incomplete || result.Data?.TotalCount > 0) && !ConfirmExport(owner, findings)) return "Export canceled.";
        var single = selectedShares.Length == 1;
        var picker = new SaveFileDialog
        {
            FileName = single ? selectedShares[0].Name + ".mcptool.json" : "model-mcp-tools.mcptools.json",
            Filter = single ? "Model MCP tool (*.mcptool.json)|*.mcptool.json" : "Model MCP tools (*.mcptools.json)|*.mcptools.json",
            OverwritePrompt = true
        };
        if (picker.ShowDialog(owner) != true) return "Export canceled.";
        if (PolicyProvider.Refusal("get-tool") is string writeRefusal) throw new InvalidOperationException(writeRefusal);
        ToolStore.AtomicWrite(picker.FileName, single ? selectedShares[0].ToFileText() : ToolShare.ToPackText(selectedShares));
        ActivityRecorder.RecordSystemNotice(single ? $"Exported saved tool {selectedShares[0].Label}." : $"Exported {selectedShares.Length} saved tools.");
        return $"Exported {selectedShares.Length} {(single ? "tool" : "tools")} to {Path.GetFileName(picker.FileName)}.";
    }

    private static List<(string Kind, string Term)> LocalTerms(UIApplication application)
    {
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
        return terms;
    }

    private static bool ConfirmExport(Window owner, IReadOnlyList<ToolShare.BatchPrivateData> results)
    {
        var dialog = Dialog(owner, "Check before sharing", 600, 520);
        var root = new DockPanel { Margin = new Thickness(16) };
        var cancel = new Button { Content = "Cancel", IsCancel = true, IsDefault = true };
        var export = new Button { Content = "Export anyway", Style = (Style)dialog.FindResource("PrimaryButton") };
        Identify(cancel, "Share.Cancel", "Cancel"); Identify(export, "Share.ExportAnyway", "Export anyway");
        Footer(dialog, root, cancel, export);
        var content = new StackPanel();
        var found = results.Where(result => result.Data?.TotalCount > 0).ToArray();
        var incomplete = results.Count(result => result.Incomplete);
        var summary = $"{found.Sum(result => result.Data!.TotalCount)} references in {found.Length} tools may contain private data.";
        if (incomplete != 0) summary += $" {incomplete} tools were not scanned. Read their text yourself.";
        content.Children.Add(Warning(dialog, summary));
        foreach (var result in results.Where(result => result.Incomplete || result.Data?.TotalCount > 0))
        {
            content.Children.Add(new TextBlock { Text = result.Share.Label, FontSize = 13, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 16, 0, 4) });
            if (result.Data is ToolShare.PrivateData data)
            {
                foreach (var finding in data.Findings)
                    content.Children.Add(Text($"{finding.Kind} · {finding.Field} · {finding.Excerpt}", true));
                if (data.TotalCount > data.Findings.Count) content.Children.Add(Text($"and {data.TotalCount - data.Findings.Count} more", true));
            }
            if (result.Incomplete) content.Children.Add(Text("Not scanned completely. Read the tool text yourself.", true));
        }
        var hint = Text("The scan is a heuristic. Read the tool text before sharing.", true); hint.Margin = new Thickness(0, 16, 0, 0); content.Children.Add(hint);
        export.Click += (_, _) => dialog.DialogResult = true;
        root.Children.Add(new ScrollViewer { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        dialog.Content = root; dialog.Loaded += (_, _) => cancel.Focus();
        return dialog.ShowDialog() == true;
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

    private sealed class ImportRow(ToolShare? share, string name, string? refusal)
    {
        internal ToolShare? Share { get; } = share;
        internal string Name { get; set; } = name;
        internal string Id { get; } = name.Replace("entry ", "entry-");
        internal string? Refusal { get; } = refusal;
        internal string? Error { get; set; }
        internal string State { get; set; } = "";
        internal string? ReviewedRevision { get; set; }
        internal CheckBox Check { get; } = new();
        internal TextBlock Status { get; } = Text("");
        internal TextBlock DetailError { get; } = Text("");
        internal StackPanel Details { get; } = new() { Margin = new Thickness(28, 4, 0, 12), Visibility = Visibility.Collapsed };
        internal TextBox? NameInput { get; set; }
        internal bool Eligible => Refusal is null && Error is null && State is "New" or "Replaces existing";
    }

    internal static (string? Name, string Status) Import(UIApplication application, Window owner)
    {
        ImportGate();
        var picker = new OpenFileDialog { Multiselect = true, Filter = "Model MCP tools (*.mcptool.json;*.mcptools.json;*.json)|*.mcptool.json;*.mcptools.json;*.json" };
        if (picker.ShowDialog(owner) != true) return (null, "Import canceled.");
        var entries = new List<ImportRow>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in picker.FileNames)
        {
            try
            {
                if (new FileInfo(file).Length > ToolShare.MaxPackBytes) throw new ArgumentException("Tool file exceeds 16 MB.");
                foreach (var entry in ToolShare.ParseFile(File.ReadAllBytes(file)))
                {
                    var reason = entry.Refusal;
                    if (!names.Add(entry.Name)) reason = "Duplicate of another selected tool.";
                    if (entry.Share is ToolShare share && reason is null && share.Icon16.Length != 0)
                    {
                        try { ToolIcons.Decode(share.Icon16, 16); ToolIcons.Decode(share.Icon32, 32); }
                        catch (Exception exception) { reason = exception.Message; }
                    }
                    entries.Add(new ImportRow(entry.Share, entry.Name, reason));
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
            {
                entries.Add(new ImportRow(null, Path.GetFileName(file), exception.Message));
            }
            var countRefusal = ToolShare.ImportCountRefusal(entries.Count);
            if (countRefusal is not null) return (null, countRefusal);
        }
        var dialog = Dialog(owner, "Import tools", 720, 620);
        var root = new DockPanel { Margin = new Thickness(16) };
        var action = new Button { Style = (Style)dialog.FindResource("PrimaryButton"), IsDefault = true };
        var cancel = new Button { Content = "Cancel" };
        Identify(action, "Import.Confirm", "Import tools"); Identify(cancel, "Import.Cancel", "Cancel");
        var footer = Footer(dialog, root, cancel, action, warning: "C# tools can access files, the network and your Revit model. Import only code you have read or that comes from an author you trust.");
        var replacementWarning = Text("");
        replacementWarning.SetResourceReference(TextBlock.ForegroundProperty, "Warning");
        replacementWarning.Margin = new Thickness(0, 8, 0, 0);
        replacementWarning.Visibility = Visibility.Collapsed;
        footer.Children.Insert(footer.Children.Count - 1, replacementWarning);
        var title = Text($"{entries.Count} {(entries.Count == 1 ? "tool" : "tools")} from {(picker.FileNames.Length == 1 ? Path.GetFileName(picker.FileName) : $"{picker.FileNames.Length} files")}");
        DockPanel.SetDock(title, Dock.Top); root.Children.Add(title);
        var all = new CheckBox { Content = "Select all", IsThreeState = false };
        Identify(all, "Import.SelectAll", "Select all");
        var count = Text("", true);
        var header = SelectionHeader(all, count); DockPanel.SetDock(header, Dock.Top); root.Children.Add(header);
        var rows = new StackPanel();
        var busy = false;
        var finished = false;
        var stopping = false;
        var updating = false;
        var mixed = false;
        var imported = new List<(ImportRow Row, ToolDefinition Tool)>();
        void UpdateHeader()
        {
            if (updating) return;
            updating = true;
            var eligible = entries.Where(entry => entry.Eligible).ToArray();
            var selected = eligible.Count(entry => entry.Check.IsChecked == true);
            mixed = selected > 0 && selected < eligible.Length;
            all.IsChecked = selected == 0 ? false : selected == eligible.Length ? true : null;
            all.IsEnabled = !busy && !finished && eligible.Length != 0;
            count.Text = $"{selected} selected";
            var replacements = eligible.Count(entry => entry.Check.IsChecked == true && entry.State == "Replaces existing");
            replacementWarning.Text = replacements == 1
                ? "1 selected tool replaces an installed tool."
                : $"{replacements} selected tools replace installed tools.";
            replacementWarning.Visibility = replacements == 0 ? Visibility.Collapsed : Visibility.Visible;
            if (!busy && !finished) { action.Content = CountAction("Import", selected); action.IsEnabled = selected != 0; }
            AutomationProperties.SetName(action, action.Content?.ToString() ?? "Import tools");
            updating = false;
        }
        void UpdateState(ImportRow entry, bool defaults, HashSet<string> invalidNames)
        {
            var oldState = entry.State;
            entry.Error = entry.Refusal;
            entry.ReviewedRevision = null;
            if (entry.Refusal is null && entry.Share is ToolShare share)
            {
                try
                {
                    ToolStore.ValidateName(entry.Name);
                    if (entries.Any(other => other != entry && other.Refusal is null && other.Name == entry.Name))
                        throw new ArgumentException("Duplicate of another selected tool.");
                    var previous = Installed(entry.Name);
                    entry.ReviewedRevision = previous?.Revision;
                    entry.State = (share with { Name = entry.Name }).ImportState(previous,
                        previous is null ? null : MyTools.Presentation.Get(previous.Name, ToolArguments.Title(previous.Parameters)),
                        invalidNames.Contains(entry.Name));
                }
                catch (Exception exception) when (exception is ArgumentException or InvalidOperationException) { entry.Error = exception.Message; }
            }
            if (entry.Error is not null) entry.State = "Cannot import";
            entry.Status.Text = entry.State;
            entry.Status.SetResourceReference(TextBlock.ForegroundProperty, entry.State == "Cannot import" ? "Error" : entry.State == "Replaces existing" ? "Warning" : "Muted");
            entry.DetailError.Text = entry.Error ?? "";
            entry.DetailError.SetResourceReference(TextBlock.ForegroundProperty, "Error");
            entry.Check.IsEnabled = entry.Eligible && !busy && !finished;
            if (!entry.Eligible) entry.Check.IsChecked = false;
            else if (defaults || entry.State != oldState) entry.Check.IsChecked = entry.State == "New";
        }
        void UpdateStates(bool defaults = false)
        {
            updating = true;
            try
            {
                MyTools.Store.List(out var invalid);
                var invalidNames = new HashSet<string>(invalid.Select(tool => tool.Name), StringComparer.Ordinal);
                foreach (var entry in entries) UpdateState(entry, defaults, invalidNames);
            }
            finally { updating = false; }
            UpdateHeader();
        }
        foreach (var entry in entries)
        {
            var body = new StackPanel();
            var line = new Grid { Height = 36 };
            foreach (var width in new[] { new GridLength(28), new GridLength(240), new GridLength(1, GridUnitType.Star), new GridLength(130) })
                line.ColumnDefinitions.Add(new ColumnDefinition { Width = width });
            entry.Check.VerticalAlignment = VerticalAlignment.Center;
            entry.Check.HorizontalAlignment = HorizontalAlignment.Left;
            Identify(entry.Check, $"Import.Row.{entry.Id}.Check", "Import " + (entry.Share?.Label ?? entry.Name));
            entry.Check.Checked += (_, _) => UpdateHeader();
            entry.Check.Unchecked += (_, _) => UpdateHeader();
            line.Children.Add(entry.Check);
            var label = Text(entry.Share?.Label ?? entry.Name);
            label.FontSize = 13; label.FontWeight = FontWeights.SemiBold; label.TextWrapping = TextWrapping.NoWrap; label.TextTrimming = TextTrimming.CharacterEllipsis;
            var expand = new Button { Content = label, Background = Brushes.Transparent, HorizontalContentAlignment = HorizontalAlignment.Stretch, Padding = new Thickness(0), Style = (Style)dialog.FindResource("QuietButton") };
            Identify(expand, $"Import.Row.{entry.Id}", "Review " + (entry.Share?.Label ?? entry.Name));
            void ToggleDetails()
            {
                var show = entry.Details.Visibility != Visibility.Visible;
                foreach (var other in entries) other.Details.Visibility = Visibility.Collapsed;
                entry.Details.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            }
            expand.Click += (_, _) => ToggleDetails();
            line.Background = Brushes.Transparent;
            line.MouseLeftButtonUp += (_, _) => ToggleDetails();
            Grid.SetColumn(expand, 1); line.Children.Add(expand);
            var description = Text(entry.Share?.Description ?? "", true); description.TextWrapping = TextWrapping.NoWrap; description.TextTrimming = TextTrimming.CharacterEllipsis; description.Margin = new Thickness(8, 0, 8, 0);
            Grid.SetColumn(description, 2); line.Children.Add(description);
            entry.Status.HorizontalAlignment = HorizontalAlignment.Right;
            Grid.SetColumn(entry.Status, 3); line.Children.Add(entry.Status);
            body.Children.Add(new Border { Style = (Style)dialog.FindResource("RowHover"), Child = line });
            entry.Details.Children.Add(entry.DetailError);
            if (entry.Refusal is null && entry.Share is ToolShare share)
            {
                entry.Details.Children.Add(Text("Tool name", true));
                var input = new TextBox { Text = entry.Name, MaxLength = 48, FontFamily = (FontFamily)dialog.FindResource("MonoFont") };
                entry.NameInput = input; Identify(input, $"Import.Row.{entry.Id}.NameBox", "Tool name");
                input.TextChanged += (_, _) => { entry.Name = input.Text; UpdateStates(); };
                entry.Details.Children.Add(input);
                entry.Details.Children.Add(Text(share.Description));
                var meta = $"{(share.Mode == "read" ? "Read only" : "Changes the model")} · Tested in Revit {string.Join(", ", share.TestedRevitYears)}";
                if (share.Transaction == "group") meta += " · Manages its own transactions";
                entry.Details.Children.Add(Text(meta, true));
                foreach (var field in new ToolParameterForm(share.Parameters).Fields)
                {
                    var parameter = Text("");
                    parameter.Inlines.Add(new Run(field.Label));
                    var descriptionSuffix = string.IsNullOrWhiteSpace(field.Description) ? "" : " · " + field.Description;
                    var requirement = new Run($" {(field.Required ? "required" : "optional")}{descriptionSuffix}");
                    requirement.SetResourceReference(System.Windows.Documents.TextElement.ForegroundProperty, "Muted");
                    parameter.Inlines.Add(requirement);
                    entry.Details.Children.Add(parameter);
                }
                var source = new TextBox { Text = share.Code, IsReadOnly = true, AcceptsReturn = true, FontFamily = (FontFamily)dialog.FindResource("MonoFont"), MaxHeight = 260, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(0, 8, 0, 8) };
                AutomationProperties.SetName(source, "C# source"); entry.Details.Children.Add(source);
                var fingerprint = share.Fingerprint();
                var fingerprintRow = new DockPanel();
                var copy = new Button { Content = "Copy", Style = (Style)dialog.FindResource("QuietButton") };
                AutomationProperties.SetName(copy, "Copy fingerprint");
                copy.Click += (_, _) => { try { Clipboard.SetText(fingerprint); } catch (Exception exception) { entry.DetailError.Text = exception.Message; } };
                DockPanel.SetDock(copy, Dock.Right); fingerprintRow.Children.Add(copy);
                var hash = Text(string.Join(" ", Enumerable.Range(0, fingerprint.Length / 4).Select(index => fingerprint.Substring(index * 4, 4))), true);
                hash.Style = (Style)dialog.FindResource("MonoText"); fingerprintRow.Children.Add(hash); entry.Details.Children.Add(fingerprintRow);
            }
            body.Children.Add(entry.Details);
            rows.Children.Add(new Border { Child = body, BorderBrush = (Brush)dialog.FindResource("Border"), BorderThickness = new Thickness(0, 0, 0, 1) });
        }
        if (entries.Count == 1) entries[0].Details.Visibility = Visibility.Visible;
        void SelectAllChanged(object sender, RoutedEventArgs args)
        {
            if (updating) return;
            var selected = all.IsChecked == true || mixed;
            updating = true;
            try
            {
                foreach (var entry in entries.Where(entry => entry.Eligible)) entry.Check.IsChecked = selected;
            }
            finally { updating = false; }
            UpdateHeader();
        }
        all.Checked += SelectAllChanged;
        all.Unchecked += SelectAllChanged;
        cancel.Click += (_, _) => { if (busy) { stopping = true; cancel.IsEnabled = false; } else dialog.Close(); };
        dialog.PreviewKeyDown += (_, args) =>
        {
            if (args.Key != Key.Escape) return;
            if (busy) { stopping = true; cancel.IsEnabled = false; } else dialog.Close();
            args.Handled = true;
        };
        dialog.Closing += (_, args) => { if (busy) { stopping = true; args.Cancel = true; } };
        action.Click += async (_, _) =>
        {
            if (finished) { dialog.Close(); return; }
            var selected = entries.Where(entry => entry.Eligible && entry.Check.IsChecked == true).ToArray();
            if (selected.Length == 0 || busy) return;
            busy = true; stopping = false;
            action.IsEnabled = false; cancel.Content = "Stop";
            foreach (var entry in entries) { entry.Check.IsEnabled = false; if (entry.NameInput is not null) entry.NameInput.IsEnabled = false; }
            UpdateHeader();
            var failed = false;
            for (var index = 0; index < selected.Length; index++)
            {
                if (stopping) break;
                var entry = selected[index];
                action.Content = $"Importing {index + 1} of {selected.Length}…";
                await Dispatcher.Yield(DispatcherPriority.Background);
                if (stopping) break;
                try
                {
                    var definition = ImportOne(application, entry);
                    imported.Add((entry, definition));
                    entry.State = "Imported"; entry.Status.Text = entry.State; entry.Status.SetResourceReference(TextBlock.ForegroundProperty, "Success");
                }
                catch (Exception exception)
                {
                    failed = true; entry.State = "Failed"; entry.Status.Text = entry.State; entry.Status.SetResourceReference(TextBlock.ForegroundProperty, "Error");
                    foreach (var other in entries) other.Details.Visibility = Visibility.Collapsed;
                    entry.DetailError.Text = exception.Message; entry.Details.Visibility = Visibility.Visible;
                }
                UpdateHeader();
            }
            MyToolsRibbon.Refresh();
            if (imported.Count == 1) ActivityRecorder.RecordSystemNotice($"Imported saved tool {imported[0].Row.Share!.Label} ({imported[0].Tool.Revision[..12]}).");
            else if (imported.Count > 1) ActivityRecorder.RecordSystemNotice($"Imported {imported.Count} saved tools.");
            busy = false; finished = true;
            if (!failed && !stopping) { dialog.DialogResult = true; return; }
            action.Content = "Close"; AutomationProperties.SetName(action, "Close"); action.IsEnabled = true;
            cancel.Content = "Cancel"; cancel.IsEnabled = true;
        };
        root.Children.Add(new ScrollViewer { Content = rows, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        dialog.Content = root; UpdateStates(defaults: true);
        dialog.ShowDialog();
        if (imported.Count == 0) return (null, finished ? "No tools were imported." : "Import canceled.");
        return (imported[0].Tool.Name, imported.Count == 1
            ? $"Imported {imported[0].Row.Share!.Label} ({imported[0].Tool.Revision[..12]}). The first run asks for review."
            : $"Imported {imported.Count} tools. The first run of each tool asks for review.");
    }

    private static ToolDefinition ImportOne(UIApplication application, ImportRow entry)
    {
        ImportGate();
        ToolStore.ValidateName(entry.Name);
        var share = entry.Share ?? throw new InvalidOperationException("Missing tool content.");
        var previous = Installed(entry.Name);
        if (previous?.Revision != entry.ReviewedRevision) throw new InvalidOperationException("The installed tool changed during review.");
        if (share.Icon16.Length != 0) { ToolIcons.Decode(share.Icon16, 16); ToolIcons.Decode(share.Icon32, 32); }
        var definition = share.ToDefinition(entry.Name, "import", DateTimeOffset.UtcNow.ToString("O"), previous?.Revision ?? "");
        var job = ActionJobParser.Parse("save-tool", new ControlJobContract { Name = entry.Name, Code = definition.Code, Transaction = share.Transaction });
        var action = job.Action ?? throw new InvalidOperationException("Missing tool arguments.");
        action.Code = definition.Code; action.ToolRevision = definition.Revision;
        var compilation = CodeExecution.Execute(application, null, null, action, new ActionCommandExecutor.ActionFailures(), "import", job.JobId, compileOnly: true);
        if (compilation.CodeError is not null)
            throw new ArgumentException(compilation.CodeError + "\n" + string.Join("\n", compilation.Diagnostics?.Select(diagnostic => $"Line {diagnostic.Line}: {diagnostic.Message}") ?? []));
        ImportGate();
        if (Installed(entry.Name)?.Revision != entry.ReviewedRevision) throw new InvalidOperationException("The installed tool changed during review.");
        var tools = MyTools.Store.List();
        var slots = ToolRibbonSlots.Assign(tools.Select(tool => tool.Name), name =>
        {
            var tool = tools.First(candidate => candidate.Name == name);
            return MyTools.Presentation.Get(name, ToolArguments.Title(tool.Parameters));
        });
        var presentation = share.ApplyTo(MyTools.Presentation.Get(entry.Name, ToolArguments.Title(share.Parameters)));
        if (previous is null)
            presentation = presentation with
            {
                Hidden = slots.Count >= ToolRibbonSlots.Capacity,
                Order = tools.Select(tool => MyTools.Presentation.Get(tool.Name, ToolArguments.Title(tool.Parameters)).Order).DefaultIfEmpty(-1).Max() + 1
            };
        MyTools.Store.Save(definition);
        MyTools.Presentation.Save(entry.Name, presentation);
        return definition;
    }
}
