using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Autodesk.Windows;
using RevitModelMcp.Activity;
using RevitModelMcp.Core.Control;

namespace RevitModelMcp.Control;

internal sealed class ReadOnlyRibbon : ICommand, IDisposable
{
    private readonly RibbonToggleButton _button;
    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;

    internal ReadOnlyRibbon()
    {
        _button = new RibbonToggleButton
        {
            Id = "ModelMcpReadOnly",
            Text = "Read-only",
            ShowText = true,
            ShowImage = true,
            ShowToolTipOnDisabled = true,
            Size = RibbonItemSize.Large,
            Orientation = System.Windows.Controls.Orientation.Vertical,
            CommandHandler = this
        };
        var tab = ComponentManager.Ribbon.Tabs.First(tab => tab.Title == RibbonText.TabName);
        var panel = tab.Panels.First(panel => panel.Source.Title == RibbonText.TabName);
        var activityIndex = panel.Source.Items.ToList().FindIndex(item => item.Text == PaneText.RibbonButton);
        panel.Source.Items.Insert(activityIndex >= 0 ? activityIndex + 1 : panel.Source.Items.Count, _button);
        ActionCommandExecutor.ReadOnlyLock.Changed += OnChanged;
        PaneTheme.Changed += OnThemeChanged;
        Update(ActionCommandExecutor.ReadOnlyLock.State);
    }

    public event EventHandler? CanExecuteChanged { add { } remove { } }

    public bool CanExecute(object? parameter) => ActionCommandExecutor.ReadOnlyLock.Source != "policy";

    public void Execute(object? parameter)
    {
        try
        {
            var enabled = !ActionCommandExecutor.ReadOnlyLock.IsLocked;
            ActionCommandExecutor.ReadOnlyLock.Set(enabled, "ribbon");
            ActivityRecorder.RecordSystemNotice(enabled
                ? "Read-only turned on for this Windows user from the ribbon."
                : "Read-only turned off for this Windows user from the ribbon.");
        }
        catch (Exception exception)
        {
            ActivityRecorder.RecordSystemNotice(exception.Message, failed: true);
        }
        Update(ActionCommandExecutor.ReadOnlyLock.State);
    }

    private void OnChanged(ReadOnlyLockState state) => _dispatcher.BeginInvoke(new Action(() => Update(ActionCommandExecutor.ReadOnlyLock.State)));
    private void OnThemeChanged() => Update(ActionCommandExecutor.ReadOnlyLock.State);

    private void Update(ReadOnlyLockState state)
    {
        _button.IsChecked = state.IsLocked;
        _button.IsEnabled = state.Source != "policy";
        var shortText = state.Source == "policy" ? "Read-only is enforced by workstation policy."
            : state.IsLocked ? "Read-only is on. Click to allow changes." : "Read-only is off. Click to block changes.";
        var suffix = PaneTheme.IsDark ? "-dark" : string.Empty;
        _button.ToolTip = new RibbonToolTip
        {
            Title = "Read-only",
            Content = shortText,
            ExpandedContent = "One workstation lock applies to all running Revit versions for this Windows user. " +
                              "While locked, Model MCP can read models but cannot change them. " +
                              "Turning off read-only from chat requires your explicit confirmation. " +
                              "Workstation policy cannot be overridden from the ribbon or chat.",
            ExpandedImage = new BitmapImage(new Uri($"pack://application:,,,/RevitModelMcp;component/Resources/Icons/ReadOnlyTooltip{suffix}.png"))
        };
        var icon = state.IsLocked ? "Locked" : "Unlocked";
        _button.Image = new BitmapImage(new Uri($"pack://application:,,,/RevitModelMcp;component/Resources/Icons/{icon}16{suffix}.png"));
        _button.LargeImage = new BitmapImage(new Uri($"pack://application:,,,/RevitModelMcp;component/Resources/Icons/{icon}32{suffix}.png"));
    }

    public void Dispose()
    {
        ActionCommandExecutor.ReadOnlyLock.Changed -= OnChanged;
        PaneTheme.Changed -= OnThemeChanged;
    }
}
