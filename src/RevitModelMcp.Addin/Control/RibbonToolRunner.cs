using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using Autodesk.Revit.UI;
using RevitModelMcp.Core.Control;
using RevitModelMcp.Core.Models;
using RevitModelMcp.Output;
using RevitModelMcp.Policy;
using ComboBox = System.Windows.Controls.ComboBox;
using TextBox = System.Windows.Controls.TextBox;

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
            if (Session.RequiresReview(tool.Name, tool.Revision))
            {
                var year = int.Parse(application.Application.VersionNumber, CultureInfo.InvariantCulture);
                var warning = tool.TestedRevitYears.Contains(year) ? "" : $"\n\nWARNING: this tool has not been tested in Revit {year}.";
                var replaced = string.IsNullOrEmpty(tool.ReplacedRevision) ? "None" : tool.ReplacedRevision[..12];
                if (!AppDialog.Show("Review saved tool",
                        $"Tool: {tool.Name}\nMode: {tool.Mode}\nRevision: {tool.Revision[..12]}\nSaved: {tool.Created}\nReplaced revision: {replaced}\nTested Revit years: {(tool.TestedRevitYears.Length == 0 ? "None" : string.Join(", ", tool.TestedRevitYears))}{warning}\n\nSaved C# has full Revit API access and your file and network rights. Read mode and Preview are not a sandbox. Review the source in Settings before approving. Approve this revision for this Revit session?",
                        confirm: true, owner: owner)) return;
                Session.Confirm(tool.Name, tool.Revision);
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
            var transactionMode = MyTools.Presentation.Get(name).TransactionMode;
            if (tool.Mode == "action" && !AppDialog.Show("Run saved tool",
                    $"Run {name} and change the active model?\nTransaction mode: {transactionMode}.", confirm: true, owner: owner)) return;
            Execute(application, tool, "{}", false, transactionMode, owner);
        }
        catch (Exception exception)
        {
            AppDialog.Show("Saved tool refused", exception.Message, owner: owner);
        }
    }

    private static void Execute(UIApplication application, ToolDefinition tool, string arguments, bool preview, string transaction, Window? owner)
    {
        ToolArguments.Validate(tool.Parameters, arguments);
        var presentation = MyTools.Presentation.Get(tool.Name);
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
            Content = new TextBlock
            {
                Text = $"Running {tool.Name}. Please wait for the result. Revit model operations run on the main thread.",
                Margin = new Thickness(20),
                TextWrapping = TextWrapping.Wrap
            }
        };
        if (owner is not null) status.Owner = owner;
        else new WindowInteropHelper(status).Owner = application.MainWindowHandle;
        MyToolsWindow.FollowTheme(status);
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
        ShowResult(response, owner);
    }

    private static void ShowResult(CommandResponse<ActionResultData> response, Window? owner)
    {
        var value = CodeResultLimiter.ToJson(response.Data?.ReturnValue);
        var full = $"{(response.Success ? "Success" : "Failure")}\n{response.Data?.Summary ?? response.Error ?? response.Message}\n{response.Error}\n\nReturned value:\n{value}";
        if (response.Data?.Log is not null) full += "\n\nLog:\n" + string.Join("\n", response.Data.Log);
        var window = new Window
        {
            Title = "Saved tool result",
            Icon = AppDialog.LoadAppIcon(),
            Width = 650,
            Height = 460,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
        if (owner is not null) window.Owner = owner;
        else new WindowInteropHelper(window).Owner = Autodesk.Windows.ComponentManager.ApplicationWindow;
        var root = new DockPanel { Margin = new Thickness(16) };
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        var copy = new Button
        {
            Content = "Copy result",
            MinWidth = 100,
            Margin = new Thickness(0, 8, 8, 0)
        };
        copy.Click += (_, _) =>
        {
            try { Clipboard.SetText(full); }
            catch (System.Runtime.InteropServices.COMException) { AppDialog.Show("Copy result", "The clipboard is busy. Try again.", owner: window); }
        };
        var close = new Button
        {
            Content = "Close",
            IsCancel = true,
            IsDefault = true,
            MinWidth = 80,
            Margin = new Thickness(0, 8, 0, 0)
        };
        close.Click += (_, _) => window.Close();
        buttons.Children.Add(copy);
        buttons.Children.Add(close);
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(buttons);
        root.Children.Add(new TextBox
        {
            IsReadOnly = true,
            Text = full.Length > 16000 ? full[..16000] + "\n[Display truncated. Copy result for the returned text.]" : full,
            TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        });
        window.Content = root;
        MyToolsWindow.FollowTheme(window);
        window.ShowDialog();
    }
}

internal sealed class ToolParametersWindow : Window
{
    internal ToolParametersWindow(ToolDefinition tool, ToolParameterForm model, Action<string, bool, string, Window> execute)
    {
        Title = "Run " + tool.Name;
        Icon = AppDialog.LoadAppIcon();
        Width = 520;
        MaxHeight = SystemParameters.WorkArea.Height * 0.85;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        MyToolsWindow.FollowTheme(this);
        var root = new StackPanel { Margin = new Thickness(16) };
        var controls = new Dictionary<string, FrameworkElement>();
        IReadOnlyDictionary<string, object> previous;
        try { previous = ToolArguments.Validate(tool.Parameters, MyTools.Presentation.Get(tool.Name).LastArguments); }
        catch (ArgumentException) { previous = new Dictionary<string, object>(); }
        foreach (var field in model.Fields)
        {
            root.Children.Add(new TextBlock
            {
                Text = field.Name + (field.Required ? " *" : ""),
                Margin = new Thickness(0, 8, 0, 4)
            });
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
            else if (field.Type == "boolean") input = new CheckBox
            {
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
            System.Windows.Automation.AutomationProperties.SetName(input, field.Name);
            controls.Add(field.Name, input);
            root.Children.Add(input);
            root.Children.Add(new TextBlock
            {
                Text = field.Description + (field.Minimum is null ? "" : $" Minimum: {field.Minimum}.") + (field.Maximum is null ? "" : $" Maximum: {field.Maximum}."),
                TextWrapping = TextWrapping.Wrap
            });
        }
        root.Children.Add(new TextBlock
        {
            Text = "Transaction mode",
            Margin = new Thickness(0, 12, 0, 4)
        });
        var transaction = new ComboBox
        {
            ItemsSource = new[] { "auto", "group" },
            SelectedItem = MyTools.Presentation.Get(tool.Name).TransactionMode
        };
        root.Children.Add(transaction);
        var error = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 8, 0, 8)
        };
        root.Children.Add(error);
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        void Run(bool preview)
        {
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
                try { execute(arguments, preview, (string)transaction.SelectedItem, this); }
                finally { IsEnabled = true; }
                if (!preview) Close();
            }
            catch (Exception exception) { error.Text = exception.Message; }
        }
        if (tool.Mode == "action")
        {
            var preview = new Button
            {
                Content = "Preview",
                MinWidth = 80,
                Margin = new Thickness(0, 0, 8, 0)
            };
            preview.Click += (_, _) => Run(true);
            buttons.Children.Add(preview);
        }
        var run = new Button
        {
            Content = tool.Mode == "action" ? "Run (change model)" : "Run",
            MinWidth = 100,
            Margin = new Thickness(0, 0, 8, 0)
        };
        run.Click += (_, _) => Run(false);
        buttons.Children.Add(run);
        var cancel = new Button
        {
            Content = "Cancel",
            IsCancel = true,
            IsDefault = true,
            MinWidth = 80
        };
        cancel.Click += (_, _) => Close();
        buttons.Children.Add(cancel);
        root.Children.Add(buttons);
        Content = new ScrollViewer
        {
            Content = root,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        };
    }
}
