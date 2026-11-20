using System.Drawing;
using System.Windows.Forms;
using RevitModelMcp.Core.Connect;

namespace RevitModelMcp.ConnectSetup;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        try
        {
            var server = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "server", McpServerSpec.ExecutableName));
            if (!File.Exists(server)) throw new IOException("The bundled server is missing. Repair the Model MCP installation.");
            var connection = new InstallerConnect(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                McpServerSpec.ForExecutable(server), Environment.GetEnvironmentVariable("PATH"));
            Application.Run(new ConnectWindow(connection));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            MessageBox.Show("AI apps were not connected. " + exception.Message, "Model MCP", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}

internal sealed class ConnectWindow : Form
{
    public ConnectWindow(InstallerConnect connection)
    {
        Text = "Model MCP: Connect AI apps";
        Font = SystemFonts.MessageBoxFont;
        AutoScaleMode = AutoScaleMode.Font;
        ClientSize = new Size(840, 490);
        MinimumSize = new Size(720, 450);
        StartPosition = FormStartPosition.CenterScreen;
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(20), RowCount = 6, ColumnCount = 1 };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(new Label { Text = "Choose the apps to connect", AutoSize = true, Font = new Font(Font, FontStyle.Bold), Margin = new Padding(0, 0, 0, 12) });
        layout.Controls.Add(new Label { Text = "Nothing is selected by default. Only checked apps will be changed.", AutoSize = true, Margin = new Padding(0, 0, 0, 8) });
        layout.Controls.Add(new Label { Text = "Model MCP adds one entry to the app's settings; you can remove it later in Revit under Connect AI app", AutoSize = true, MaximumSize = new Size(790, 0), Margin = new Padding(0, 0, 0, 12) });
        var apps = new ListView { Dock = DockStyle.Fill, View = View.Details, CheckBoxes = true, FullRowSelect = true, HideSelection = false, ShowItemToolTips = true, AccessibleName = "Detected AI apps" };
        apps.Columns.Add("AI app", 240);
        apps.Columns.Add("Result", 520);
        foreach (var target in connection.Targets) apps.Items.Add(new ListViewItem(new[] { target.DisplayName, "Not selected" }) { Tag = target });
        if (connection.ClaudeExecutable is not null) apps.Items.Add(new ListViewItem(new[] { "Claude Code (user scope)", "Not selected" }));
        layout.Controls.Add(apps);
        layout.Controls.Add(new Label
        {
            Text = apps.Items.Count == 0
                ? "No supported apps detected. Install an app, then use Connect AI app in Revit."
                : "Restart connected apps. Then open Revit and ask the app to call revit_ping and describe the open model.",
            AutoSize = true,
            MaximumSize = new Size(790, 0),
            Margin = new Padding(0, 12, 0, 12)
        });
        var buttons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft };
        var close = new Button { Text = "Close", AutoSize = true, DialogResult = DialogResult.Cancel };
        var connect = new Button { Text = "Connect selected apps", AutoSize = true, Enabled = apps.Items.Count > 0 };
        buttons.Controls.Add(close);
        buttons.Controls.Add(connect);
        layout.Controls.Add(buttons);
        Controls.Add(layout);
        CancelButton = close;
        AcceptButton = connect;
        var busy = false;
        FormClosing += (_, eventArgs) => eventArgs.Cancel = busy;
        connect.Click += async (_, _) =>
        {
            var selected = apps.CheckedItems.Cast<ListViewItem>().ToArray();
            if (selected.Length == 0) return;
            busy = true;
            connect.Enabled = close.Enabled = apps.Enabled = false;
            try
            {
                foreach (var item in selected)
                {
                    item.SubItems[1].Text = "Connecting...";
                    var result = await Task.Run(() => item.Tag is McpClientTarget target ? connection.Connect(target) : connection.ConnectClaudeCode());
                    item.SubItems[1].Text = result;
                    item.ToolTipText = result;
                    item.Checked = false;
                }
            }
            finally
            {
                busy = false;
                connect.Enabled = close.Enabled = apps.Enabled = true;
            }
        };
    }
}
