using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Autodesk.Revit.UI;
using RevitModelMcp.Core.Control;
using RevitModelMcp.Core.Models;
using RevitModelMcp.Output;
using RevitModelMcp.Policy;
using RevitModelMcp.Ui.Kit;
using ComboBox = System.Windows.Controls.ComboBox;
using TextBox = System.Windows.Controls.TextBox;
using TextElement = System.Windows.Documents.TextElement;
using Visibility = System.Windows.Visibility;

namespace RevitModelMcp.Control;

internal sealed record RibbonToolApproval(string Revision, string Binding, Action<CommandResponse<ActionResultData>> Completed);

internal static class RibbonToolRunner
{
    private static readonly ToolRibbonSession Session = new();

    internal static void Run(UIApplication application, string name, Window? owner = null)
    {
        try
        {
            ActionCommandExecutor.ReadOnlyLock.Refresh();
            var refusal = ToolRibbonSession.Refusal(ActionCommandExecutor.ReadOnlyMode,
                PolicyProvider.ReadOnlyMessage, PolicyProvider.Refusal("run-tool"));
            if (refusal is not null) throw new InvalidOperationException(refusal);
            if (application.ActiveUIDocument is null) throw new InvalidOperationException("No active Revit document.");
            var tool = MyTools.Store.Get(name);
            var presentation = MyTools.Presentation.Get(tool.Name, ToolArguments.Title(tool.Parameters));
            if (Session.RequiresReview(tool.ApprovalBinding(), presentation.TransactionMode))
            {
                var year = int.Parse(application.Application.VersionNumber, CultureInfo.InvariantCulture);
                if (!Review(tool, presentation, year, owner, application.MainWindowHandle)) return;
                Session.Confirm(tool.ApprovalBinding(), presentation.TransactionMode);
            }
            var form = new ToolParameterForm(tool.Parameters);
            if (form.Fields.Count != 0)
            {
                var parameters = new ToolParametersWindow(tool, form, (arguments, preview, transaction, formOwner) =>
                    Execute(application, tool, arguments, preview, transaction, formOwner));
                if (owner is not null) parameters.Owner = owner;
                else new WindowInteropHelper(parameters).Owner = application.MainWindowHandle;
                parameters.ShowDialog();
                return;
            }
            var transactionMode = presentation.TransactionMode;
            if (tool.Mode == "action" && !KitDialog.Show($"Run {presentation.Label}?",
                    "This tool changes the active model.", "Run tool", owner, ownerHandle: application.MainWindowHandle)) return;
            Execute(application, tool, "{}", false, transactionMode, owner);
        }
        catch (Exception exception)
        {
            KitDialog.Inform("Saved tool refused", exception.Message, owner, application.MainWindowHandle);
        }
    }

    private static void Execute(UIApplication application, ToolDefinition tool, string arguments, bool preview, string transaction, Window? owner)
    {
        ToolArguments.Validate(tool.Parameters, arguments);
        var presentation = MyTools.Presentation.Get(tool.Name, ToolArguments.Title(tool.Parameters));
        MyTools.Presentation.Save(tool.Name, presentation with
        {
            LastArguments = arguments,
            TransactionMode = transaction
        });
        var job = ActionJobParser.Parse("run-tool", new ControlJobContract
        {
            Name = tool.Name,
            ToolArgs = arguments,
            DryRun = preview,
            Transaction = transaction,
            ClientName = "Ribbon"
        });
        job.ClientName = "Ribbon";
        CommandResponse<ActionResultData>? response = null;
        var approval = new RibbonToolApproval(tool.Revision, tool.ApprovalBinding(), result => response = result);
        var delivery = ResponseDelivery.Current;
        var cursor = Mouse.OverrideCursor;
        var status = new Window
        {
            Title = "Running saved tool",
            Icon = AppDialog.LoadAppIcon(),
            Width = 380,
            SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Margin = new Thickness(20),
                Children =
                {
                    new TextBlock { Text = $"Running {presentation.Label}…", FontSize = 13, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap },
                    new TextBlock { Text = "Revit works on this until it finishes.", Margin = new Thickness(0, 8, 0, 0) }
                }
            }
        };
        if (owner is not null) status.Owner = owner;
        else new WindowInteropHelper(status).Owner = application.MainWindowHandle;
        RevitModelMcp.Ui.Kit.KitTheme.Apply(status);
        ((TextBlock)((StackPanel)status.Content).Children[1]).SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        try
        {
            status.Show();
            Mouse.OverrideCursor = Cursors.Wait;
            status.Dispatcher.Invoke(DispatcherPriority.Render, new Action(() => { }));
            ResponseDelivery.Current = _ => { };
            ActionCommandExecutor.ReadOnlyLock.Refresh();
            ActionCommandExecutor.Execute(application, job, DateTimeOffset.Now, approval);
        }
        finally
        {
            ResponseDelivery.Current = delivery;
            Mouse.OverrideCursor = cursor;
            status.Close();
        }
        if (response is null) throw new InvalidOperationException("The execution pipeline did not return a result.");
        ShowResult(response, tool.Name, presentation.Label, preview, owner);
    }

    private static void Identify(DependencyObject control, string id, string name)
    {
        AutomationProperties.SetAutomationId(control, id);
        AutomationProperties.SetName(control, name);
    }

    private static bool Review(ToolDefinition tool, ToolPresentation presentation, int year, Window? owner, IntPtr ownerHandle)
    {
        var dialog = new Window
        {
            Title = "Review saved tool",
            Icon = AppDialog.LoadAppIcon(),
            Width = 480,
            SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
        if (owner is not null) dialog.Owner = owner;
        else new WindowInteropHelper(dialog).Owner = ownerHandle;
        KitTheme.Apply(dialog);
        TextBlock Text(string text, string token = "Text", double size = 12.5)
        {
            var block = new TextBlock { Text = text, FontSize = size, TextWrapping = TextWrapping.Wrap };
            block.SetResourceReference(TextBlock.ForegroundProperty, token);
            return block;
        }
        var root = new DockPanel { Margin = new Thickness(16) };
        var footer = new DockPanel { Margin = new Thickness(0, 16, 0, 0), LastChildFill = false };
        var code = new Button { Content = "View code" };
        Identify(code, "Review.ViewCode", "View code");
        var refusal = PolicyProvider.Refusal("get-tool");
        code.IsEnabled = refusal is null; code.ToolTip = refusal; ToolTipService.SetShowOnDisabled(code, true);
        code.Click += (_, _) =>
        {
            if (PolicyProvider.Refusal("get-tool") is string denied) { KitDialog.Inform("Saved tool refused", denied, dialog); return; }
            new ToolCodeWindow(tool, presentation.Label) { Owner = dialog }.ShowDialog();
        };
        DockPanel.SetDock(code, Dock.Left); footer.Children.Add(code);
        var actions = new StackPanel { Orientation = Orientation.Horizontal };
        var cancel = new Button { Content = "Cancel", IsCancel = true, IsDefault = true };
        var approve = new Button { Content = "Approve and run", Style = (Style)dialog.FindResource("PrimaryButton"), Margin = new Thickness(8, 0, 0, 0) };
        Identify(cancel, "Review.Cancel", "Cancel"); Identify(approve, "Review.Approve", "Approve and run");
        approve.Click += (_, _) => dialog.DialogResult = true;
        actions.Children.Add(cancel); actions.Children.Add(approve); DockPanel.SetDock(actions, Dock.Right); footer.Children.Add(actions);
        DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);
        var content = new StackPanel();
        var headline = Text(presentation.Label, size: 13); headline.FontWeight = FontWeights.SemiBold; content.Children.Add(headline);
        var name = Text(tool.Name, "Muted", 11.5); name.SetResourceReference(TextBlock.FontFamilyProperty, "MonoFont"); content.Children.Add(name);
        var firstRun = Text("First run of this revision in this Revit session.", "Muted"); firstRun.Margin = new Thickness(0, 12, 0, 12); content.Children.Add(firstRun);
        var facts = new System.Windows.Controls.Grid();
        facts.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(96) });
        facts.ColumnDefinitions.Add(new ColumnDefinition());
        void Fact(string caption, string value, bool mono = false)
        {
            var index = facts.RowDefinitions.Count; facts.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var key = Text(caption, "Muted", 11.5); key.Margin = new Thickness(0, 0, 8, 6);
            var detail = Text(value); detail.Margin = new Thickness(0, 0, 0, 6);
            if (mono) { detail.FontSize = 11.5; detail.SetResourceReference(TextBlock.FontFamilyProperty, "MonoFont"); }
            System.Windows.Controls.Grid.SetRow(key, index); System.Windows.Controls.Grid.SetRow(detail, index); System.Windows.Controls.Grid.SetColumn(detail, 1);
            facts.Children.Add(key); facts.Children.Add(detail);
        }
        Fact("Mode", tool.Mode == "action" ? "Changes the model" : "Read only");
        Fact("Saved", DateTimeOffset.TryParse(tool.Created, CultureInfo.InvariantCulture, DateTimeStyles.None, out var saved) ? saved.LocalDateTime.ToString("dd MMM yyyy", CultureInfo.GetCultureInfo("en")) : tool.Created);
        Fact("Revision", tool.Revision[..Math.Min(12, tool.Revision.Length)], true);
        if (!string.IsNullOrEmpty(tool.ReplacedRevision)) Fact("Replaces", tool.ReplacedRevision[..Math.Min(12, tool.ReplacedRevision.Length)], true);
        Fact("Tested in", tool.TestedRevitYears.Length == 0 ? "Not recorded" : "Revit " + string.Join(", ", tool.TestedRevitYears));
        content.Children.Add(facts);
        if (!tool.TestedRevitYears.Contains(year)) content.Children.Add(Text($"This tool has not been tested in Revit {year}.", "Warning"));
        var warning = Text("Saved C# has full Revit API access and your file and network rights. Read mode and Preview are not a sandbox. Read the code before you approve.");
        warning.Margin = new Thickness(0, 12, 0, 0); content.Children.Add(warning);
        root.Children.Add(content); dialog.Content = root; dialog.Loaded += (_, _) => cancel.Focus();
        return dialog.ShowDialog() == true;
    }

    private static void ShowResult(CommandResponse<ActionResultData> response, string name, string label, bool preview, Window? owner)
    {
        var value = CodeResultLimiter.ToJson(response.Data?.ReturnValue);
        var full = $"{(response.Success ? "Success" : "Failure")}\n{response.Data?.Summary ?? response.Error ?? response.Message}\n{response.Error}\n\nReturned value:\n{value}";
        if (response.Data?.Log is not null) full += "\n\nLog:\n" + string.Join("\n", response.Data.Log);
        var window = new Window
        {
            Title = label,
            Icon = AppDialog.LoadAppIcon(),
            Width = 560,
            SizeToContent = SizeToContent.Height,
            MaxHeight = SystemParameters.WorkArea.Height * 0.8,
            MinHeight = 180,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
        if (owner is not null) window.Owner = owner;
        else new WindowInteropHelper(window).Owner = Autodesk.Windows.ComponentManager.ApplicationWindow;
        KitTheme.Apply(window);
        var root = new DockPanel { Margin = new Thickness(16) };
        var footer = new DockPanel { LastChildFill = false, Margin = new Thickness(0, 16, 0, 0) };
        var copy = new Button { Content = "Copy result" }; Identify(copy, "Result.Copy", "Copy result");
        var close = new Button { Content = "Close", IsCancel = true, IsDefault = true }; Identify(close, "Result.Close", "Close");
        DockPanel.SetDock(copy, Dock.Left); DockPanel.SetDock(close, Dock.Right); footer.Children.Add(copy); footer.Children.Add(close);
        DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);
        var content = new StackPanel();
        var headline = new TextBlock { Text = !response.Success ? "Failed" : preview ? "Preview finished. Nothing was changed." : "Done", FontSize = 13, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
        headline.SetResourceReference(TextBlock.ForegroundProperty, response.Success ? "Text" : "Error"); Identify(headline, "Result.Headline", "Result");
        content.Children.Add(headline);
        var summary = response.Data?.Summary ?? response.Error ?? response.Message;
        var prefix = name + ": ";
        var displaySummary = summary is not null && summary.StartsWith(prefix, StringComparison.Ordinal) ? summary[prefix.Length..] : summary;
        var summaryBlock = new TextBlock
        {
            Text = displaySummary,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 12, 0, 0)
        };
        Identify(summaryBlock, "Result.Summary", "Summary"); content.Children.Add(summaryBlock);
        if (!response.Success && !string.IsNullOrEmpty(response.Error) && response.Error != summary)
            content.Children.Add(new TextBlock { Text = response.Error, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) });
        void Value(string caption, string text, string id, double height)
        {
            var heading = new TextBlock { Text = caption, FontSize = 11.5, Margin = new Thickness(0, 12, 0, 4) };
            heading.SetResourceReference(TextBlock.ForegroundProperty, "Muted"); content.Children.Add(heading);
            var box = new TextBox { Text = text.Length > 16000 ? text[..16000] + "\n[Display truncated. Copy result for the returned text.]" : text, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = height, FontSize = 11.5 };
            box.SetResourceReference(TextBox.FontFamilyProperty, "MonoFont"); Identify(box, id, caption); content.Children.Add(box);
        }
        if (response.Data?.ReturnValue is not null) Value("Returned value", value, "Result.Value", 260);
        if (response.Data?.Log is not null && response.Data.Log.Any()) Value("Log", string.Join("\n", response.Data.Log), "Result.Log", 160);
        var notice = new TextBlock { Visibility = Visibility.Collapsed, Margin = new Thickness(0, 8, 0, 0), TextWrapping = TextWrapping.Wrap };
        notice.SetResourceReference(TextBlock.ForegroundProperty, "Muted"); content.Children.Add(notice);
        copy.Click += (_, _) =>
        {
            try { Clipboard.SetText(full); notice.Text = "Copied."; }
            catch (System.Runtime.InteropServices.COMException) { notice.Text = "The clipboard is busy. Try again."; }
            notice.Visibility = Visibility.Visible;
        };
        close.Click += (_, _) => window.Close();
        root.Children.Add(new ScrollViewer { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        window.Content = root; window.ShowDialog();
    }
}

internal sealed class ToolParametersWindow : Window
{
    internal ToolParametersWindow(ToolDefinition tool, ToolParameterForm model, Action<string, bool, string, Window> execute)
    {
        var presentation = MyTools.Presentation.Get(tool.Name, ToolArguments.Title(tool.Parameters));
        Title = "Run " + presentation.Label;
        Icon = AppDialog.LoadAppIcon();
        Width = 520;
        MaxHeight = SystemParameters.WorkArea.Height * 0.85;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        RevitModelMcp.Ui.Kit.KitTheme.Apply(this);
        var root = new DockPanel { Margin = new Thickness(16) };
        var content = new StackPanel();
        if (!string.IsNullOrEmpty(tool.Description))
        {
            var description = new TextBlock { Text = tool.Description, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) };
            description.SetResourceReference(TextBlock.ForegroundProperty, "Muted"); content.Children.Add(description);
        }
        var controls = new Dictionary<string, FrameworkElement>();
        IReadOnlyDictionary<string, object> previous;
        try { previous = ToolArguments.Validate(tool.Parameters, presentation.LastArguments); }
        catch (Exception exception) when (exception is ArgumentException or System.Xml.XmlException) { previous = new Dictionary<string, object>(); }
        foreach (var field in model.Fields)
        {
            var booleanField = field.Type == "boolean" && field.Choices.Count == 0;
            var caption = new TextBlock
            {
                FontSize = 12.5,
                FontWeight = booleanField ? FontWeights.Normal : FontWeights.SemiBold,
                Margin = booleanField ? new Thickness(0) : new Thickness(0, controls.Count == 0 ? 0 : 12, 0, 4),
                TextWrapping = TextWrapping.Wrap
            };
            caption.SetResourceReference(TextBlock.ForegroundProperty, "Text");
            caption.Inlines.Add(new Run(field.Label));
            if (!field.Required)
            {
                var optional = new Run(" (optional)")
                {
                    FontWeight = FontWeights.Normal
                };
                optional.SetResourceReference(TextElement.ForegroundProperty, "Muted");
                caption.Inlines.Add(optional);
            }
            if (!booleanField) content.Children.Add(caption);
            previous.TryGetValue(field.Name, out var saved);
            FrameworkElement input;
            if (field.Choices.Count != 0)
            {
                var combo = new ComboBox
                {
                    ItemsSource = field.Choices,
                    MinHeight = 28
                };
                combo.SelectedItem = field.Choices.FirstOrDefault(choice => Convert.ToString(choice, CultureInfo.InvariantCulture) == Convert.ToString(saved, CultureInfo.InvariantCulture));
                input = combo;
            }
            else if (booleanField) input = new CheckBox
            {
                Content = caption,
                FontSize = 12.5,
                Margin = new Thickness(0, controls.Count == 0 ? 0 : 12, 0, 0),
                IsThreeState = !field.Required,
                IsChecked = saved is bool boolean ? boolean : field.Required ? false : null
            };
            else input = new TextBox
            {
                Text = Convert.ToString(saved, CultureInfo.InvariantCulture) ?? "",
                MinHeight = 28,
                MaxLength = field.MaxLength ?? 0
            };
            input.ToolTip = field.Description;
            System.Windows.Automation.AutomationProperties.SetName(input, field.Label);
            AutomationProperties.SetAutomationId(input, "Run.Field." + field.Name);
            controls.Add(field.Name, input);
            content.Children.Add(input);
            var hintText = string.Join(" ", new[]
            {
                field.Description,
                field.Minimum is null ? "" : $"Minimum: {field.Minimum}.",
                field.Maximum is null ? "" : $"Maximum: {field.Maximum}."
            }.Where(part => !string.IsNullOrWhiteSpace(part)));
            if (hintText.Length != 0)
            {
                var hint = new TextBlock
                {
                    Text = hintText,
                    FontSize = 11.5,
                    Margin = new Thickness(0, 4, 0, 0),
                    TextWrapping = TextWrapping.Wrap
                };
                hint.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
                content.Children.Add(hint);
            }
        }
        if (tool.Mode == "action")
        {
            var warning = new TextBlock { Text = "This tool changes the model. Preview runs it and rolls the changes back.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 0) };
            warning.SetResourceReference(TextBlock.ForegroundProperty, "Muted"); content.Children.Add(warning);
        }
        var error = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0), Visibility = Visibility.Collapsed };
        AutomationProperties.SetAutomationId(error, "Run.Error"); AutomationProperties.SetName(error, "Error");
        error.SetResourceReference(TextBlock.ForegroundProperty, "Error"); content.Children.Add(error);
        var footer = new DockPanel { LastChildFill = false, Margin = new Thickness(0, 16, 0, 0) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        void ClearError()
        {
            error.Text = "";
            error.Visibility = Visibility.Collapsed;
        }
        foreach (var input in controls.Values)
        {
            switch (input)
            {
                case TextBox text:
                    text.TextChanged += (_, _) => ClearError();
                    break;
                case ComboBox combo:
                    combo.SelectionChanged += (_, _) => ClearError();
                    break;
                case CheckBox check:
                    check.Checked += (_, _) => ClearError();
                    check.Unchecked += (_, _) => ClearError();
                    check.Indeterminate += (_, _) => ClearError();
                    break;
            }
        }
        void Run(bool preview)
        {
            ClearError();
            try
            {
                var values = controls.ToDictionary(pair => pair.Key, pair => pair.Value switch
                {
                    ComboBox combo => combo.SelectedItem is null ? null : Convert.ToString(combo.SelectedItem, CultureInfo.InvariantCulture),
                    CheckBox check => check.IsChecked?.ToString(),
                    TextBox text => text.Text,
                    _ => null
                });
                var arguments = model.Validate(values);
                IsEnabled = false;
                try { execute(arguments, preview, presentation.TransactionMode, this); }
                finally { IsEnabled = true; }
                if (!preview) Close();
            }
            catch (Exception exception)
            {
                error.Text = exception.Message.StartsWith("args.", StringComparison.Ordinal)
                    ? exception.Message[5..]
                    : exception.Message;
                error.Visibility = Visibility.Visible;
            }
        }
        if (tool.Mode == "action")
        {
            var preview = new Button { Content = "Preview" };
            AutomationProperties.SetAutomationId(preview, "Run.Preview"); AutomationProperties.SetName(preview, "Preview");
            preview.Click += (_, _) => Run(true);
            DockPanel.SetDock(preview, Dock.Left); footer.Children.Add(preview);
        }
        var cancel = new Button { Content = "Cancel", IsCancel = true };
        AutomationProperties.SetAutomationId(cancel, "Run.Cancel"); AutomationProperties.SetName(cancel, "Cancel");
        cancel.Click += (_, _) => Close(); buttons.Children.Add(cancel);
        var run = new Button { Content = "Run", IsDefault = tool.Mode == "read", Style = (Style)FindResource("PrimaryButton"), Margin = new Thickness(8, 0, 0, 0) };
        AutomationProperties.SetAutomationId(run, "Run.Run"); AutomationProperties.SetName(run, "Run");
        run.Click += (_, _) => Run(false); buttons.Children.Add(run);
        DockPanel.SetDock(buttons, Dock.Right); footer.Children.Add(buttons);
        DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);
        root.Children.Add(new ScrollViewer { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        Content = root;
    }
}
